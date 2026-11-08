import type { ReactNode } from 'react';
import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';

/** A link in an answer opens in a new tab without giving the page's address away. */
function Link({ children, href }: Readonly<{ children?: ReactNode; href?: string }>) {
  return (
    <a href={href} rel="noreferrer noopener" target="_blank">
      {children}
    </a>
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
