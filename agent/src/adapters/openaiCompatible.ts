// A model behind an OpenAI-compatible API (D-87): OpenAI, Azure OpenAI, a local server such as Ollama or vLLM, or anything that speaks `/chat/completions`
// with tool calls. There is no CLI to drive here, so this adapter runs the agent loop itself: it sends the conversation and Mnemarr's MCP tools to the model,
// calls the tool the model asks for, gives it the result, and goes on until the model answers in words. What it must not do is decide what a tool may do: every
// call is asked of the session manager as a `permission-request`, whose policy answers (D-88), exactly as for a CLI that asks over its control protocol.

import { randomUUID } from 'node:crypto';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

import { summarize, type Usage } from '../events.ts';
import { McpClient, type McpEndpoint, type McpTool, readEndpoint } from '../mcpClient.ts';
import { serverName } from '../policy.ts';
import { systemPrompt } from '../prompt.ts';
import type {
  AgentAdapter,
  AgentProcess,
  ModelOption,
  PermissionDecision,
  StartOptions,
} from './adapter.ts';

export interface OpenAiSettings {
  /** The API's base address, e.g. `https://api.openai.com/v1` or `http://ollama:11434/v1`. */
  baseUrl?: string;
  /** Optional: a local server needs none. Never logged, never sent anywhere but `baseUrl`. */
  apiKey?: string;
  /** What the agent is called in the panel. */
  name: string;
  models: readonly ModelOption[];
  defaultModel: string;
  /** Where the messages of a conversation are kept, so that it resumes after its process stopped. */
  stateDir: string;
  fetcher?: typeof fetch;
}

/** The most calls to the model in one turn: a model that keeps asking for tools is stopped, not followed. */
export const maxRounds = 12;

interface ToolCall {
  id: string;
  name: string;
  arguments: string;
}

type Message =
  | { role: 'system' | 'user'; content: string }
  | { role: 'assistant'; content: string | null; tool_calls?: { id: string; type: 'function'; function: { name: string; arguments: string } }[] }
  | { role: 'tool'; tool_call_id: string; content: string };

interface Round {
  text: string;
  calls: ToolCall[];
  usage: Partial<Usage>;
}

const rejectedByAbort = 'The turn was stopped.';

export class OpenAiCompatibleAdapter implements AgentAdapter {
  readonly id = 'openai';
  readonly models: readonly ModelOption[];
  readonly defaultModel: string;

  private readonly settings: OpenAiSettings;

  constructor(settings: OpenAiSettings) {
    this.settings = settings;
    this.models = settings.models;
    this.defaultModel = settings.defaultModel;
  }

  get name(): string {
    return this.settings.name;
  }

  available(): boolean {
    return Boolean(this.settings.baseUrl);
  }

  start(options: StartOptions): AgentProcess {
    return new OpenAiProcess(this.settings, options);
  }
}

class OpenAiProcess implements AgentProcess {
  private readonly messages: Message[];
  private readonly mcp: McpClient;
  private readonly stateFile: string;
  private readonly waiting = new Map<string, (decision: PermissionDecision) => void>();
  private abort: AbortController | undefined;
  private closed = false;
  private readonly settings: OpenAiSettings;
  private readonly options: StartOptions;

  constructor(settings: OpenAiSettings, options: StartOptions) {
    this.settings = settings;
    this.options = options;
    const endpoint: McpEndpoint = readEndpoint(options.mcpConfigFile);
    this.mcp = new McpClient(endpoint, settings.fetcher);
    mkdirSync(settings.stateDir, { recursive: true, mode: 0o700 });
    // A conversation is its session id: a process that starts later for the same id takes its messages back.
    const session = options.resume ?? options.sessionId;
    this.stateFile = join(settings.stateDir, `${session.replaceAll(/[^A-Za-z0-9-]/g, '')}.json`);
    this.messages = this.load();
    queueMicrotask(() => options.onEvent({ type: 'session', agentSessionId: session, model: options.model }));
  }

  send(text: string): void {
    if (this.closed) {
      return;
    }
    this.abort = new AbortController();
    this.messages.push({ role: 'user', content: text });
    void this.turn(this.abort.signal);
  }

  answer(requestId: string, decision: PermissionDecision): void {
    const resolve = this.waiting.get(requestId);
    this.waiting.delete(requestId);
    resolve?.(decision);
  }

  interrupt(): void {
    this.abort?.abort();
    for (const [requestId, resolve] of this.waiting) {
      this.waiting.delete(requestId);
      resolve({ allow: false, message: rejectedByAbort });
    }
  }

  close(): void {
    this.finish(0);
  }

  kill(): void {
    this.interrupt();
    this.finish(null);
  }

  private finish(code: number | null): void {
    if (!this.closed) {
      this.closed = true;
      this.options.onExit(code);
    }
  }

  private async turn(signal: AbortSignal): Promise<void> {
    const started = Date.now();
    const usage: Usage = { inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0 };
    try {
      const tools = await this.mcp.listTools(signal);
      for (let round = 0; round < maxRounds; round += 1) {
        const answer = await this.complete(tools, signal);
        usage.inputTokens += answer.usage.inputTokens ?? 0;
        usage.outputTokens += answer.usage.outputTokens ?? 0;
        if (answer.text) {
          this.options.onEvent({ type: 'text', text: answer.text });
        }
        this.messages.push({
          role: 'assistant',
          content: answer.text || null,
          ...(answer.calls.length > 0
            ? {
                tool_calls: answer.calls.map((call) => ({
                  id: call.id,
                  type: 'function' as const,
                  function: { name: call.name, arguments: call.arguments },
                })),
              }
            : {}),
        });
        if (answer.calls.length === 0) {
          return this.endTurn(false, usage, started);
        }
        for (const call of answer.calls) {
          await this.useTool(call, signal);
        }
        this.save();
      }
      this.options.onEvent({ type: 'error', message: 'The model kept asking for tools: the turn was stopped.' });
      this.endTurn(true, usage, started);
    } catch (error) {
      if (signal.aborted) {
        this.endTurn(true, usage, started);
        return;
      }
      this.options.onEvent({ type: 'error', message: error instanceof Error ? error.message : String(error) });
      this.endTurn(true, usage, started);
    }
  }

