// The assistant runner of the end-to-end stack (ADR 0035): the real runner (sessions, policy, store, HTTP API) with a scripted model instead of a CLI. The "model" is
// deterministic and uses Mnemarr's real MCP server with the token that the runner is given, so the whole chain is the real one: the panel, the proxy, the token, the tools and the
// audit. It does what the message says: "add <text> to <source>" reads the sources, proposes a note (a card that waits for the person) and, once approved, creates it.

import { mkdir } from 'node:fs/promises';

import type {
  AdapterEvent,
  AgentAdapter,
  AgentProcess,
  PermissionDecision,
  StartOptions,
} from '../app/src/adapters/adapter.ts';
import { loadConfig } from '../app/src/config.ts';
import { McpClient, readEndpoint } from '../app/src/mcpClient.ts';
import { createRunnerServer } from '../app/src/server.ts';
import { SessionManager } from '../app/src/sessions.ts';
import { ConversationStore } from '../app/src/store.ts';

class ScriptedProcess implements AgentProcess {
  private readonly waiting = new Map<string, (decision: PermissionDecision) => void>();
  private readonly mcp: McpClient;
  private readonly options: StartOptions;

  constructor(options: StartOptions) {
    this.options = options;
    this.mcp = new McpClient(readEndpoint(options.mcpConfigFile));
    queueMicrotask(() => options.onEvent({ type: 'session', agentSessionId: options.sessionId }));
  }

  send(text: string): void {
    void this.turn(text);
  }

  answer(requestId: string, decision: PermissionDecision): void {
    this.waiting.get(requestId)?.(decision);
    this.waiting.delete(requestId);
  }

  interrupt(): void {
    for (const [requestId, resolve] of this.waiting) {
      this.waiting.delete(requestId);
      resolve({ allow: false, message: 'Stopped.' });
    }
  }

  close(): void {
    this.options.onExit(0);
  }

  kill(): void {
    this.options.onExit(null);
  }

  private emit(event: AdapterEvent): void {
    this.options.onEvent(event);
  }

  private async tool(
    id: string,
    name: string,
    input: unknown,
  ): Promise<{ isError: boolean; text: string } | undefined> {
    const toolName = `mcp__mnemarr__${name}`;
    this.emit({ type: 'tool-call', id, name: toolName, input });
    const decision = await new Promise<PermissionDecision>((resolve) => {
      this.waiting.set(id, resolve);
      this.emit({ type: 'permission-request', requestId: id, toolName, input });
    });
    if (!decision.allow) {
      this.emit({ type: 'tool-result', id, isError: true, summary: decision.message });
      return undefined;
    }
    const result = await this.mcp.callTool(name, decision.input);
    this.emit({ type: 'tool-result', id, isError: result.isError, summary: result.text.slice(0, 300) });
    return result;
  }

  private async turn(text: string): Promise<void> {
    try {
      const sources = await this.tool('call-1', 'list_sources', {});
      const wanted = /\bto (\S+)/.exec(text)?.[1];
      if (!sources || !wanted) {
        this.emit({
          type: 'text',
          text: 'I looked at your sources. Tell me which one to add to: "add <text> to <source>".',
        });
        return this.end(false);
      }
      const what = /^add (.+) to \S+/i.exec(text)?.[1] ?? text;
      // The path comes from the words, so that the same request made twice in one stack does not meet its own note.
      const path = `${what
        .toLowerCase()
        .replaceAll(/[^a-z0-9]+/g, '-')
        .replaceAll(/^-|-$/g, '')}.md`;
      const created = await this.tool('call-2', 'create_note', {
        source: wanted,
        path,
        content: `# From the chat\n\n${what}\n`,
      });
      this.emit({
        type: 'text',
        text: created && !created.isError ? `Done: I added it to ${wanted}.` : 'I did not change anything.',
      });
      this.end(false);
    } catch (error) {
      this.emit({ type: 'error', message: error instanceof Error ? error.message : String(error) });
      this.end(true);
    }
  }

  private end(isError: boolean): void {
    this.emit({
      type: 'turn-end',
      isError,
      usage: { inputTokens: 1, outputTokens: 1, cacheReadTokens: 0, cacheWriteTokens: 0 },
    });
  }
}

const adapter: AgentAdapter = {
  id: 'claude-code',
  name: 'Scripted model',
  models: [{ id: 'sonnet', label: 'Sonnet' }],
  defaultModel: 'sonnet',
  available: () => true,
  credentialsHint: 'nothing to set: this one is scripted',
  start: (options) => new ScriptedProcess(options),
};

const config = loadConfig(process.env);
await mkdir(config.dataDir, { recursive: true });
const store = new ConversationStore(config.dataDir);
await store.init();
const manager = new SessionManager([adapter], store, {
  maxSessions: config.maxSessions,
  maxConversations: config.maxConversations,
  idleTimeoutMs: config.idleTimeoutMs,
  workDir: config.dataDir,
  runDir: config.dataDir,
  mnemarrMcpUrl: config.mnemarrMcpUrl,
});
await manager.init();
createRunnerServer(manager, config.secret, (message) => process.stdout.write(`${message}\n`)).listen(
  config.port,
  config.host,
);
