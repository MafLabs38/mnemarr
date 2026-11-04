const MAX_LENGTH = 200;
const BACKSLASH = String.fromCodePoint(92);
const IMAGE_EXTENSIONS = ['.png', '.jpg', '.jpeg', '.gif', '.webp', '.avif', '.bmp'];

/** The scheme the reader gives to a `[[wikilink]]` it turned into a Markdown link, and to an embedded file. */
export const NOTE_SCHEME = 'mnemarr-note:';
export const FILE_SCHEME = 'mnemarr-file:';

/**
 * What a link names, the way the server stores and matches it: no heading, no `.md`, trimmed, lower case, `/` for a path. Null when
 * there is nothing to point at. It must stay in step with `MarkdownNotes.NormalizeTarget` on the server.
 */
export function normalizeTarget(target: string): string | null {
  const hash = target.indexOf('#');
  let name = trimSlashes((hash >= 0 ? target.slice(0, hash) : target).trim().replaceAll(BACKSLASH, '/'));
  for (const extension of ['.md', '.markdown']) {
    if (name.toLowerCase().endsWith(extension)) {
      name = name.slice(0, -extension.length);
    }
  }
  return name.length === 0 || name.length > MAX_LENGTH ? null : name.toLowerCase();
}

function escapeText(text: string): string {
  return [BACKSLASH, '[', ']'].reduce((escaped, char) => escaped.replaceAll(char, BACKSLASH + char), text);
}

function trimSlashes(text: string): string {
  let start = 0;
  let end = text.length;
  while (start < end && text[start] === '/') {
    start++;
  }
  while (end > start && text[end - 1] === '/') {
    end--;
  }
  return text.slice(start, end);
}

function isImage(file: string): boolean {
  const lower = file.toLowerCase();
  return IMAGE_EXTENSIONS.some((extension) => lower.endsWith(extension));
}

/** A `[[...]]` (its inside), as a Markdown link or image; the text unchanged when it names nothing. */
function convert(inner: string, embedded: boolean): string | null {
  const bar = inner.indexOf('|');
  const target = (bar >= 0 ? inner.slice(0, bar) : inner).trim();
  const alias = bar >= 0 ? inner.slice(bar + 1).trim() : '';
  if (embedded && isImage(target.split('#')[0] ?? '')) {
    const file = (target.split('#')[0] ?? '').trim().replaceAll(BACKSLASH, '/');
    return `![${escapeText(file)}](${FILE_SCHEME}${encodeURIComponent(file)})`;
  }
  const normalized = normalizeTarget(target);
  if (normalized === null) {
    return null;
  }
  return `[${escapeText(alias || target)}](${NOTE_SCHEME}${encodeURIComponent(normalized)})`;
}

/** The `[[...]]` that starts at `index` (after a `!` when embedded), converted, and where the line goes on; null when it is not one. */
function convertAt(line: string, index: number): { text: string; next: number } | null {
  const embedded = line[index] === '!';
  const start = embedded ? index + 1 : index;
  if (!line.startsWith('[[', start)) {
    return null;
  }
  const close = line.indexOf(']]', start + 2);
  const converted = close < 0 ? null : convert(line.slice(start + 2, close), embedded);
  return converted === null ? null : { text: converted, next: close + 2 };
}

function convertLine(line: string): string {
  let result = '';
  let index = 0;
  let inCode = false;
  while (index < line.length) {
    inCode = line[index] === '`' ? !inCode : inCode;
    const converted = inCode ? null : convertAt(line, index);
    result += converted?.text ?? line[index];
    index = converted?.next ?? index + 1;
  }
  return result;
}

/**
 * Turns the `[[wikilinks]]` and `![[embeds]]` of a note into Markdown links and images with the schemes above, outside code (fenced or
 * inline), so that the renderer can resolve them against what the server says. A link that names nothing stays as it was written.
 */
export function linkWikilinks(markdown: string): string {
  let inFence = false;
  return markdown
    .split('\n')
    .map((line) => {
      if (line.startsWith('```') || line.startsWith('~~~')) {
        inFence = !inFence;
        return line;
      }
      return inFence ? line : convertLine(line);
    })
    .join('\n');
}

/** The path of a relative link or image from the note that holds it, or null when it leaves the source. */
export function resolveRelative(notePath: string, reference: string): string | null {
  const clean = reference.split('#')[0]?.split('?')[0] ?? '';
  let decoded: string;
  try {
    decoded = decodeURIComponent(clean);
  } catch {
    return null;
  }
  const parts = notePath.split('/').slice(0, -1);
  for (const part of decoded.replaceAll(BACKSLASH, '/').split('/')) {
    if (part === '..') {
      if (parts.length === 0) {
        return null;
      }
      parts.pop();
    } else if (part !== '.' && part !== '') {
      parts.push(part);
    }
  }
  return parts.length === 0 ? null : parts.join('/');
}
