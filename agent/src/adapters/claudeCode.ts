// Claude Code in its headless, structured mode (ADR 0035): `claude -p` reading and writing stream-json, one process per
// active conversation. Permission prompts come over the same pipes (`--permission-prompt-tool stdio`, the control
// protocol the Agent SDK uses): a `control_request` of subtype `can_use_tool` on stdout, answered by a
// `control_response` on stdin. No PTY, no screen scraping.

import { type ChildProcessWithoutNullStreams, spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';

import { summarize, type Usage } from '../events.ts';
import { knownReadTools } from '../policy.ts';
import { systemPrompt } from '../prompt.ts';
import type {
  AdapterEvent,
  AgentAdapter,
  AgentProcess,
  ModelOption,
  PermissionDecision,
  StartOptions,
} from './adapter.ts';

/** Built-in tools refused on top of `--tools ""` (defence in depth: the CLI must have no shell, files or web). */
const disallowedBuiltIns = [
  'Bash',
  'BashOutput',
  'KillShell',
  'Edit',
  'MultiEdit',
  'Write',
  'Read',
  'Glob',
  'Grep',
  'NotebookEdit',
  'WebFetch',
  'WebSearch',
  'Task',
  'TodoWrite',
];

/** The command line of one process. Exported for the tests: it is the security boundary of the CLI. */
export function buildArgs(options: Pick<StartOptions, 'model' | 'resume' | 'sessionId' | 'mcpConfigFile'>): string[] {
  return [
    '-p',
    '--input-format',
    'stream-json',
    '--output-format',
    'stream-json',
    '--verbose',
    '--include-partial-messages',
    '--model',
    options.model,
    ...(options.resume ? ['--resume', options.resume] : ['--session-id', options.sessionId]),
    // Only Mnemarr's MCP server, from our file: no user, project or plugin servers.
    '--strict-mcp-config',
    '--mcp-config',
    options.mcpConfigFile,
    // No settings files (hooks, permission rules) and no built-in tools.
    '--setting-sources',
    '',
    '--tools',
    '',
    '--disallowedTools',
    disallowedBuiltIns.join(','),
    '--allowedTools',
    knownReadTools.join(','),
    // Everything not pre-allowed is asked over stdio and decided by the runner's policy.
    '--permission-prompt-tool',
    'stdio',
    '--permission-mode',
    'default',
    '--system-prompt',
    systemPrompt,
  ];
}

interface ContentBlock {
  type: string;
  text?: string;
  thinking?: string;
  id?: string;
  name?: string;
  input?: unknown;
  tool_use_id?: string;
  content?: unknown;
  is_error?: boolean;
}

type Line = Record<string, unknown>;

function asRecord(value: unknown): Line | undefined {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? (value as Line) : undefined;
}

function blocksOf(line: Line): ContentBlock[] {
  const content = asRecord(line.message)?.content;
  return Array.isArray(content) ? (content as ContentBlock[]) : [];
}

function resultText(content: unknown): string {
  if (typeof content === 'string') {
    return content;
  }
  if (Array.isArray(content)) {
    return content
      .map((part) => (typeof asRecord(part)?.text === 'string' ? String(asRecord(part)?.text) : ''))
      .filter(Boolean)
      .join('\n');
  }
  return '';
}

function number(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : 0;
}

function usageOf(line: Line): Usage | undefined {
  const usage = asRecord(line.usage);
  if (!usage) {
    return undefined;
  }
  return {
    inputTokens: number(usage.input_tokens),
    outputTokens: number(usage.output_tokens),
    cacheReadTokens: number(usage.cache_read_input_tokens),
    cacheWriteTokens: number(usage.cache_creation_input_tokens),
    ...(typeof line.total_cost_usd === 'number' ? { costUsd: line.total_cost_usd } : {}),
    ...(typeof line.duration_ms === 'number' ? { durationMs: line.duration_ms } : {}),
  };
}

function assistantEvents(line: Line): AdapterEvent[] {
  // Sub-agent messages (parent_tool_use_id set) cannot happen without the Task tool; ignored if they ever do.
  if (line.parent_tool_use_id) {
    return [];
  }
  const events: AdapterEvent[] = [];
  for (const block of blocksOf(line)) {
    if (block.type === 'text' && block.text) {
      events.push({ type: 'text', text: block.text });
    } else if (block.type === 'thinking' && block.thinking) {
      events.push({ type: 'thinking', text: block.thinking });
    } else if (block.type === 'tool_use' && block.id && block.name) {
      events.push({ type: 'tool-call', id: block.id, name: block.name, input: block.input ?? {} });
    }
  }
  return events;
}

function userEvents(line: Line): AdapterEvent[] {
  return blocksOf(line)
    .filter((block) => block.type === 'tool_result' && block.tool_use_id)
    .map((block) => ({
      type: 'tool-result' as const,
      id: String(block.tool_use_id),
      isError: block.is_error === true,
      summary: summarize(resultText(block.content)),
    }));
}

function streamEvents(line: Line): AdapterEvent[] {
  const event = asRecord(line.event);
  const delta = asRecord(event?.delta);
  if (line.parent_tool_use_id || event?.type !== 'content_block_delta' || delta?.type !== 'text_delta') {
    return [];
  }
  return typeof delta.text === 'string' && delta.text ? [{ type: 'text-delta', text: delta.text }] : [];
}

function systemEvents(line: Record<string, unknown>): AdapterEvent[] {
  if (line.subtype !== 'init' || typeof line.session_id !== 'string') {
    return [];
  }
  return [
    {
      type: 'session',
      agentSessionId: line.session_id,
      model: typeof line.model === 'string' ? line.model : undefined,
      tools: Array.isArray(line.tools) ? line.tools.map(String) : undefined,
    },
  ];
}

function resultEvents(line: Record<string, unknown>): AdapterEvent[] {
  const isError = line.is_error === true || (typeof line.subtype === 'string' && line.subtype !== 'success');
  const events: AdapterEvent[] = [];
  if (isError && typeof line.result === 'string' && line.result) {
    events.push({ type: 'error', message: line.result });
  }
  events.push({ type: 'turn-end', isError, usage: usageOf(line) });
  return events;
}

function controlEvents(line: Record<string, unknown>): AdapterEvent[] {
  const request = asRecord(line.request);
  if (
    request?.subtype !== 'can_use_tool' ||
    typeof line.request_id !== 'string' ||
    typeof request.tool_name !== 'string'
  ) {
    return [];
  }
  return [
    {
      type: 'permission-request',
      requestId: line.request_id,
      toolName: request.tool_name,
      input: request.input ?? {},
    },
  ];
}

/**
 * One line of Claude Code's stream-json output → the events it carries. Pure, so it is tested on recorded
 * fixtures. Control requests other than `can_use_tool` are not chat events: see {@link ClaudeCodeProcess}.
 */
export function parseLine(raw: string): AdapterEvent[] {
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return [];
  }
  return eventsOf(parsed);
}

