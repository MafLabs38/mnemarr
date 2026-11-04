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
  /** What the link names, lower case, as the server matches it. */
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

/** The address of the reader for a note: the one place it is built, so that every link to a note goes the same way. */
export function noteAddress(note: Pick<NoteRef, 'sourceId' | 'path'>): string {
  return `/notes?${new URLSearchParams({ source: note.sourceId, path: note.path }).toString()}`;
}

function encodePath(path: string): string {
  return path.split('/').map(encodeURIComponent).join('/');
}

/** The notes the person may read, of one tag when asked. */
export function useNoteTree(tag: string | null) {
  return useApiQuery<NoteEntry[]>(tag ? `/notes?tag=${encodeURIComponent(tag)}` : '/notes');
}

export function useTags() {
  return useApiQuery<Tag[]>('/notes/tags');
}

/** A note with its links and backlinks. Nothing is asked until a note is chosen. */
export function useNote(sourceId: string | null, path: string | null) {
  return useApiQuery<Note>(`/notes/${sourceId ?? ''}/${encodePath(path ?? '')}`, {
    enabled: sourceId !== null && path !== null,
  });
}

/** The address of a file of a source (an image), served by the authenticated API from that source only. */
export function filePath(sourceId: string, path: string): string {
  return `/files/${sourceId}/${encodePath(path)}`;
}
