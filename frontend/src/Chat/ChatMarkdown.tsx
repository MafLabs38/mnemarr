import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';

/**
 * The answer of the model, as Markdown. Raw HTML is not interpreted (the library's default: it is shown as text), and a link opens in a new tab without giving the page's
 * address away: what a model writes is text from outside, never markup of ours.
 */
export default function ChatMarkdown({ text }: Readonly<{ text: string }>) {
  return (
    <Markdown
      components={{
        a: ({ children, href }) => (
          <a href={href} rel="noreferrer noopener" target="_blank">
            {children}
          </a>
        ),
      }}
      remarkPlugins={[remarkGfm]}
    >
      {text}
    </Markdown>
  );
}