/** The events of a line that was parsed already (the process parses each line once). */
function eventsOf(parsed: unknown): AdapterEvent[] {
  const line = asRecord(parsed);
  if (!line) {
    return [];
  }

  switch (line.type) {
    case 'system':
      return systemEvents(line);
    case 'stream_event':
      return streamEvents(line);
    case 'assistant':
      return assistantEvents(line);
    case 'user':
      return userEvents(line);
    case 'result':
      return resultEvents(line);
    case 'control_request':
      return controlEvents(line);
    default:
      return [];
  }
}

/** The answer to a `can_use_tool` request, as the CLI expects it on stdin. */
export function permissionResponse(requestId: string, decision: PermissionDecision): string {
  const response = decision.allow
    ? { behavior: 'allow', updatedInput: decision.input ?? {} }
    : { behavior: 'deny', message: decision.message };
  return JSON.stringify({
    type: 'control_response',
    response: { subtype: 'success', request_id: requestId, response },
  });
}

export function userMessage(text: string): string {
  return JSON.stringify({ type: 'user', message: { role: 'user', content: text } });
}

/** The environment of the CLI: an allow-list, so the runner's secret and Mnemarr's key never reach it. */
export function childEnvironment(source: NodeJS.ProcessEnv, configDir: string, home: string): NodeJS.ProcessEnv {
  const passed = [
    'PATH',
    'LANG',
    'LC_ALL',
    'TZ',
    'NODE_EXTRA_CA_CERTS',
    'HTTPS_PROXY',
    'HTTP_PROXY',
    'NO_PROXY',
    'CLAUDE_CODE_OAUTH_TOKEN',
    'ANTHROPIC_API_KEY',
  ];
  const env: NodeJS.ProcessEnv = {};
  for (const name of passed) {
    if (source[name]) {
      env[name] = source[name];
    }
  }
  return {
    ...env,
    HOME: home,
    // The CLI's own state (sessions to resume, settings) lives in the runner's data, never in a user's ~/.claude.
    CLAUDE_CONFIG_DIR: configDir,
    DISABLE_AUTOUPDATER: '1',
    CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: '1',
  };
}

