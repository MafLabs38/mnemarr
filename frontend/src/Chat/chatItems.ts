import type { ChatEvent, Usage } from './typings';

export type PermissionState = 'pending' | 'approved' | 'rejected' | 'cancelled';

export type ChatItem =
  | { kind: 'user'; key: string; text: string }
  | { kind: 'text'; key: string; text: string }
  | { kind: 'thinking'; key: string; text: string }
  | {
      kind: 'tool';
      key: string;
      name: string;
      input: unknown;
      result?: { isError: boolean; summary: string };
    }
  | { kind: 'permission'; key: string; requestId: string; toolName: string; input: unknown; state: PermissionState }
  | { kind: 'turn-end'; key: string; isError: boolean; usage?: Usage }
  | { kind: 'error'; key: string; message: string };

/** `mcp__mnemarr__create_note` → `create_note`. */
export function shortToolName(name: string): string {
  const server = /^mcp__[a-z0-9-]{1,64}__/.exec(name);
  return server ? name.slice(server[0].length) : name;
}

function resolved(event: Extract<ChatEvent, { type: 'permission-resolved' }>): PermissionState {
  if (event.approved) {
    return 'approved';
  }
  return event.reason === 'cancelled' ? 'cancelled' : 'rejected';
}

/**
 * The stored events of a conversation as what the panel shows: a tool's result joins its call, a permission's
 * answer updates its card. Live-only events (text deltas, status) are handled by the stream, not here.
 */
export default function chatItems(events: ChatEvent[]): ChatItem[] {
  const items: ChatItem[] = [];
  const tools = new Map<string, Extract<ChatItem, { kind: 'tool' }>>();
  const permissions = new Map<string, Extract<ChatItem, { kind: 'permission' }>>();

  for (const event of events) {
    const key = String(event.seq);
    switch (event.type) {
      case 'user':
      case 'text':
      case 'thinking':
        items.push({ kind: event.type, key, text: event.text });
        break;
      case 'tool-call': {
        const item = { kind: 'tool' as const, key, name: event.name, input: event.input };
        tools.set(event.id, item);
        items.push(item);
        break;
      }
      case 'tool-result': {
        const call = tools.get(event.id);
        if (call) {
          call.result = { isError: event.isError, summary: event.summary };
        }
        break;
      }
      case 'permission': {
        const item = {
          kind: 'permission' as const,
          key,
          requestId: event.requestId,
          toolName: event.toolName,
          input: event.input,
          state: 'pending' as PermissionState,
        };
        permissions.set(event.requestId, item);
        items.push(item);
        break;
      }
      case 'permission-resolved': {
        const card = permissions.get(event.requestId);
        if (card) {
          card.state = resolved(event);
        }
        break;
      }
      case 'turn-end':
        items.push({ kind: 'turn-end', key, isError: event.isError, usage: event.usage });
        break;
      case 'error':
        items.push({ kind: 'error', key, message: event.message });
        break;
      default:
        break;
    }
  }
  return items;
}
