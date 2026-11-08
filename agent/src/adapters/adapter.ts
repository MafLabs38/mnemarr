// The adapter interface (ADR 0035): one implementation per agent CLI. An adapter starts the CLI in its headless,
// structured mode, writes the operator's messages and permission answers to it, and turns its output into
// ChatEvents. It never decides what a tool may do: the session manager's policy does (ADR 0035).

import type { ChatEvent } from '../events.ts';

export interface ModelOption {
  /** The value passed to the CLI, e.g. `sonnet`. */
  id: string;
  label: string;
}

/** What the adapter reports besides chat events. */
export type AdapterEvent =
  | ChatEvent
  /** The CLI's own session id, to resume the conversation later. */
  | { type: 'session'; agentSessionId: string; model?: string; tools?: string[] }
  /** The CLI asks whether a tool may run; answered through `AgentProcess.answer`. */
  | { type: 'permission-request'; requestId: string; toolName: string; input: unknown };

export interface StartOptions {
  model: string;
  /** Resume this CLI session (a conversation reopened after its process stopped). */
  resume?: string;
  /** The session id to give a new conversation's first process, when the CLI accepts one. */
  sessionId: string;
  /** An empty directory: the CLI's working directory, with no project instructions in it. */
  workDir: string;
  /** The MCP configuration file pointing at Mnemarr's `/mcp` (with the key; mode 600). */
  mcpConfigFile: string;
  onEvent(event: AdapterEvent): void;
  onExit(code: number | null): void;
}

export type PermissionDecision = { allow: true; input: unknown } | { allow: false; message: string };

/** One running CLI process, serving one conversation. */
export interface AgentProcess {
  send(text: string): void;
  answer(requestId: string, decision: PermissionDecision): void;
  /** Stops the current turn; the process stays for the next message. */
  interrupt(): void;
  /** Ends the process gracefully (closes its input). */
  close(): void;
  kill(): void;
}

export interface AgentAdapter {
  readonly id: string;
  readonly name: string;
  readonly models: readonly ModelOption[];
  readonly defaultModel: string;
  /** Whether the credentials it needs are present (the agent is listed but unusable otherwise). */
  available(): boolean;
  /** What to set when `available()` is false, said to the person who tries to use the agent. */
  readonly credentialsHint: string;
  start(options: StartOptions): AgentProcess;
  /** Removes what the agent kept of a conversation (its session) when the conversation is deleted. */
  forget?(agentSessionId: string): Promise<void>;
}
