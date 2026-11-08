import { useMutation, useQueryClient } from '@tanstack/react-query';

import { apiSend } from 'Helpers/Api/apiGet';
import { isApiPath, useApiQuery } from 'Helpers/Hooks/useApiQuery';

export type ConsolidationState =
  'off' | 'no-runner' | 'no-memory-vault' | 'memory-vault-read-only' | 'running' | 'ready';

/** Where the consolidation of the conversations into notes of the memory vault stands (ADR 0036). */
export interface ConsolidationStatus {
  enabled: boolean;
  state: ConsolidationState;
  model: string;
  memoryVault: string | null;
  waiting: number;
  done: number;
  failing: number;
  notes: number;
  today: number;
  perDay: number;
  /** Why the last conversation failed, in a short sentence with nothing that was read or written. */
  lastFailure: string | null;
  lastFailureAt: string | null;
  /** Set while the assistant runner cannot be reached. */
  problem: string | null;
}

const REFRESH_MS = 5000;

/** Asked again while a run is going and while conversations wait; once it is off or idle with nothing waiting, it stops. */
export function useConsolidation(enabled: boolean) {
  return useApiQuery<ConsolidationStatus>('/consolidation', {
    enabled,
    refetchInterval: (status) =>
      status && status.enabled && (status.state === 'running' || status.waiting > 0) ? REFRESH_MS : false,
  });
}

/** Consolidates what waits now, without waiting for the conversations to be left alone; it runs in the background. */
export function useConsolidateNow() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => apiSend('POST', '/consolidation/run', {}),
    onSuccess: () =>
      client.invalidateQueries({
        predicate: (query) => isApiPath(query, '/consolidation', '/notes', '/search', '/sources'),
      }),
  });
}

/** Gives the conversations that failed too often their tries back: the next pass tries them again. */
export function useRetryFailed() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => apiSend<{ retried: number }>('POST', '/consolidation/retry', {}),
    onSuccess: () => client.invalidateQueries({ predicate: (query) => isApiPath(query, '/consolidation') }),
  });
}
