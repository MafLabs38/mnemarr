import { Children, isValidElement } from 'react';
import Markdown from 'react-markdown';
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
const ALLOWED = ['http:', 'https:', 'mailto:', NOTE_SCHEME, FILE_SCHEME];

/**
 * What a link or an image may point at. A note is untrusted text (it can quote a web page or a tool's output): only these schemes
 * survive, a relative address is the source's own, and anything else (`javascript:`, `data:`) becomes nothing.
 */
function transformUrl(url: string): string {
  const scheme = /^([a-zA-Z][a-zA-Z0-9+.-]*:)/.exec(url)?.[1]?.toLowerCase();
  return scheme === undefined || ALLOWED.includes(scheme) ? url : '';
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

/** The Markdown of a note, rendered without raw HTML, with its wikilinks resolved, its images fetched from its own source, and its callouts. */
export default function NoteMarkdown({ note }: Readonly<{ note: Note }>) {
  const links = new Map(note.links.map((link) => [link.target, link.note]));

  return (
    <div className={styles.markdown}>
      <Markdown
        components={{
          code: ({ className, children }) =>
            className === MERMAID_CLASS ? (
              <MermaidDiagram source={String(children).replace(/\n$/, '')} />
            ) : (
              <code className={className}>{children}</code>
            ),
          pre: ({ children }) => {
            const code = Children.toArray(children)[0];
            return isValidElement<{ className?: string }>(code) && code.props.className === MERMAID_CLASS ? (
              <>{children}</>
            ) : (
              <pre>{children}</pre>
            );
          },
          a: ({ href, children }) => {
            if (href?.startsWith(NOTE_SCHEME)) {
              const target = links.get(decode(href.slice(NOTE_SCHEME.length)));
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
              href && isRelative(href) && /\.(md|markdown)(#.*)?$/i.test(href)
                ? resolveRelative(note.path, href)
                : null;
            return path ? (
              <Link to={noteAddress({ sourceId: note.sourceId, path })}>{children}</Link>
            ) : (
              <span>{children}</span>
            );
          },
          img: ({ src, alt }) => {
            const embedded = src?.startsWith(FILE_SCHEME)
              ? note.embeds[decode(src.slice(FILE_SCHEME.length))]
              : undefined;
            const path = embedded ?? (src && isRelative(src) ? resolveRelative(note.path, src) : null);
            return path ? (
              <img alt={alt ?? ''} loading="lazy" src={apiUrl(filePath(note.sourceId, path))} />
            ) : (
              <span className={styles.unresolved}>{translate('ImageNotShown', { name: alt ?? '' })}</span>
            );
          },
        }}
        remarkPlugins={[remarkGfm, remarkCallouts, remarkLineBreaks]}
        urlTransform={transformUrl}
      >
        {linkWikilinks(note.markdown)}
      </Markdown>
    </div>
  );
}
