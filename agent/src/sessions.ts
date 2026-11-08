// Conversations and their CLI processes (ADR 0035): one process per active conversation, started on its first message
// (resuming the CLI session when the conversation is reopened), stopped after an idle timeout, at most
// `maxSessions` at once (the least recently used idle one makes room). Tool permissions are decided here (ADR 0035).

import { randomUUID } from 'node:crypto';
import { writeFileSync } from 'node:fs';
import { mkdir, readdir, rm } from 'node:fs/promises';
import { join } from 'node:path';

import type { AdapterEvent, AgentAdapter, AgentProcess } from './adapters/adapter.ts';
import { type ChatEvent, type ChatEventEnvelope, transientTypes } from './events.ts';
import { cancelledMessage, classify, forbiddenMessage, rejectedMessage, serverName } from './policy.ts';
import type { Conversation, ConversationStore } from './store.ts';

export class ChatError extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.status = status;
  }
}

export interface SessionOptions {
  maxSessions: number;
  /** Conversations kept; the oldest idle one is deleted to make room for a new one (default 200). */
  maxConversations?: number;
  idleTimeoutMs: number;
  /** Where the CLI runs: an empty directory. */
  workDir: string;
  /** Where the per-session MCP configuration files are written (mode 600). */
  runDir: string;
  mnemarrMcpUrl: string;
  now?: () => Date;
}

interface PendingPermission {
  requestId: string;
  toolName: string;
  input: unknown;
}

interface Active {
  process: AgentProcess;
  model: string;
  busy: boolean;
  lastActive: number;
  pending: Map<string, PendingPermission>;
  mcpConfigFile: string;
  stopping: boolean;
  startedAt: number;
}

export interface ConversationSummary {
  id: string;
  title: string;
  agent: string;
  model: string;
  createdAt: string;
  updatedAt: string;
  running: boolean;
  busy: boolean;
  pendingPermissions: number;
}

type Listener = (event: ChatEventEnvelope) => void;

const maxTitleLength = 80;
const killAfterInterruptMs = 5000;
/** A process holds the token it was started with, which Mnemarr ends 12 hours after it made it: one that is older than this is replaced at the next message. */
const maxProcessAgeMs = 11 * 60 * 60 * 1000;

function titleOf(text: string): string {
  const line = text.trim().split('\n')[0].trim();
  return line.length <= maxTitleLength ? line : `${line.slice(0, maxTitleLength - 1)}…`;
}

/** Conversations kept when the options say nothing. */
const defaultMaxConversations = 200;

export class SessionManager {
  private readonly conversations = new Map<string, Conversation>();
  private readonly active = new Map<string, Active>();
  private readonly listeners = new Map<string, Set<Listener>>();
  private readonly adapters: Map<string, AgentAdapter>;
  private readonly store: ConversationStore;
  private readonly options: SessionOptions;
  private sweeper: NodeJS.Timeout | undefined;

  constructor(adapters: AgentAdapter[], store: ConversationStore, options: SessionOptions) {
    this.adapters = new Map(adapters.map((adapter) => [adapter.id, adapter]));
    this.store = store;
    this.options = options;
  }

  async init(): Promise<void> {
    // The configuration files of processes hold a person's token and are removed when the process ends: those that a killed runner left behind go now.
    await mkdir(this.options.runDir, { recursive: true, mode: 0o700 });
    for (const name of await readdir(this.options.runDir)) {
      if (name.endsWith('.mcp.json')) {
        await rm(join(this.options.runDir, name), { force: true });
      }
    }
    for (const conversation of await this.store.loadAll()) {
      this.conversations.set(conversation.id, conversation);
    }
    this.sweeper = setInterval(() => this.sweep(), Math.min(30_000, this.options.idleTimeoutMs));
    this.sweeper.unref();
  }

  agents() {
    return [...this.adapters.values()].map((adapter) => ({
      id: adapter.id,
      name: adapter.name,
      models: adapter.models,
      defaultModel: adapter.defaultModel,
      available: adapter.available(),
    }));
  }

  list(): ConversationSummary[] {
    return [...this.conversations.values()]
      .map((conversation) => this.summary(conversation))
      .sort((a, b) => b.updatedAt.localeCompare(a.updatedAt));
  }

  get(id: string): ConversationSummary & { events: ChatEventEnvelope[] } {
    const conversation = this.require(id);
    return { ...this.summary(conversation), events: conversation.events };
  }

