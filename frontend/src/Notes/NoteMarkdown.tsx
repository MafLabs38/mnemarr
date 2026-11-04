import { Children, createContext, type ReactNode, useContext } from 'react';
import Markdown, { type Components } from 'react-markdown';
import { Link } from 'react-router-dom';
import remarkGfm from 'remark-gfm';

import { apiUrl } from 'Helpers/Api/apiGet';
import translate from 'Utilities/String/translate';

import { FILE_SCHEME, NOTE_SCHEME, parseFileAddress, parseNoteAddress, UNRESOLVED_SCHEME } from './addresses';
import MermaidDiagram from './MermaidDiagram';
import { filePath, type Note, noteAddress } from './notes';
import styles from './Notes.module.css';
import remarkCallouts from './remarkCallouts';
import remarkLineBreaks from './remarkLineBreaks';

const MERMAID_CLASS = 'language-mermaid';
const ALLOWED = new Set(['http:', 'https:', 'mailto:', NOTE_SCHEME, FILE_SCHEME, UNRESOLVED_SCHEME]);

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
  if (href?.startsWith(NOTE_SCHEME)) {
    const target = parseNoteAddress(href);
    return target ? <Link to={noteAddress(target)}>{children}</Link> : <span>{children}</span>;
  }
  if (href?.startsWith(UNRESOLVED_SCHEME)) {
    return (
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
  // What the server did not rewrite (a relative address that leaves the source, a file that is not a note) is text here.
  return <span>{children}</span>;
}

function NoteImage({ src, alt }: Readonly<{ src?: string; alt?: string }>) {
  const note = useNoteBeingRendered();
  const path = src?.startsWith(FILE_SCHEME) ? parseFileAddress(note.sourceId, src) : null;
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
  return (
    <NoteContext.Provider value={note}>
      <div className={styles.markdown}>
        <Markdown components={components} remarkPlugins={remarkPlugins} urlTransform={transformUrl}>
          {note.rendered}
        </Markdown>
      </div>
    </NoteContext.Provider>
  );
}
