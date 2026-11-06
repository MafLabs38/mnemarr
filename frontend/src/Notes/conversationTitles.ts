import { useMutation, useQueryClient } from '@tanstack/react-query';

import { apiSend } from 'Helpers/Api/apiGet';
import { isApiPath, useApiQuery } from 'Helpers/Hooks/useApiQuery';

/** Where the title of a conversation comes from (ADR 0028): the first sentence, a summary written by a model, or a person. */
export type TitleSource = 'first-sentence' | 'summary' | 'person';

export interface ConversationTitleItem {
  path: string;
  title: string;
  source: TitleSource;
  summary: string | null;
  /** Whether a model writes titles on this Mnemarr. */
  modelEnabled: boolean;
}

export interface TitleCounts {
  modelEnabled: boolean;
  firstSentence: number;
  summary: number;
  person: number;
  /** Why the last call to the model failed, and when; null when none failed (a provider that is down must show). */
  lastError: string | null;
  lastErrorAt: string | null;
}

/** A change to a title makes the notes stale (their heading, the tree, the search) and the titles themselves. */
const isTitleQuery = (query: { queryKey: readonly unknown[] }) => isApiPath(query, '/notes', '/conversations');

export function useConversationTitle(path: string) {
  return useApiQuery<ConversationTitleItem>(`/conversations/title?path=${encodeURIComponent(path)}`);
}

export function useTitleCounts() {
  return useApiQuery<TitleCounts>('/conversations/titles');
}

/** A person gives the conversation a title of their own. */
export function useSetTitle(path: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (title: string) => apiSend<ConversationTitleItem>('PUT', '/conversations/title', { path, title }),
    onSuccess: () => client.invalidateQueries({ predicate: isTitleQuery }),
  });
}

/** The model writes the title and the summary of this conversation again, now. */
export function useRewriteTitle(path: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => apiSend<ConversationTitleItem>('POST', '/conversations/title/rewrite', { path }),
    onSuccess: () => client.invalidateQueries({ predicate: isTitleQuery }),
  });
}

/** Every conversation that has only a first sentence is queued for the model, in the background. */
export function useRewriteAllTitles() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => apiSend<{ queued: number }>('POST', '/conversations/titles/rewrite', { includePerson: false }),
    onSuccess: () => client.invalidateQueries({ predicate: isTitleQuery }),
  });
}