  async create(
    agentId: string | undefined,
    model: string | undefined,
    title: string | undefined,
  ): Promise<ConversationSummary> {
    const adapter = this.adapter(agentId ?? [...this.adapters.keys()][0]);
    const chosen = this.model(adapter, model);
    this.checkRoomForAConversation();
    const now = this.now();
    const conversation: Conversation = {
      id: randomUUID(),
      title: title ? titleOf(title) : '',
      agent: adapter.id,
      model: chosen,
      createdAt: now,
      updatedAt: now,
      events: [],
    };
    this.conversations.set(conversation.id, conversation);
    await this.store.save(conversation);
    return this.summary(conversation);
  }

  /** At the cap, nothing is deleted to make room: the conversations belong to different people, and only they decide (429). */
  private checkRoomForAConversation(): void {
    const cap = this.options.maxConversations ?? defaultMaxConversations;
    if (this.conversations.size >= cap) {
      throw new ChatError(429, `The runner keeps ${cap} conversations: delete one first.`);
    }
  }

  async delete(id: string): Promise<void> {
    this.require(id);
    this.stop(id);
    this.conversations.delete(id);
    this.listeners.delete(id);
    await this.store.remove(id);
  }

  /**
   * Sends the operator's message; the answer arrives as events. 409 while a turn runs. `mcpToken` is the token Mnemarr
   * minted for the person who sends it: a process started now uses it as its MCP key, so Mnemarr's tools run as that
   * person. There is no other key: a message without a token reaches nothing.
   */
  send(id: string, text: string, mcpToken: string, model?: string): void {
    const conversation = this.require(id);
    const adapter = this.adapter(conversation.agent);
    if (!adapter.available()) {
      throw new ChatError(503, `${adapter.name} has no credentials: ${adapter.credentialsHint}.`);
    }
    const chosen = model ? this.model(adapter, model) : conversation.model;
    let current = this.active.get(id);
    if (current?.busy) {
      throw new ChatError(409, 'A turn is running: wait for it or stop it.');
    }
    if (current && (current.model !== chosen || Date.now() - current.startedAt > maxProcessAgeMs)) {
      // Another model, or a token that is about to end: the next process resumes the same CLI session with it.
      this.stop(id);
      current = undefined;
    }
    // Spawned first: a process that cannot start (no room, no token) must not leave the model and the title changed.
    const active = current ?? this.spawn(conversation, adapter, chosen, mcpToken);
    conversation.model = chosen;
    if (!conversation.title) {
      conversation.title = titleOf(text);
    }
    active.busy = true;
    active.lastActive = Date.now();
    this.emit(id, { type: 'user', text });
    this.emit(id, { type: 'status', state: 'running' });
    active.process.send(text);
  }

  /** The operator's answer to a write: the CLI gets it, the stream says it. */
  answer(id: string, requestId: string, approve: boolean): void {
    this.require(id);
    const active = this.active.get(id);
    const pending = active?.pending.get(requestId);
    if (!active || !pending) {
      throw new ChatError(404, 'No such permission request (already answered, or the turn stopped).');
    }
    active.pending.delete(requestId);
    active.lastActive = Date.now();
    active.process.answer(
      requestId,
      approve ? { allow: true, input: pending.input } : { allow: false, message: rejectedMessage },
    );
    this.emit(id, {
      type: 'permission-resolved',
      requestId,
      approved: approve,
      ...(approve ? {} : { reason: 'rejected' }),
    });
  }

  /** Stops the running turn: pending writes are refused, the CLI is interrupted, killed if it does not stop. */
  cancel(id: string): void {
    this.require(id);
    const active = this.active.get(id);
    if (!active?.busy) {
      return;
    }
    for (const pending of active.pending.values()) {
      active.process.answer(pending.requestId, { allow: false, message: cancelledMessage });
      this.emit(id, {
        type: 'permission-resolved',
        requestId: pending.requestId,
        approved: false,
        reason: 'cancelled',
      });
    }
    active.pending.clear();
    active.process.interrupt();
    const timer = setTimeout(() => {
      if (this.active.get(id) === active && active.busy) {
        active.process.kill();
      }
    }, killAfterInterruptMs);
    timer.unref();
  }

