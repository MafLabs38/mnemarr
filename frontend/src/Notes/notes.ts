import { useApiQuery } from 'Helpers/Hooks/useApiQuery';

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
}

export interface Tag {
  tag: string;
  count: number;
}

/** The notes the person may read and how many there are: a very large vault is cut, and `total` says by how much. */
export interface NoteListing {
  notes: NoteEntry[];
  total: number;
}

export interface TagListing {
  tags: Tag[];
  total: number;
}

/** The address of the reader for a note: the one place it is built, so that every link to a note goes the same way. */
export function noteAddress(note: Pick<NoteRef, 'sourceId' | 'path'>, tag?: string | null): string {
  const params = new URLSearchParams({ source: note.sourceId, path: note.path });
  if (tag) {
    // The notes of a tag are being read one after the other: the filter stays.
    params.set('tag', tag);
  }
  return `/notes?${params.toString()}`;
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
}

/** The best matching chunks of the notes. Nothing is asked while there is nothing to look for. */
export function useSearch(query: string) {
  return useApiQuery<SearchHit[]>(`/search?q=${encodeURIComponent(query)}`, { enabled: query.trim() !== '' });
}
