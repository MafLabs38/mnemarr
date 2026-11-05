import { useMutation, useQueryClient } from '@tanstack/react-query';

import { apiSend } from 'Helpers/Api/apiGet';
import { isApiPath, useApiQuery } from 'Helpers/Hooks/useApiQuery';
import { type Kind, kinds } from 'Helpers/Props/kinds';
import type { TranslationKey } from 'Utilities/String/en';

export type MachineState = 'unenrolled' | 'refused' | 'waiting' | 'failing' | 'online' | 'away' | 'silent';
export type MachineKindName = 'always-on' | 'intermittent';

/** A machine that joined, or a name that conversations came from and that never did (ADR 0027). */
export interface MachineItem {
  machine: string;
  kind: MachineKindName | null;
  state: MachineState;
  conversations: number;
  lines: number;
  lastSeen: string | null;
  enrolledAt: string | null;
  version: string | null;
  pendingLines: number;
  error: string | null;
}

/** A code that joins a machine, and where it stands. */
export interface EnrolmentItem {
  id: string;
  machine: string;
  kind: MachineKindName;
  state: 'pending' | 'approved' | 'expired';
  requestedAt: string;
  requestedBy: string;
  approvedBy: string | null;
  expiresAt: string;
}

/** A machine that has just been added: the code is in this answer and nowhere else, ever. */
export interface CreatedEnrolment {
  enrolment: EnrolmentItem;
  code: string;
  command: string;
}

/** How a state is worded, and how it looks: never by colour alone (the label carries an icon and the words). */
export const stateLook: Record<MachineState, { text: TranslationKey; kind: Kind }> = {
  online: { text: 'StateOnline', kind: kinds.SUCCESS },
  away: { text: 'StateAway', kind: kinds.INFO },
  waiting: { text: 'StateWaiting', kind: kinds.INFO },
  unenrolled: { text: 'StateUnenrolled', kind: kinds.INFO },
  silent: { text: 'StateSilent', kind: kinds.WARNING },
  failing: { text: 'StateFailing', kind: kinds.DANGER },
  refused: { text: 'StateRefused', kind: kinds.DANGER },
};

/** The states change with the clock, not only with what a person does: the page asks again now and then. */
const REFRESH_MS = 15_000;

const isMachinesQuery = (query: { queryKey: readonly unknown[] }) => isApiPath(query, '/collectors', '/enrolments');

export function useMachines() {
  return useApiQuery<MachineItem[]>('/collectors', { refetchInterval: REFRESH_MS });
}

/** Only an administrator sees the requests: the server refuses everybody else. */
export function useEnrolments(enabled: boolean) {
  return useApiQuery<EnrolmentItem[]>('/enrolments', { enabled, refetchInterval: REFRESH_MS });
}

export function useAddMachine() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (machine: { machine: string; kind: MachineKindName }) =>
      apiSend<CreatedEnrolment>('POST', '/enrolments', machine),
    gcTime: 0,
    onSuccess: () => client.invalidateQueries({ predicate: isMachinesQuery }),
  });
}

export function useApproveEnrolment() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiSend('POST', `/enrolments/${id}/approve`),
    onSuccess: () => client.invalidateQueries({ predicate: isMachinesQuery }),
  });
}

export function useRejectEnrolment() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiSend('POST', `/enrolments/${id}/reject`),
    onSuccess: () => client.invalidateQueries({ predicate: isMachinesQuery }),
  });
}

export function useRevokeMachine() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (machine: string) => apiSend('DELETE', `/collectors/${encodeURIComponent(machine)}`),
    onSuccess: () => client.invalidateQueries({ predicate: isMachinesQuery }),
  });
}
