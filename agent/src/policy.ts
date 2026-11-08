// What a tool call may do (ADR 0035), decided by the runner whatever the CLI: Mnemarr's read tools run, Mnemarr's
// other tools (the tools that change notes, sources or machines) wait for the operator, anything else is refused without asking.

/** The MCP server's name in the CLI's configuration: its tools are `mcp__mnemarr__<tool>`. */
export const serverName = 'mnemarr';

export type ToolClass = 'read' | 'write' | 'forbidden';

const toolPattern = /^mcp__mnemarr__([a-z][a-z_]{0,63})$/;
const readPrefixes = ['get_', 'list_', 'find_', 'read_'];
/** `search` is a read tool whose name has no prefix. */
const readNames = new Set(['search']);

export function classify(toolName: string): ToolClass {
  const match = toolPattern.exec(toolName);
  if (!match) {
    return 'forbidden';
  }
  return readNames.has(match[1]) || readPrefixes.some((prefix) => match[1].startsWith(prefix)) ? 'read' : 'write';
}

/** `mcp__mnemarr__create_note` → `create_note`, for people. */
export function shortToolName(toolName: string): string {
  return toolPattern.exec(toolName)?.[1] ?? toolName;
}

/** Mnemarr's read tools at the time of writing, pre-allowed on the CLI's command line (others are classified). */
export const knownReadTools = ['search', 'list_sources', 'list_notes', 'read_note', 'list_tags', 'get_status'].map(
  (tool) => `mcp__${serverName}__${tool}`,
);

/**
 * What an unattended job (the consolidation of the conversations into the memory vault) may change: notes, and nothing else. No removal: a note that is
 * wrong is replaced by a correct one, and the server refuses anything outside the memory vault as well.
 */
export const jobWriteTools = ['create_note', 'update_note', 'append_to_note', 'rename_note'];

/** Whether an unattended job may call this tool without anybody to approve: Mnemarr's reads, and the note tools of {@link jobWriteTools}. */
export function jobMayCall(toolName: string): boolean {
  return classify(toolName) === 'read' || jobWriteTools.includes(shortToolName(toolName));
}

export const rejectedMessage = 'The operator rejected this change. Do not retry it; ask what they want instead.';
export const forbiddenMessage = 'Only Mnemarr tools are available in this chat.';
export const cancelledMessage = 'The operator stopped this turn.';
