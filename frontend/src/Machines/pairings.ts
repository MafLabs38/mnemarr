import { useMutation, useQueryClient } from '@tanstack/react-query';

import { apiSend } from 'Helpers/Api/apiGet';
import { isApiPath, useApiQuery } from 'Helpers/Hooks/useApiQuery';

import type { MachineKindName } from './machines';

/** A computer that asked to be linked to this Mnemarr, and waits for a person to answer (ADR 0030). */
export interface PairingItem {
  id: string;
  hostname: string;
  requestedAt: string;
  expiresAt: string;
}

/** A person answers while a computer waits: the page looks again now and then. */
const REFRESH_MS = 5000;

const isPairingQuery = (query: { queryKey: readonly unknown[] }) => isApiPath(query, '/pairings', '/collectors');

/** Only an administrator sees the requests: the server refuses everybody else. */
export function usePairings(enabled: boolean) {
  return useApiQuery<PairingItem[]>('/pairings', { enabled, refetchInterval: REFRESH_MS });
}

/** The request that a computer shows the code of. A POST: the code is not in an address. */
export function useLookupPairing() {
  return useMutation({
    mutationFn: (code: string) => apiSend<PairingItem>('POST', '/pairings/lookup', { code }),
  });
}

export function useApprovePairing() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (answer: { id: string; machine: string; kind: MachineKindName }) =>
      apiSend<PairingItem>('POST', `/pairings/${answer.id}/approve`, { machine: answer.machine, kind: answer.kind }),
    onSuccess: () => client.invalidateQueries({ predicate: isPairingQuery }),
  });
}

export function useRejectPairing() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiSend('POST', `/pairings/${id}/reject`),
    onSuccess: () => client.invalidateQueries({ predicate: isPairingQuery }),
  });
}