  /** Replays the stored events after `after`, then streams live ones until the returned function is called. */
  subscribe(id: string, after: number, listener: Listener): () => void {
    const conversation = this.require(id);
    for (const event of conversation.events) {
      if (event.seq > after) {
        listener(event);
      }
    }
    // A permission asked before this subscriber came is in the replay above, still unresolved.
    const active = this.active.get(id);
    let state: 'running' | 'idle' | 'stopped' = 'stopped';
    if (active) {
      state = active.busy ? 'running' : 'idle';
    }
    listener({
      type: 'status',
      state,
      seq: this.lastSeq(conversation),
      at: this.now(),
    });
    let set = this.listeners.get(id);
    if (!set) {
      set = new Set();
      this.listeners.set(id, set);
    }
    set.add(listener);
    return () => set.delete(listener);
  }

  /** Stops every process (shutdown). */
  shutdown(): void {
    clearInterval(this.sweeper);
    // A copy: stopping a session removes it from the map being walked.
    for (const id of Array.from(this.active.keys())) {
      this.stop(id);
    }
  }

  private spawn(conversation: Conversation, adapter: AgentAdapter, model: string, mcpToken: string): Active {
    this.makeRoom();
    const mcpConfigFile = join(this.options.runDir, `${conversation.id}.${randomUUID()}.mcp.json`);
    this.writeMcpConfig(mcpConfigFile, mcpToken);
    const sessionId = conversation.agentSessionId ? undefined : randomUUID();
    const id = conversation.id;
    // Filled right after start: the exit callback needs to know which process it was.
    const holder: { active?: Active } = {};
    let process: AgentProcess;
    try {
      process = adapter.start({
        model,
        resume: sessionId ? undefined : conversation.agentSessionId,
        sessionId: sessionId ?? conversation.agentSessionId ?? randomUUID(),
        workDir: this.options.workDir,
        mcpConfigFile,
        onEvent: (event) => this.onAdapterEvent(id, event),
        onExit: () => {
          const active = holder.active;
          if (active && this.active.get(id) === active) {
            this.active.delete(id);
            if (active.busy && !active.stopping) {
              this.emit(id, { type: 'error', message: 'The agent stopped before the end of the turn.' });
              this.emit(id, { type: 'turn-end', isError: true });
            }
            for (const pending of active.pending.values()) {
              this.emit(id, {
                type: 'permission-resolved',
                requestId: pending.requestId,
                approved: false,
                reason: 'cancelled',
              });
            }
            this.emit(id, { type: 'status', state: 'stopped' });
          }
          // Best effort: a file that cannot be removed now is removed when the runner starts again.
          rm(mcpConfigFile, { force: true }).catch(() => undefined);
        },
      });
    } catch (error) {
      // Nothing is left behind by a process that did not start: not its file with the token, not a CLI session that never existed.
      rm(mcpConfigFile, { force: true }).catch(() => undefined); // best effort: it is removed at the next start otherwise
      throw error;
    }
    if (sessionId) {
      conversation.agentSessionId = sessionId;
    }
    const active: Active = {
      process,
      model,
      busy: false,
      lastActive: Date.now(),
      pending: new Map(),
      mcpConfigFile,
      stopping: false,
      startedAt: Date.now(),
    };
    holder.active = active;
    this.active.set(id, active);
    return active;
  }

  /** The MCP configuration of one process: Mnemarr's `/mcp` and the person's token (the audit knows the conversation by the token). */
  private writeMcpConfig(file: string, mcpToken: string): void {
    const config = {
      mcpServers: {
        [serverName]: {
          type: 'http',
          url: this.options.mnemarrMcpUrl,
          headers: { 'X-Api-Key': mcpToken },
        },
      },
    };
    // Written before the process starts; readable by the runner's user only; removed when the process ends.
    writeFileSync(file, JSON.stringify(config), { mode: 0o600 });
  }

  private onAdapterEvent(id: string, event: AdapterEvent): void {
    const conversation = this.conversations.get(id);
    const active = this.active.get(id);
    if (!conversation) {
      return;
    }
    switch (event.type) {
      case 'session':
        if (conversation.agentSessionId !== event.agentSessionId) {
          conversation.agentSessionId = event.agentSessionId;
          this.store.save(conversation); // never rejects: a failure is reported by the store
        }
        return;
      case 'permission-request':
        this.decide(id, active, event);
        return;
      case 'turn-end':
        if (active) {
          active.busy = false;
          active.lastActive = Date.now();
        }
        this.emit(id, event);
        this.emit(id, { type: 'status', state: 'idle' });
        return;
      default:
        this.emit(id, event);
    }
  }

