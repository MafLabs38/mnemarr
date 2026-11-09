import { useMutation, useQueryClient } from '@tanstack/react-query';

import { apiSend } from 'Helpers/Api/apiGet';
import { isApiPath, useApiQuery } from 'Helpers/Hooks/useApiQuery';
import { type Kind, kinds } from 'Helpers/Props/kinds';
import type { TranslationKey } from 'Utilities/String/en';

export type MachineState = 'unenrolled' | 'refused' | 'waiting' | 'failing' | 'online' | 'paused' | 'away' | 'silent';
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
  /** This Mnemarr carries a program for the system of the machine, and it is not the one the machine runs (ADR 0031). */
  programDiffers: boolean;
  /** The version a machine that was installed with the installer can update to, when this Mnemarr carries a newer one (ADR 0032). */
  updateTo: string | null;
  /** Why the last attempt of the collector to update failed, when it did (ADR 0032). */
  updateNote: string | null;
  /** What the collector found where it looks for conversations: why a machine that is online sends nothing (a sentence with no path). */
  watching: string | null;
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

/** One line a person pastes on the machine to install the collector and join it, for one system. */
export interface InstallLine {
  system: 'windows' | 'unix';
  label: string;
  command: string;
  /** A program that already carries its setting, to open with a double click: where one is offered. */
  downloadUrl: string | null;
  /** The installer, the same for every machine, with no code in it: it asks which Mnemarr to link to when it starts (ADR 0032). */
  setupUrl: string | null;
}

/**
 * A machine that has just been added: the code is in this answer and nowhere else, ever. `installs` has a line for each system this Mnemarr has a
 * program for (none: only `command`, which joins a machine that has the program already); `insecure` says the address is plain HTTP.
 */
export interface CreatedEnrolment {
  enrolment: EnrolmentItem;
  code: string;
  command: string;
  installs: InstallLine[];
  insecure: boolean;
}

/** How a state is worded, and how it looks: never by colour alone (the label carries an icon and the words). */
export const stateLook: Record<MachineState, { text: TranslationKey; kind: Kind }> = {
  online: { text: 'StateOnline', kind: kinds.SUCCESS },
  paused: { text: 'StatePaused', kind: kinds.INFO },
  away: { text: 'StateAway', kind: kinds.INFO },
  waiting: { text: 'StateWaiting', kind: kinds.INFO },
  unenrolled: { text: 'StateUnenrolled', kind: kinds.INFO },
  silent: { text: 'StateSilent', kind: kinds.WARNING },
  failing: { text: 'StateFailing', kind: kinds.DANGER },
  refused: { text: 'StateRefused', kind: kinds.DANGER },
};

/** The states change with the clock, not only with what a person does: the page asks again now and then. */
const REFRESH_MS = 5000;

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

/** A revoked machine is taken out of the list: its name is free again, and what it sent stays. */
export function useForgetMachine() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (machine: string) => apiSend('POST', `/collectors/${encodeURIComponent(machine)}/forget`),
    onSuccess: () => client.invalidateQueries({ predicate: isMachinesQuery }),
  });
}

/**
 * The name a machine will be given for what a person typed: a name is letters, digits, dots, hyphens and underscores, so "Home Office"
 * becomes `home-office` (lower case, accents dropped, spaces and other characters turned into hyphens). It is shown as it is typed, so that
 * nobody is told off for an ordinary name.
 */
export function machineNameFor(typed: string): string {
  const characters = [
    ...typed
      .normalize('NFD')
      .replaceAll(/[\u0300-\u036F]/g, '')
      .toLowerCase()
      .replaceAll(/[^a-z0-9._-]+/g, '-'),
  ];
  const isLetterOrDigit = (character: string | undefined) => character !== undefined && /[a-z0-9]/.test(character);
  const start = characters.findIndex(isLetterOrDigit);
  if (start === -1) {
    return '';
  }
  const kept = characters.slice(start, start + 50);
  while (kept.length > 0 && !isLetterOrDigit(kept.at(-1))) {
    kept.pop();
  }
  return kept.join('');
}

export function useRevokeMachine() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (machine: string) => apiSend('DELETE', `/collectors/${encodeURIComponent(machine)}`),
    onSuccess: () => client.invalidateQueries({ predicate: isMachinesQuery }),
  });
}

/** A machine is taken out of the list, revoked or not: what it sent stays, filed under its name (ADR 0031). */
export function useRemoveMachine() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (machine: string) => apiSend('POST', `/collectors/${encodeURIComponent(machine)}/remove`),
    onSuccess: () => client.invalidateQueries({ predicate: isMachinesQuery }),
  });
}

/** A machine, or a name that conversations came from, is given another name; what it sent is filed under it. */
export function useRenameMachine() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (change: { machine: string; name: string }) =>
      apiSend('POST', `/collectors/${encodeURIComponent(change.machine)}/rename`, { name: change.name }),
    onSuccess: () => client.invalidateQueries({ predicate: isMachinesQuery }),
  });
}

/** What a merge did: how many conversations were filed under the other machine, and how many it held already. */
export interface MergeResult {
  moved: number;
  alreadyThere: number;
}

/** What a machine sent is filed under another machine, and the first leaves the list, without any conversation made twice. */
export function useMergeMachine() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (merge: { machine: string; into: string }) =>
      apiSend<MergeResult>('POST', `/collectors/${encodeURIComponent(merge.machine)}/merge`, { into: merge.into }),
    onSuccess: () => client.invalidateQueries({ predicate: isMachinesQuery }),
  });
}

/** A machine is deleted with every conversation it sent. It cannot be undone. */
export function useEraseMachine() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (machine: string) =>
      apiSend<{ conversations: number }>('POST', `/collectors/${encodeURIComponent(machine)}/erase`),
    onSuccess: () => client.invalidateQueries({ predicate: isMachinesQuery }),
  });
}
