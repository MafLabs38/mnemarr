// Conversation history (D-87): one JSON file per conversation under the data directory, written atomically
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

  constructor(dataDir: string) {
    this.dir = join(dataDir, 'conversations');
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
        conversations.push(JSON.parse(await readFile(join(this.dir, name), 'utf8')) as Conversation);
      } catch {
        // A damaged file is skipped, never fatal.
      }
    }
    return conversations;
  }

  /** Saves after any previous save of the same conversation, so writes never overtake each other. */
  save(conversation: Conversation): Promise<void> {
    const previous = this.writes.get(conversation.id) ?? Promise.resolve();
    const next = previous.then(() => this.write(conversation)).catch(() => undefined);
    this.writes.set(conversation.id, next);
    return next;
  }

  async remove(id: string): Promise<void> {
    await (this.writes.get(id) ?? Promise.resolve());
    this.writes.delete(id);
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
