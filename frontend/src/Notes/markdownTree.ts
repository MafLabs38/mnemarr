/** The few parts of the Markdown tree (mdast) that the plugins of the reader read and change. */
export interface MarkdownNode {
  type: string;
  value?: string;
  children?: MarkdownNode[];
  data?: { hName?: string; hProperties?: Record<string, unknown> };
}
