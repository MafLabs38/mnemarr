// The events of a conversation, the same whatever the agent CLI behind it (D-87). Adapters turn their CLI's
// output into these; the session manager numbers them, keeps the durable ones and streams them all.

/** Token and cost figures of one turn, as the CLI reports them (a subscription reports a notional cost). */
export interface Usage {
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  costUsd?: number;
  durationMs?: number;
}

export type ChatEvent =
  /** What the operator typed or pasted. */
  | { type: 'user'; text: string }
  /** A piece of the answer being written (live only, never stored: the complete `text` follows). */
  | { type: 'text-delta'; text: string }
  /** A complete block of the answer, Markdown. */
  | { type: 'text'; text: string }
  /** The model's visible reasoning, shown collapsed. */
  | { type: 'thinking'; text: string }
  /** The agent calls a tool. `name` is the CLI's name (e.g. `mcp__mnemarr__list_sources`). */
  | { type: 'tool-call'; id: string; name: string; input: unknown }
  /** What a tool answered, shortened. */
  | { type: 'tool-result'; id: string; isError: boolean; summary: string }
  /** A write waits for the operator: Approve or Reject. */
  | { type: 'permission'; requestId: string; toolName: string; input: unknown }
  /** The operator answered (or the turn was cancelled: `approved` false). */
  | { type: 'permission-resolved'; requestId: string; approved: boolean; reason?: string }
  /** The turn is over. */
  | { type: 'turn-end'; isError: boolean; usage?: Usage }
  /** The conversation's process state (live only). */
  | { type: 'status'; state: 'running' | 'idle' | 'stopped' }
  | { type: 'error'; message: string };

/** An event as stored and streamed: numbered within its conversation. */
export type ChatEventEnvelope = ChatEvent & { seq: number; at: string };

/** Live-only events: streamed, never written to the conversation file. */
export const transientTypes: ReadonlySet<ChatEvent['type']> = new Set(['text-delta', 'status']);

/** Longest tool result summary kept: enough to see what the tool said, not a copy of the fleet. */
export const maxSummaryLength = 1500;

export function summarize(text: string): string {
  const flat = text.trim();
  return flat.length <= maxSummaryLength ? flat : `${flat.slice(0, maxSummaryLength)}…`;
}