export class ClaudeCodeProcess implements AgentProcess {
  private buffer = '';
  private readonly child: ChildProcessWithoutNullStreams;
  private readonly options: StartOptions;

  constructor(command: string, args: string[], env: NodeJS.ProcessEnv, options: StartOptions) {
    this.options = options;
    this.child = spawn(command, args, { cwd: options.workDir, env, stdio: ['pipe', 'pipe', 'pipe'] });
    this.child.stdout.setEncoding('utf8');
    this.child.stdout.on('data', (chunk: string) => this.read(chunk));
    // stderr may echo configuration; only its first line is surfaced, and only on a failed start.
    let firstError = '';
    this.child.stderr.setEncoding('utf8');
    this.child.stderr.on('data', (chunk: string) => {
      firstError ||= chunk.split('\n')[0].slice(0, 300);
    });
    this.child.stdin.on('error', () => undefined);
    this.child.on('error', (error) =>
      options.onEvent({ type: 'error', message: `The agent CLI could not start: ${error.message}` }),
    );
    this.child.on('exit', (code) => {
      if (code && firstError) {
        options.onEvent({ type: 'error', message: `The agent CLI stopped (${code}): ${firstError}` });
      }
      options.onExit(code);
    });
  }

  send(text: string): void {
    this.write(userMessage(text));
  }

  answer(requestId: string, decision: PermissionDecision): void {
    this.write(permissionResponse(requestId, decision));
  }

  interrupt(): void {
    this.write(
      JSON.stringify({ type: 'control_request', request_id: randomUUID(), request: { subtype: 'interrupt' } }),
    );
  }

  close(): void {
    this.child.stdin.end();
  }

  kill(): void {
    this.child.kill('SIGKILL');
  }

  private write(line: string): void {
    if (this.child.stdin.writable) {
      this.child.stdin.write(`${line}\n`);
    }
  }

  private read(chunk: string): void {
    this.buffer += chunk;
    let index: number;
    while ((index = this.buffer.indexOf('\n')) >= 0) {
      const raw = this.buffer.slice(0, index).trim();
      this.buffer = this.buffer.slice(index + 1);
      if (raw) {
        this.handle(raw);
      }
    }
  }

  private handle(raw: string): void {
    let parsed: unknown;
    try {
      parsed = JSON.parse(raw);
    } catch {
      return; // Not JSON: ignored.
    }
    for (const event of eventsOf(parsed)) {
      this.options.onEvent(event);
    }
    // Control requests this runner does not serve (hooks, elicitation, MCP over the SDK) get an error answer, so the
    // CLI never waits on them.
    const line = asRecord(parsed);
    if (
      line?.type === 'control_request' &&
      asRecord(line.request)?.subtype !== 'can_use_tool' &&
      typeof line.request_id === 'string'
    ) {
      this.write(
        JSON.stringify({
          type: 'control_response',
          response: { subtype: 'error', request_id: line.request_id, error: 'Not supported by mnemarr-agent.' },
        }),
      );
    }
  }
}

export const claudeModels: readonly ModelOption[] = [
  { id: 'sonnet', label: 'Sonnet' },
  { id: 'opus', label: 'Opus' },
  { id: 'haiku', label: 'Haiku' },
];

export class ClaudeCodeAdapter implements AgentAdapter {
  readonly id = 'claude-code';
  readonly name = 'Claude Code';
  readonly models: readonly ModelOption[];
  readonly defaultModel: string;

  private readonly command: string;
  private readonly configDir: string;
  private readonly home: string;
  private readonly env: NodeJS.ProcessEnv;

  constructor(
    command: string,
    configDir: string,
    home: string,
    env: NodeJS.ProcessEnv = process.env,
    models: readonly ModelOption[] = claudeModels,
    defaultModel?: string,
  ) {
    this.models = models;
    this.defaultModel = defaultModel ?? models[0]?.id ?? 'sonnet';
    this.command = command;
    this.configDir = configDir;
    this.home = home;
    this.env = env;
  }

  readonly credentialsHint = 'set CLAUDE_CODE_OAUTH_TOKEN (the login of a Claude subscription) or ANTHROPIC_API_KEY';

  available(): boolean {
    return Boolean(this.env.CLAUDE_CODE_OAUTH_TOKEN || this.env.ANTHROPIC_API_KEY);
  }

  start(options: StartOptions): AgentProcess {
    return new ClaudeCodeProcess(
      this.command,
      buildArgs(options),
      childEnvironment(this.env, this.configDir, this.home),
      options,
    );
  }
}
