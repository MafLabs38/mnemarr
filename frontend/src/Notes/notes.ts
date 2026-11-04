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
  /** The link as the note wrote it (lower case, no extension, no heading): what is found in the text. */
  target: string;
  /** The note it leads to, or null when no note the person may read has that name. */
  note: NoteRef | null;
}

export interface Note {
  sourceId: string;
  sourceName: string;
  path: string;
  title: string;
  markdown: string;
  tags: string[];
  links: NoteLink[];
  backlinks: NoteRef[];
  /** Where the files an embed names (`![[plan.png]]`) are in the source, by the name written in the note. */
  embeds: Record<string, string>;
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
export function noteAddress(note: Pick<NoteRef, 'sourceId' | 'path'>): string {
  return `/notes?${new URLSearchParams({ source: note.sourceId, path: note.path }).toString()}`;
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
