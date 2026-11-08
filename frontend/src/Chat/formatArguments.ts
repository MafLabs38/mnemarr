/** A tool's arguments as indented JSON, for people (the panel never interprets them). */
export default function formatArguments(input: unknown): string {
  if (input === undefined || input === null) {
    return '{}';
  }
  try {
    return JSON.stringify(input, null, 2);
  } catch {
    // Only a value that JSON cannot hold ends up here: a bigint prints as itself, a cycle as a placeholder.
    return typeof input === 'bigint' ? input.toString() : '{…}';
  }
}
