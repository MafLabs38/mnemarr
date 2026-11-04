import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiGetText, apiSend, apiSendYaml } from 'Helpers/Api/apiGet';
import { isApiPath, useApiQuery } from 'Helpers/Hooks/useApiQuery';

export type SourceKind = 'Knowledge' | 'Memory' | 'ApplicationDocs';
export type SourceMode = 'ReadOnly' | 'ReadWrite';

export interface SourceItem {
  id: string;
  name: string;
  kind: SourceKind;
  mode: SourceMode;
  path: string;
  createdAt: string;
  notes: number;
  chunks: number;
  lastIndexedAt: string | null;
  /** When the last pass over the folder ended, since the server started. */
  lastScanAt: string | null;
  /** Why the last pass failed (a stable code), or null when it went well. */
  lastError: 'folder-missing' | 'outside-roots' | 'unreadable' | 'index-failed' | null;
  /** A configuration document owns it: it is changed by changing the document. */
  managed: boolean;
}

/** A kind of source, the rule behind it and how many are declared: what the base needs, and what is missing. */
export interface SourceKindItem {
  kind: SourceKind;
  required: boolean;
  unique: boolean;
  count: number;
  satisfied: boolean;
  defaultMode: SourceMode;
}

export interface NewSource {
  name: string;
  kind: SourceKind;
  mode: SourceMode;
  path: string;
}

/** What one pass over a source did. */
export interface IndexReport {
  added: number;
  updated: number;
  removed: number;
  unchanged: number;
}

export function useSources() {
  return useApiQuery<SourceItem[]>('/sources');
}

export function useSourceKinds() {
  return useApiQuery<SourceKindItem[]>('/sources/kinds');
}

/** The folders the operator allows for sources: they do not change while the page is open. */
export function useSourceRoots() {
  return useApiQuery<string[]>('/sources/roots', { staleTime: Infinity });
}

/** What a change to the sources makes stale: the list with its counts, every search, and the document that describes them. */
const isSourcesQuery = (query: { queryKey: readonly unknown[] }) => isApiPath(query, '/sources', '/search', '/config');

export function useCreateSource() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (source: NewSource) => apiSend('POST', '/sources', source),
    onSuccess: () => client.invalidateQueries({ predicate: isSourcesQuery }),
  });
}

/** Changes the name, the mode or the folder of a source. Its kind does not change: the rule behind it is the business of the kind. */
export function useUpdateSource(id: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (change: { name: string; mode: SourceMode; path: string }) => apiSend('PUT', `/sources/${id}`, change),
    onSuccess: () => client.invalidateQueries({ predicate: isSourcesQuery }),
  });
}

export function useDeleteSource(id: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => apiSend('DELETE', `/sources/${id}`),
    onSuccess: () => client.invalidateQueries({ predicate: isSourcesQuery }),
  });
}

/** Brings the index of a source in line with its folder now, without waiting for the next scan. */
export function useIndexSource(id: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => apiSend<IndexReport>('POST', `/sources/${id}/index`),
    onSuccess: () => client.invalidateQueries({ predicate: isSourcesQuery }),
  });
}

/** What a configuration document does, or would do, to one source. */
export interface PlanItem {
  action: 'Create' | 'Update' | 'Adopt' | 'Remove' | 'Keep';
  kind: SourceKind;
  name: string;
  path: string | null;
  mode: SourceMode | null;
}

export interface ConfigPlan {
  applied: boolean;
  items: PlanItem[];
}

/** Whether a configuration file is in charge, whether it was applied and why not. */
export interface ConfigStatus {
  file: boolean;
  applied: boolean;
  error: string | null;
}

export function useConfigStatus() {
  return useApiQuery<ConfigStatus>('/config/status');
}

/** The current state as a configuration document (YAML): what to keep in a repository. */
export function useConfigDocument() {
  return useQuery({ queryKey: ['api', '/config'], queryFn: ({ signal }) => apiGetText('/config', signal) });
}

/** Previews or applies a configuration document. A document with an error changes nothing and the error lists every reason. */
export function useApplyConfig() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ yaml, dryRun }: { yaml: string; dryRun: boolean }) =>
      apiSendYaml<ConfigPlan>(`/config${dryRun ? '?dryRun=true' : ''}`, yaml),
    onSuccess: (plan) => (plan.applied ? client.invalidateQueries({ predicate: isSourcesQuery }) : undefined),
  });
}