  private endTurn(isError: boolean, usage: Usage, started: number): void {
    this.save();
    this.options.onEvent({ type: 'turn-end', isError, usage: { ...usage, durationMs: Date.now() - started } });
  }

  /** One call of the model, streamed: the words as they come, and the tools it asks for once it has finished. */
  private async complete(tools: McpTool[], signal: AbortSignal): Promise<Round> {
    const { baseUrl, apiKey, fetcher = fetch } = this.settings;
    const response = await fetcher(`${baseUrl!.replace(/\/+$/, '')}/chat/completions`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Accept: 'text/event-stream',
        ...(apiKey ? { Authorization: `Bearer ${apiKey}` } : {}),
      },
      body: JSON.stringify({
        model: this.options.model,
        stream: true,
        stream_options: { include_usage: true },
        messages: [{ role: 'system', content: systemPrompt }, ...this.messages],
        tools: tools.map((tool) => ({
          type: 'function',
          function: { name: tool.name, description: tool.description, parameters: tool.inputSchema },
        })),
      }),
      signal,
    });
    if (!response.ok || !response.body) {
      throw new Error(`The model's API answered ${response.status}${response.status === 401 ? ' (is the key right?)' : ''}.`);
    }
    return this.read(response.body);
  }

  private async read(body: ReadableStream<Uint8Array>): Promise<Round> {
    const round: Round = { text: '', calls: [], usage: {} };
    const decoder = new TextDecoder();
    let pending = '';
    for await (const chunk of body) {
      pending += decoder.decode(chunk, { stream: true });
      let end = pending.indexOf('\n');
      while (end >= 0) {
        this.take(pending.slice(0, end).trim(), round);
        pending = pending.slice(end + 1);
        end = pending.indexOf('\n');
      }
    }
    this.take(pending.trim(), round);
    return round;
  }

  private take(line: string, round: Round): void {
    if (!line.startsWith('data:')) {
      return;
    }
    const data = line.slice(5).trim();
    if (data === '' || data === '[DONE]') {
      return;
    }
    let parsed: {
      choices?: { delta?: { content?: string | null; tool_calls?: { index?: number; id?: string; function?: { name?: string; arguments?: string } }[] } }[];
      usage?: { prompt_tokens?: number; completion_tokens?: number };
    };
    try {
      parsed = JSON.parse(data);
    } catch {
      return;
    }
    if (parsed.usage) {
      round.usage = { inputTokens: parsed.usage.prompt_tokens, outputTokens: parsed.usage.completion_tokens };
    }
    const delta = parsed.choices?.[0]?.delta;
    if (typeof delta?.content === 'string' && delta.content !== '') {
      round.text += delta.content;
      this.options.onEvent({ type: 'text-delta', text: delta.content });
    }
    for (const piece of delta?.tool_calls ?? []) {
      const at = piece.index ?? 0;
      const call = (round.calls[at] ??= { id: piece.id ?? randomUUID(), name: '', arguments: '' });
      call.name += piece.function?.name ?? '';
      call.arguments += piece.function?.arguments ?? '';
      if (piece.id) {
        call.id = piece.id;
      }
    }
  }

  /** The tool the model asked for: shown, asked of the policy, run if it was allowed, and its result given back to the model. */
  private async useTool(call: ToolCall, signal: AbortSignal): Promise<void> {
    const toolName = `mcp__${serverName}__${call.name}`;
    let input: unknown;
    try {
      input = call.arguments.trim() === '' ? {} : JSON.parse(call.arguments);
    } catch {
      this.toolMessage(call, true, 'The arguments are not JSON.');
      return;
    }
    this.options.onEvent({ type: 'tool-call', id: call.id, name: toolName, input });
    const decision = await this.ask(call.id, toolName, input, signal);
    if (!decision.allow) {
      this.toolMessage(call, true, decision.message);
      return;
    }
    try {
      const result = await this.mcp.callTool(call.name, decision.input, signal);
      this.toolMessage(call, result.isError, result.text);
    } catch (error) {
      if (signal.aborted) {
        throw error;
      }
      this.toolMessage(call, true, error instanceof Error ? error.message : String(error));
    }
  }

  private ask(id: string, toolName: string, input: unknown, signal: AbortSignal): Promise<PermissionDecision> {
    if (signal.aborted) {
      return Promise.resolve({ allow: false, message: rejectedByAbort });
    }
    return new Promise((resolve) => {
      this.waiting.set(id, resolve);
      this.options.onEvent({ type: 'permission-request', requestId: id, toolName, input });
    });
  }

  private toolMessage(call: ToolCall, isError: boolean, text: string): void {
    this.messages.push({ role: 'tool', tool_call_id: call.id, content: text });
    this.options.onEvent({ type: 'tool-result', id: call.id, isError, summary: summarize(text) });
  }

  private load(): Message[] {
    try {
      const saved = JSON.parse(readFileSync(this.stateFile, 'utf8')) as Message[];
      return Array.isArray(saved) ? saved : [];
    } catch {
      return [];
    }
  }

  private save(): void {
    writeFileSync(this.stateFile, JSON.stringify(this.messages), { mode: 0o600 });
  }
}
