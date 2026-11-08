import { useSyncExternalStore } from 'react';

export const minPanelWidth = 320;
export const maxPanelWidth = 960;
export const defaultPanelWidth = 440;

const storageKey = 'mnemarr.chat';

interface ChatState {
  open: boolean;
  width: number;
  /** The conversation shown; null for a new one. */
  conversationId: string | null;
  agent: string | null;
  model: string | null;
}

interface ChatActions {
  setOpen: (open: boolean) => void;
  setWidth: (width: number) => void;
  setConversation: (conversationId: string | null) => void;
  setAgent: (agent: string, model: string) => void;
  setModel: (model: string) => void;
}

export function clampWidth(width: number): number {
  return Math.round(Math.min(maxPanelWidth, Math.max(minPanelWidth, width)));
}

const defaults: ChatState = { open: false, width: defaultPanelWidth, conversationId: null, agent: null, model: null };

/** What the browser kept, read defensively: a private window, a full or blocked storage, or an old shape all give the defaults. */
function load(): ChatState {
  try {
    const saved = JSON.parse(localStorage.getItem(storageKey) ?? 'null') as Partial<ChatState> | null;
    return {
      open: saved?.open === true,
      width: typeof saved?.width === 'number' ? clampWidth(saved.width) : defaultPanelWidth,
      conversationId: typeof saved?.conversationId === 'string' ? saved.conversationId : null,
      agent: typeof saved?.agent === 'string' ? saved.agent : null,
      model: typeof saved?.model === 'string' ? saved.model : null,
    };
  } catch {
    return defaults;
  }
}

let state: ChatState = load();
const listeners = new Set<() => void>();

function update(change: Partial<ChatState>) {
  state = { ...state, ...change };
  try {
    localStorage.setItem(storageKey, JSON.stringify(state));
  } catch {
    // Kept for this tab only: the panel works the same.
  }
  listeners.forEach((listener) => listener());
}

const actions: ChatActions = {
  setOpen: (open) => update({ open }),
  setWidth: (width) => update({ width: clampWidth(width) }),
  setConversation: (conversationId) => update({ conversationId }),
  setAgent: (agent, model) => update({ agent, model }),
  setModel: (model) => update({ model }),
};

/** Forgets the screen options and goes back to what was kept: for the tests, which share the module. */
export function resetChatStore(next: Partial<ChatState> = {}) {
  state = { ...defaults, ...next };
  listeners.forEach((listener) => listener());
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** The screen options of the chat panel (ADR 0035), kept per browser: what is read, and what changes it. */
export function useChatStore(): ChatState & ChatActions;
export function useChatStore<T>(select: (current: ChatState & ChatActions) => T): T;
export function useChatStore<T>(select?: (current: ChatState & ChatActions) => T) {
  const current = useSyncExternalStore(subscribe, () => state);
  const whole = { ...current, ...actions };
  return select ? select(whole) : whole;
}
