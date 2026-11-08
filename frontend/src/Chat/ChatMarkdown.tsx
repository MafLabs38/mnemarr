import type { ReactNode } from 'react';
import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';

function hostOf(href: string | undefined): string | undefined {
  try {
    return href ? new URL(href).host : undefined;
  } catch {
    return undefined;
  }
}

/**
 * A link in an answer opens in a new tab without giving the page's address away, and shows where it goes: a model can write any
 * text for an address, so the host stays visible beside it, and the whole address is its title.
 */
function Link({ children, href }: Readonly<{ children?: ReactNode; href?: string }>) {
  const host = hostOf(href);
  const shown = typeof children === 'string' && children !== href;
  return (
    <>
      <a href={href} rel="noreferrer noopener" target="_blank" title={href}>
        {children}
      </a>
      {shown && host ? <small> ({host})</small> : null}
    </>
  );
}

const components = { a: Link };

/**
 * The answer of the model, as Markdown. Raw HTML is not interpreted (the library's default: it is shown as text): what a model writes is text from outside, never markup of ours.
 */
export default function ChatMarkdown({ text }: Readonly<{ text: string }>) {
  return (
    <Markdown components={components} remarkPlugins={[remarkGfm]}>
      {text}
    </Markdown>
  );
}
