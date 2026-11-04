import { noteToRead } from './notes';

/**
 * The schemes the server gives to what a note links to, in the Markdown it sends to be shown (the server decides what a `[[wikilink]]`
 * means; the interface only turns the result into addresses): a note is `mnemarr-note:{source}/{path}`, an image of the note's own source
 * is `mnemarr-file:{path}`, and what leads nowhere the person may read is `mnemarr-unresolved:`. Paths are URL-encoded. The server rewrites ordinary relative links and
 * images the same way, so the interface resolves no address itself.
 */
export const NOTE_SCHEME = 'mnemarr-note:';
export const FILE_SCHEME = 'mnemarr-file:';
export const UNRESOLVED_SCHEME = 'mnemarr-unresolved:';

/**
 * The source and the path of a `mnemarr-note:` address, or null when it is not one the server could have written. A note is untrusted and can
 * write such an address itself (in a definition the server does not rewrite), so the source must be an id and the path must stay inside it.
 */
export function parseNoteAddress(address: string): { sourceId: string; path: string; heading: string | null } | null {
  const hash = address.indexOf('#');
  const rest = address.slice(NOTE_SCHEME.length, hash < 0 ? undefined : hash);
  const slash = rest.indexOf('/');
  if (!address.startsWith(NOTE_SCHEME) || slash <= 0) {
    return null;
  }
  try {
    const note = noteToRead(rest.slice(0, slash), decodeURIComponent(rest.slice(slash + 1)));
    const heading = hash < 0 ? null : decodeURIComponent(address.slice(hash + 1));
    // A heading is an id the interface draws (see `headingSlug`): anything else is not one the server wrote.
    return note && (heading === null || /^[\p{L}\p{N}_-]+$/u.test(heading)) ? { ...note, heading } : null;
  } catch {
    return null;
  }
}

/** The path, in the source of the note, that a `mnemarr-file:` address names, or null when it is not one that stays inside the source. */
export function parseFileAddress(sourceId: string, address: string): string | null {
  try {
    return noteToRead(sourceId, decodeURIComponent(address.slice(FILE_SCHEME.length)))?.path ?? null;
  } catch {
    return null;
  }
}