  /** Reads run, writes wait for the operator, anything else is refused without asking. */
  private decide(
    id: string,
    active: Active | undefined,
    request: Extract<AdapterEvent, { type: 'permission-request' }>,
  ): void {
    if (!active) {
      return;
    }
    switch (classify(request.toolName)) {
      case 'read':
        active.process.answer(request.requestId, { allow: true, input: request.input });
        return;
      case 'write':
        active.pending.set(request.requestId, {
          requestId: request.requestId,
          toolName: request.toolName,
          input: request.input,
        });
        this.emit(id, {
          type: 'permission',
          requestId: request.requestId,
          toolName: request.toolName,
          input: request.input,
        });
        return;
      default:
        active.process.answer(request.requestId, { allow: false, message: forbiddenMessage });
    }
  }

  private emit(id: string, event: ChatEvent): void {
    const conversation = this.conversations.get(id);
    if (!conversation) {
      return;
    }
    const envelope = {
      ...event,
      seq: this.lastSeq(conversation) + (transientTypes.has(event.type) ? 0 : 1),
      at: this.now(),
    } as ChatEventEnvelope;
    if (!transientTypes.has(event.type)) {
      conversation.events.push(envelope);
      conversation.updatedAt = envelope.at;
      this.store.save(conversation); // never rejects: a failure is reported by the store
    }
    for (const listener of this.listeners.get(id) ?? []) {
      listener(envelope);
    }
  }

  private lastSeq(conversation: Conversation): number {
    return conversation.events.at(-1)?.seq ?? 0;
  }

  private stop(id: string): void {
    const active = this.active.get(id);
    if (active) {
      active.stopping = true;
      active.process.close();
      const timer = setTimeout(() => active.process.kill(), killAfterInterruptMs);
      timer.unref();
    }
  }

  private makeRoom(): void {
    if (this.active.size < this.options.maxSessions) {
      return;
    }
    const idle = [...this.active.entries()]
      .filter(([, active]) => !active.busy && active.pending.size === 0)
      .sort(([, a], [, b]) => a.lastActive - b.lastActive);
    if (idle.length === 0) {
      throw new ChatError(429, `${this.options.maxSessions} conversations are running: wait for one to finish.`);
    }
    const [oldest] = idle[0];
    this.stop(oldest);
    if (this.active.delete(oldest)) {
      this.emit(oldest, { type: 'status', state: 'stopped' });
    }
  }

  private sweep(): void {
    const limit = Date.now() - this.options.idleTimeoutMs;
    for (const [id, active] of this.active) {
      if (!active.busy && active.pending.size === 0 && active.lastActive < limit) {
        this.stop(id);
      }
    }
  }

  private summary(conversation: Conversation): ConversationSummary {
    const active = this.active.get(conversation.id);
    return {
      id: conversation.id,
      title: conversation.title,
      agent: conversation.agent,
      model: conversation.model,
      createdAt: conversation.createdAt,
      updatedAt: conversation.updatedAt,
      running: Boolean(active),
      busy: Boolean(active?.busy),
      pendingPermissions: active?.pending.size ?? 0,
    };
  }

  private require(id: string): Conversation {
    const conversation = this.conversations.get(id);
    if (!conversation) {
      throw new ChatError(404, 'No such conversation.');
    }
    return conversation;
  }

  private adapter(id: string): AgentAdapter {
    const adapter = this.adapters.get(id);
    if (!adapter) {
      throw new ChatError(400, `Unknown agent '${id}'. Agents: ${[...this.adapters.keys()].join(', ')}.`);
    }
    return adapter;
  }

  private model(adapter: AgentAdapter, model: string | undefined): string {
    if (!model) {
      return adapter.defaultModel;
    }
    if (!adapter.models.some((option) => option.id === model)) {
      throw new ChatError(
        400,
        `Unknown model '${model}' for ${adapter.name}. Models: ${adapter.models.map((option) => option.id).join(', ')}.`,
      );
    }
    return model;
  }

  private now(): string {
    return (this.options.now?.() ?? new Date()).toISOString();
  }
}
