interface Node {
  type: string;
  value?: string;
  children?: Node[];
}

/** True for exactly `<br>`, `<br/>` or `<br />`, in any case: the one piece of HTML that notes use for a line break inside a table cell. */
function isLineBreak(node: Node): boolean {
  if (node.type !== 'html' || node.value === undefined) {
    return false;
  }
  const tag = node.value.trim().toLowerCase().replaceAll(/\s+/g, '');
  return tag === '<br>' || tag === '<br/>';
}

/**
 * Raw HTML is never rendered (a note is untrusted), but `<br>` is how Obsidian notes break a line inside a table cell. It becomes a real
 * line break; nothing else about the HTML is read, so no markup can come through it.
 */
export default function remarkLineBreaks() {
  return (tree: Node) => visit(tree);
}

function visit(node: Node) {
  node.children?.forEach((child, index) => {
    if (isLineBreak(child)) {
      (node.children as Node[])[index] = { type: 'break' };
    } else {
      visit(child);
    }
  });
}
