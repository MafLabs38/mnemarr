/**
 * The schemes the server gives to what a note links to, in the Markdown it sends to be shown (the server decides what a `[[wikilink]]`
 * means; the interface only turns the result into addresses): a note is `mnemarr-note:{source}/{path}`, an image of the note's own source
 * is `mnemarr-file:{path}`, and what leads nowhere the person may read is `mnemarr-unresolved:`. Paths are URL-encoded. The server rewrites ordinary relative links and
 * images the same way, so the interface resolves no address itself.
 */
export const NOTE_SCHEME = 'mnemarr-note:';
export const FILE_SCHEME = 'mnemarr-file:';
export const UNRESOLVED_SCHEME = 'mnemarr-unresolved:';

/** The source and the path of a `mnemarr-note:` address, or null when it is not one the server could have written. */
export function parseNoteAddress(address: string): { sourceId: string; path: string } | null {
  const rest = address.slice(NOTE_SCHEME.length);
  const slash = rest.indexOf('/');
  if (!address.startsWith(NOTE_SCHEME) || slash <= 0) {
    return null;
  }
  try {
    return { sourceId: rest.slice(0, slash), path: decodeURIComponent(rest.slice(slash + 1)) };
  } catch {
    return null;
  }
}
