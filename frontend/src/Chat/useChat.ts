import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';

import { apiSend } from 'Helpers/Api/apiGet';
import { useApiQuery } from 'Helpers/Hooks/useApiQuery';
import { permissions } from 'Session/session';
import { usePermissions } from 'Session/SessionContext';

import type { ChatEvent, ChatStatus, ConversationSummary } from './typings';

const base = '/chat';
const conversationsPath = `${base}/conversations`;
const STATUS_STALE_MS = 60_000;
const CONVERSATIONS_REFRESH_MS = 30_000;

/** Whether the chat is on (MNEMARR__AGENT__URL), and its agents and models. Only people whose role gives the chat ask: the others would get a 403. */
export function useChatStatus() {
  const { can } = usePermissions();
  return useApiQuery<ChatStatus>(base, { staleTime: STATUS_STALE_MS, enabled: can(permissions.chatUse) });
}

export function useConversations(enabled: boolean) {
  return useApiQuery<ConversationSummary[]>(conversationsPath, { enabled, refetchInterval: CONVERSATIONS_REFRESH_MS });
}

/** What changes the list of conversations makes stale. */
const isConversations = (query: { queryKey: readonly unknown[] }) => query.queryKey[1] === conversationsPath;

export function useCreateConversation() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (request: { agent?: string; model?: string }) =>
      apiSend<ConversationSummary>('POST', conversationsPath, request),
    onSuccess: () => client.invalidateQueries({ predicate: isConversations }),
  });
}

export function useDeleteConversation() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ id }: { id: string }) => apiSend('DELETE', `${conversationsPath}/${id}`),
    onSuccess: () => client.invalidateQueries({ predicate: isConversations }),
  });
}

export function useSendMessage() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ id, text, model }: { id: string; text: string; model?: string }) =>
      apiSend('POST', `${conversationsPath}/${id}/messages`, { text, model }),
    onSuccess: () => client.invalidateQueries({ predicate: isConversations }),
  });
}

export function useAnswerPermission() {
  return useMutation({
    mutationFn: ({ id, requestId, approve }: { id: string; requestId: string; approve: boolean }) =>
      apiSend('POST', `${conversationsPath}/${id}/permissions/${encodeURIComponent(requestId)}`, { approve }),
  });
}

export function useCancelTurn() {
  return useMutation({
    mutationFn: ({ id }: { id: string }) => apiSend('POST', `${conversationsPath}/${id}/cancel`, {}),
  });
}

export interface ChatStream {
  /** Stored events, in order (history and new ones). */
  events: ChatEvent[];
  /** The answer being written, from the live text deltas. */
  draft: string;
  state: 'running' | 'idle' | 'stopped' | 'connecting';
  connected: boolean;
}

const initial: ChatStream = { events: [], draft: '', state: 'connecting', connected: false };

function apply(stream: ChatStream, event: ChatEvent): ChatStream {
  switch (event.type) {
    case 'text-delta':
      return { ...stream, draft: stream.draft + event.text };
    case 'status':
      return { ...stream, state: event.state, draft: event.state === 'running' ? stream.draft : '' };
    default: {
      // Replays after a reconnect may repeat events: the sequence number tells.
      if (event.seq <= (stream.events.at(-1)?.seq ?? 0)) {
        return stream;
      }
      const draft =
        event.type === 'text' || event.type === 'tool-call' || event.type === 'turn-end' ? '' : stream.draft;
      return { ...stream, events: [...stream.events, event], draft };
    }
  }
}

/**
 * A conversation's events (ADR 0035): the server-sent stream replays the stored history, then the live events. EventSource reconnects by itself and resumes after the last
 * event it got (Last-Event-ID).
 */
export function useChatStream(conversationId: string | null): ChatStream {
  // Keyed by conversation: switching conversations shows an empty stream until the new one replays.
  const [state, setState] = useState<{ id: string | null; stream: ChatStream }>({ id: null, stream: initial });

  useEffect(() => {
    if (!conversationId || typeof EventSource === 'undefined') {
      return undefined;
    }
    const update = (change: (stream: ChatStream) => ChatStream) =>
      setState((current) => ({
        id: conversationId,
        stream: change(current.id === conversationId ? current.stream : initial),
      }));
    const source = new EventSource(`/api/v1${conversationsPath}/${conversationId}/events`);
    source.onopen = () => update((stream) => ({ ...stream, connected: true }));
    source.onerror = () => update((stream) => ({ ...stream, connected: false }));
    source.addEventListener('chat', (message: MessageEvent<string>) => {
      const event = JSON.parse(message.data) as ChatEvent;
      update((stream) => apply(stream, event));
    });
    return () => source.close();
  }, [conversationId]);

  return state.id === conversationId ? state.stream : initial;
}
