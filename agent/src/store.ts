// Conversation history (ADR 0035): one JSON file per conversation under the data directory, written atomically
// (temporary file, then rename). The panel lists them and reopens any; the CLI's own session is resumed from
// `agentSessionId`.

import { mkdir, readdir, readFile, rename, rm, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

import type { ChatEventEnvelope } from './events.ts';

export interface Conversation {
  id: string;
  title: string;
  agent: string;
  model: string;
  /** What the conversation may do: `chat` (a person is there and approves the changes) or `job` (unattended, the consolidation: a fixed set of note tools runs, nothing else). */
  policy?: 'chat' | 'job';
  /** The CLI's session, set by its first process; resumed by the next ones. */
  agentSessionId?: string;
  createdAt: string;
  updatedAt: string;
  events: ChatEventEnvelope[];
}

export const idPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

/** A conversation keeps its latest events only: enough to read it back, bounded on disk. */
export const maxStoredEvents = 2000;

/** ... and in size: a conversation of long answers drops its oldest events beyond this many bytes of JSON. */
export const maxStoredBytes = 4 * 1024 * 1024;

export class ConversationStore {
  private readonly dir: string;
  private readonly writes = new Map<string, Promise<void>>();
  /** Saves that wait for the one before them: the conversation is written as it is when its turn comes, so one waiting save is enough. */
  private readonly waiting = new Map<string, Promise<void>>();
  private readonly report: (message: string) => void;

  constructor(dataDir: string, report: (message: string) => void = (message) => process.stderr.write(`${message}\n`)) {
    this.dir = join(dataDir, 'conversations');
    this.report = report;
  }

  async init(): Promise<void> {
    await mkdir(this.dir, { recursive: true, mode: 0o700 });
  }

  async loadAll(): Promise<Conversation[]> {
    const names = (await readdir(this.dir)).filter((name) => name.endsWith('.json'));
    const conversations: Conversation[] = [];
    for (const name of names) {
      const id = name.slice(0, -'.json'.length);
      if (!idPattern.test(id)) {
        continue;
      }
      try {
        const conversation = JSON.parse(await readFile(join(this.dir, name), 'utf8')) as Partial<Conversation>;
        // A file that parses but is not a conversation of that name (damaged, or put there) is skipped like a damaged one.
        if (conversation.id === id && Array.isArray(conversation.events)) {
          conversations.push(conversation as Conversation);
        }
      } catch {
        // A damaged file is skipped, never fatal.
      }
    }
    return conversations;
  }

  /**
   * Saves after any previous save of the same conversation, so writes never overtake each other. While a save waits for
   * the one before it, a new request joins it (the latest state is what gets written). A failure is reported, never thrown:
   * a full disk must not stop a conversation that is running.
   */
  save(conversation: Conversation): Promise<void> {
    const waiting = this.waiting.get(conversation.id);
    if (waiting) {
      return waiting;
    }
    const previous = this.writes.get(conversation.id) ?? Promise.resolve();
    const next = previous
      .then(() => {
        this.waiting.delete(conversation.id);
        return this.write(conversation);
      })
      .catch((error: unknown) => {
        this.report(
          `Could not save conversation ${conversation.id}: ${error instanceof Error ? error.message : 'unknown error'}`,
        );
      });
    this.waiting.set(conversation.id, next);
    this.writes.set(conversation.id, next);
    return next;
  }

  async remove(id: string): Promise<void> {
    await (this.writes.get(id) ?? Promise.resolve());
    this.writes.delete(id);
    this.waiting.delete(id);
    await rm(join(this.dir, `${id}.json`), { force: true });
  }

  private async write(conversation: Conversation): Promise<void> {
    if (conversation.events.length > maxStoredEvents) {
      conversation.events.splice(0, conversation.events.length - maxStoredEvents);
    }
    let json = JSON.stringify(conversation);
    while (json.length > maxStoredBytes && conversation.events.length > 1) {
      conversation.events.splice(0, Math.max(1, Math.ceil(conversation.events.length / 10)));
      json = JSON.stringify(conversation);
    }
    const file = join(this.dir, `${conversation.id}.json`);
    const temporary = `${file}.tmp`;
    await writeFile(temporary, json, { mode: 0o600 });
    await rename(temporary, file);
  }
}
