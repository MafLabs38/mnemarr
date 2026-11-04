import { Children, createContext, type ReactNode, useContext, useMemo } from 'react';
import Markdown, { type Components } from 'react-markdown';
import { Link } from 'react-router-dom';
import remarkGfm from 'remark-gfm';

import { apiUrl } from 'Helpers/Api/apiGet';
import translate from 'Utilities/String/translate';

import MermaidDiagram from './MermaidDiagram';
import { filePath, type Note, noteAddress } from './notes';
import styles from './Notes.module.css';
import remarkCallouts from './remarkCallouts';
import remarkLineBreaks from './remarkLineBreaks';
import { FILE_SCHEME, linkWikilinks, NOTE_SCHEME, resolveRelative } from './wikilinks';

const MERMAID_CLASS = 'language-mermaid';
const ALLOWED = new Set(['http:', 'https:', 'mailto:', NOTE_SCHEME, FILE_SCHEME]);

/** The note being rendered: its links and embeds say where an address leads, and its path is where a relative address starts. */
const NoteContext = createContext<Note | null>(null);

/**
 * What a link or an image may point at. A note is untrusted text (it can quote a web page or a tool's output): only these schemes
 * survive, a relative address is the source's own, and anything else (`javascript:`, `data:`) becomes nothing.
 */
function transformUrl(url: string): string {
  const scheme = /^([a-zA-Z][a-zA-Z0-9+.-]*:)/.exec(url)?.[1]?.toLowerCase();
  return scheme === undefined || ALLOWED.has(scheme) ? url : '';
}

function isRelative(url: string): boolean {
  return !/^([a-zA-Z][a-zA-Z0-9+.-]*:|\/|#)/.test(url);
}

function decode(value: string): string {
  try {
    return decodeURIComponent(value);
  } catch {
    return value;
  }
}

function useNoteBeingRendered(): Note {
  const note = useContext(NoteContext);
  if (note === null) {
    throw new Error('A note is rendered by NoteMarkdown.');
  }
  return note;
}

function NoteCode({ className, children }: Readonly<{ className?: string; children?: ReactNode }>) {
  return className === MERMAID_CLASS ? (
    <MermaidDiagram source={String(children).replace(/\n$/, '')} />
  ) : (
    <code className={className}>{children}</code>
  );
}

function NotePre({ children }: Readonly<{ children?: ReactNode }>) {
  const code = Children.toArray(children)[0];
  const isDiagram =
    typeof code === 'object' && 'props' in code && (code.props as { className?: string }).className === MERMAID_CLASS;
  return isDiagram ? <>{children}</> : <pre>{children}</pre>;
}

function NoteAnchor({ href, children }: Readonly<{ href?: string; children?: ReactNode }>) {
  const note = useNoteBeingRendered();
  if (href?.startsWith(NOTE_SCHEME)) {
    const target = note.links.find((link) => link.target === decode(href.slice(NOTE_SCHEME.length)))?.note;
    return target ? (
      <Link to={noteAddress(target)}>{children}</Link>
    ) : (
      <span className={styles.unresolved} title={translate('UnresolvedLink')}>
        {children}
      </span>
    );
  }
  if (href && /^(https?:|mailto:)/i.test(href)) {
    return (
      <a href={href} rel="noopener noreferrer nofollow" target="_blank">
        {children}
      </a>
    );
  }
  const path =
    href && isRelative(href) && /\.(md|markdown)(#.*)?$/i.test(href) ? resolveRelative(note.path, href) : null;
  return path ? <Link to={noteAddress({ sourceId: note.sourceId, path })}>{children}</Link> : <span>{children}</span>;
}

function NoteImage({ src, alt }: Readonly<{ src?: string; alt?: string }>) {
  const note = useNoteBeingRendered();
  // The key comes from the note, and `embeds` is a plain object: a name such as `constructor` must not find what every object inherits.
  const name = src?.startsWith(FILE_SCHEME) ? decode(src.slice(FILE_SCHEME.length)) : undefined;
  const embedded = name !== undefined && Object.hasOwn(note.embeds, name) ? note.embeds[name] : undefined;
  const path = embedded ?? (src && isRelative(src) ? resolveRelative(note.path, src) : null);
  return path ? (
    <img alt={alt ?? ''} loading="lazy" src={apiUrl(filePath(note.sourceId, path))} />
  ) : (
    <span className={styles.unresolved}>{translate('ImageNotShown', { name: alt ?? '' })}</span>
  );
}

// Defined once, outside any component: a new function at each render would be a new component type, and the diagrams would be drawn again.
const components: Components = { code: NoteCode, pre: NotePre, a: NoteAnchor, img: NoteImage };
const remarkPlugins = [remarkGfm, remarkCallouts, remarkLineBreaks];

/** The Markdown of a note, rendered without raw HTML, with its wikilinks resolved, its images fetched from its own source, and its callouts. */
export default function NoteMarkdown({ note }: Readonly<{ note: Note }>) {
  const markdown = useMemo(() => linkWikilinks(note.markdown), [note.markdown]);

  return (
    <NoteContext.Provider value={note}>
      <div className={styles.markdown}>
        <Markdown components={components} remarkPlugins={remarkPlugins} urlTransform={transformUrl}>
          {markdown}
        </Markdown>
      </div>
    </NoteContext.Provider>
  );
}
