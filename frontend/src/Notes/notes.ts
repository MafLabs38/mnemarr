import { useMutation, useQueryClient } from '@tanstack/react-query';

import { apiSend } from 'Helpers/Api/apiGet';
import { isApiPath, useApiQuery } from 'Helpers/Hooks/useApiQuery';
import type { SourceKind, SourceMode, SyncError } from 'Sources/sources';

export interface NoteEntry {
  sourceId: string;
  sourceName: string;
  path: string;
  title: string;
  topic: string;
}

export interface NoteRef {
  sourceId: string;
  sourceName: string;
  path: string;
  title: string;
}

export interface NoteLink {
  /** What the link names, as stored: lower case, no extension, from the root of the source. */
  target: string;
  /** The note it leads to, or null when no note the person may read has that name. */
  note: NoteRef | null;
}

/** Why a person may or may not write the notes of a source (the server decides; the interface explains). */
export type RightsReason = 'writable' | 'source-read-only' | 'kind-not-writable' | 'no-permission';

export interface NoteRights {
  canWrite: boolean;
  reason: RightsReason;
  /** The role of the person, to say it in words. */
  role: string;
}

/** A source of notes the person may read: what kind of notes it holds, whether it is open for writing, and what the person may do there. */
export interface NoteSource {
  id: string;
  name: string;
  kind: SourceKind;
  mode: SourceMode;
  rights: NoteRights;
}

export interface Note {
  sourceId: string;
  sourceName: string;
  path: string;
  title: string;
  /** The note as written (without its front matter). */
  markdown: string;
  /** The same, with its wikilinks and embeds rewritten by the server into links and images (see `addresses.ts`): what is shown. */
  rendered: string;
  tags: string[];
  links: NoteLink[];
  /** The first backlinks, and `backlinksTotal` of them in all. */
  backlinks: NoteRef[];
  backlinksTotal: number;
  /** The whole text of the file, front matter included: what the editor changes. */
  raw: string;
  /** The version of the file as read: sent back with a change, so that a note that changed since is not overwritten. */
  version: string;
  /** Whether the person may write this note (`rights.canWrite`). */
  writable: boolean;
  sourceKind: SourceKind;
  sourceMode: SourceMode;
  rights: NoteRights;
}

export interface Tag {
  tag: string;
  count: number;
}

/** The notes the person may read and how many there are: a very large vault is cut, and `total` says by how much. */
export interface NoteListing {
  notes: NoteEntry[];
  total: number;
  /** The sources the notes are in, with their kind, their mode and what the person may do in each (also those with no note yet). */
  sources: NoteSource[];
}

export interface TagListing {
  tags: Tag[];
  total: number;
}

/** The address of the reader for a note: the one place it is built, so that every link to a note goes the same way. */
export function noteAddress(
  note: Pick<NoteRef, 'sourceId' | 'path'>,
  tag?: string | null,
  heading?: string | null,
): string {
  const params = new URLSearchParams({ source: note.sourceId, path: note.path });
  if (tag) {
    // The notes of a tag are being read one after the other: the filter stays.
    params.set('tag', tag);
  }
  const fragment = heading ? `#${encodeURIComponent(heading)}` : '';
  return `/notes?${params.toString()}${fragment}`;
}

/** The id of a heading, so that a link can lead to it: the rule of the server (`Headings.Slug`), which writes the links. */
export function headingSlug(heading: string): string {
  let slug = '';
  for (const character of heading.trim().toLowerCase()) {
    if (/[\p{L}\p{Nd}_-]/u.test(character)) {
      slug += character;
    } else if (/\s/u.test(character)) {
      slug += '-';
    }
  }
  return slug;
}

function encodePath(path: string): string {
  return path.split('/').map(encodeURIComponent).join('/');
}

/** The notes the person may read, of one tag when asked. */
export function useNoteTree(tag: string | null) {
  return useApiQuery<NoteListing>(tag ? `/notes?tag=${encodeURIComponent(tag)}` : '/notes');
}

export function useTags() {
  return useApiQuery<TagListing>('/notes/tags');
}

const SOURCE_ID = /^[\da-f]{8}(-[\da-f]{4}){3}-[\da-f]{12}$/i;

/**
 * The note an address names, or null. The address is typed by whoever sent the link, so it is checked before it becomes a call to the API:
 * a source is an id, and a path has no empty, `.` or `..` segment (`..` would walk out of the notes, to another resource of the API).
 */
export function noteToRead(sourceId: string | null, path: string | null): { sourceId: string; path: string } | null {
  if (sourceId === null || path === null || !SOURCE_ID.test(sourceId)) {
    return null;
  }
  const segments = path.split('/');
  return segments.some((segment) => segment === '' || segment === '.' || segment === '..') ? null : { sourceId, path };
}

