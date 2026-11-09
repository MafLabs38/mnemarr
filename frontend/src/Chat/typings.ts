/** The chat (ADR 0035): what Mnemarr's proxy passes through from the assistant runner. */

export interface ChatModel {
  id: string;
  label: string;
}

export interface ChatAgent {
  id: string;
  name: string;
  models: ChatModel[];
  defaultModel: string;
  /** False when the runner has no credentials for this CLI. */
  available: boolean;
}

export interface ChatStatus {
  /** MNEMARR__AGENT__URL is set: the chat is shown. */
  enabled: boolean;
  /** The runner answered. */
  reachable: boolean;
  agents: ChatAgent[] | null;
  /** The tools that change notes and sources are offered (MNEMARR__MCP__WRITETOOLS): without them the chat only reads. */
  mcpWriteTools: boolean;
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

export interface Usage {
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  costUsd?: number;
  durationMs?: number;
}

export type ChatEvent = { seq: number; at: string } & (
  | { type: 'user'; text: string }
  | { type: 'text-delta'; text: string }
  | { type: 'text'; text: string }
  | { type: 'thinking'; text: string }
  | { type: 'tool-call'; id: string; name: string; input: unknown }
  | { type: 'tool-result'; id: string; isError: boolean; summary: string }
  | { type: 'permission'; requestId: string; toolName: string; input: unknown }
  | { type: 'permission-resolved'; requestId: string; approved: boolean; reason?: string }
  | { type: 'turn-end'; isError: boolean; usage?: Usage }
  | { type: 'status'; state: 'running' | 'idle' | 'stopped' }
  | { type: 'error'; message: string }
);
