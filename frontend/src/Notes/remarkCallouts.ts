import styles from './Notes.module.css';

/** The few parts of the Markdown tree the plugin reads and changes. */
interface Node {
  type: string;
  value?: string;
  children?: Node[];
  data?: { hName?: string; hProperties?: Record<string, unknown> };
}

/**
 * Obsidian callouts: a quote whose first line is `[!note] A title` becomes a box with a title, and `[!note]-` (or `+`) one that folds,
 * closed (or open). The title keeps its inline formatting; what follows is Markdown, rendered like the rest. The type is a short word,
 * and only letters and dashes are kept (it ends up in a class name).
 */
export default function remarkCallouts() {
  return (tree: Node) => visit(tree);
}

function visit(node: Node) {
  if (node.type === 'blockquote') {
    markCallout(node);
  }
  node.children?.forEach(visit);
}

/** The first line of a paragraph (the title) and what comes after it (the start of the body). */
function splitFirstLine(nodes: Node[]): { title: Node[]; body: Node[] } {
  const title: Node[] = [];
  const body: Node[] = [];
  let inTitle = true;
  for (const node of nodes) {
    if (!inTitle) {
      body.push(node);
    } else if (node.type === 'text' && node.value?.includes('\n')) {
      const newline = node.value.indexOf('\n');
      title.push({ ...node, value: node.value.slice(0, newline) });
      body.push({ ...node, value: node.value.slice(newline + 1) });
      inTitle = false;
    } else if (node.type === 'break') {
      inTitle = false;
    } else {
      title.push(node);
    }
  }
  return { title, body: body.filter((node) => node.type !== 'text' || node.value !== '') };
}

/** Leading spaces and tabs only: a line break after the marker means the callout has no title. */
function trimSpaces(text: string): string {
  let start = 0;
  while (text[start] === ' ' || text[start] === '\t') {
    start++;
  }
  return text.slice(start);
}

function markCallout(quote: Node) {
  const paragraph = quote.children?.[0];
  const first = paragraph?.type === 'paragraph' ? paragraph.children?.[0] : undefined;
  if (first?.type !== 'text' || !first.value?.startsWith('[!')) {
    return;
  }
  const close = first.value.indexOf(']');
  const kind =
    close < 0
      ? ''
      : first.value
          .slice(2, close)
          .replaceAll(/[^a-zA-Z-]/g, '')
          .toLowerCase();
  if (kind === '') {
    return;
  }
  let rest = first.value.slice(close + 1);
  const fold = rest[0] === '-' || rest[0] === '+' ? rest[0] : null;
  if (fold) {
    rest = rest.slice(1);
  }
  const { title, body } = splitFirstLine([
    { ...first, value: trimSpaces(rest) },
    ...(paragraph?.children?.slice(1) ?? []),
  ]);
  const label: Node[] =
    title.length > 0 && title.some((node) => node.type !== 'text' || node.value)
      ? title
      : [{ type: 'text', value: (kind[0]?.toUpperCase() ?? '') + kind.slice(1) }];
  const heading: Node = fold
    ? { type: 'paragraph', children: label, data: { hName: 'summary' } }
    : { type: 'paragraph', children: [{ type: 'strong', children: label }] };
  const others = quote.children?.slice(1) ?? [];
  quote.children = [heading, ...(body.length > 0 ? [{ type: 'paragraph', children: body }] : []), ...others];
  quote.data = {
    hName: fold ? 'details' : 'div',
    hProperties: { className: [styles.callout], 'data-callout': kind, ...(fold === '+' ? { open: true } : {}) },
  };
}