/** A note with its links and backlinks. Nothing is asked until a valid note is chosen. */
export function useNote(sourceId: string | null, path: string | null) {
  const note = noteToRead(sourceId, path);
  return useApiQuery<Note>(note ? `/notes/${note.sourceId}/${encodePath(note.path)}` : '/notes/none', {
    enabled: note !== null,
  });
}

/** The address of a file of a source (an image), served by the authenticated API from that source only. */
export function filePath(sourceId: string, path: string): string {
  return `/sources/${sourceId}/files/${encodePath(path)}`;
}

export interface SearchHit {
  sourceId: string;
  sourceName: string;
  path: string;
  topic: string;
  title: string;
  heading: string;
  snippet: string;
  score: number;
  /** Why it is in the answer: it holds the words, it means what was asked, or both. */
  match: 'keywords' | 'meaning' | 'both';
}

export interface SemanticSearchState {
  state: 'off' | 'starting' | 'ready' | 'failed';
  model: string;
  passages: number;
  computed: number;
  reason: string | null;
}

const SEMANTIC_REFRESH_MS = 5000;

/** Where the search by meaning is at: while the passages are computed, the page asks again. */
export function useSemanticSearchState() {
  return useApiQuery<SemanticSearchState>('/search/status', { refetchInterval: SEMANTIC_REFRESH_MS });
}

/** The best matching chunks of the notes. Nothing is asked while there is nothing to look for. */
export function useSearch(query: string) {
  return useApiQuery<SearchHit[]>(`/search?q=${encodeURIComponent(query)}`, { enabled: query.trim() !== '' });
}

/** What a change to the notes makes stale: the tree, a note, the tags, the searches, and the counts of the sources. */
const isNotesQuery = (query: { queryKey: readonly unknown[] }) => isApiPath(query, '/notes', '/search', '/sources');

/** What became of the commit of a change to a Git source: pushed, or the code of why not (the commit is kept, and a later sync pushes it). */
export interface GitPublish {
  pushed: boolean;
  error: SyncError | null;
}

export interface WriteResult {
  path: string;
  /** The version of the note as it is now, to send back with the next change. */
  version: string;
  git: GitPublish | null;
}

/** What a change leaves to say on the page it goes back to (carried in the navigation state): a push that did not leave, links left behind. */
export interface WriteOutcome {
  git?: GitPublish | null;
  notesNotRewritten?: number;
}

export function outcomeOf(state: unknown): WriteOutcome | null {
  const outcome = (state as { outcome?: WriteOutcome } | null)?.outcome;
  return outcome ?? null;
}

export interface RenameResult extends WriteResult {
  linksRewritten: number;
  notesRewritten: number;
  /** Notes of other sources that link to the note and were left as they were. */
  notesNotRewritten: number;
}

export function useCreateNote(sourceId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ path, content }: { path: string; content: string }) =>
      apiSend<WriteResult>('POST', `/notes/${sourceId}/${encodePath(path)}`, { content }),
    onSuccess: () => client.invalidateQueries({ predicate: isNotesQuery }),
  });
}

/** Replaces the text of a note; refused (409) when the note is no longer the version that was read. */
export function useUpdateNote(sourceId: string, path: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ content, version }: { content: string; version: string }) =>
      apiSend<WriteResult>('PUT', `/notes/${sourceId}/${encodePath(path)}`, { content, version }),
    onSuccess: () => client.invalidateQueries({ predicate: isNotesQuery }),
  });
}

/** Moves a note; the links to it in the notes of the same source are rewritten by the server. */
export function useRenameNote(sourceId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ from, to }: { from: string; to: string }) =>
      apiSend<RenameResult>('POST', `/notes/${sourceId}/rename`, { from, to }),
    onSuccess: () => client.invalidateQueries({ predicate: isNotesQuery }),
  });
}

export function useDeleteNote(sourceId: string, path: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (version: string) =>
      apiSend<{ git: GitPublish | null }>(
        'DELETE',
        `/notes/${sourceId}/${encodePath(path)}?version=${encodeURIComponent(version)}`,
      ),
    onSuccess: () => client.invalidateQueries({ predicate: isNotesQuery }),
  });
}

/** A text drawn as a note of the source it would be saved in, its links resolved by the server. Nothing is written. */
export function usePreviewNote(sourceId: string) {
  return useMutation({
    mutationFn: ({ path, content }: { path: string; content: string }) =>
      apiSend<{ rendered: string }>('POST', `/notes/${sourceId}/preview`, { path, content }),
  });
}
