import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';

import { type Conversation, ConversationStore, maxStoredBytes, maxStoredEvents } from '../src/store.ts';

async function stored(events: number, textLength: number) {
  const dir = mkdtempSync(join(tmpdir(), 'mnemarr-store-test-'));
  const store = new ConversationStore(dir);
  await store.init();
  const conversation = {
    id: '0d5c2f1e-7a3b-4c8d-9e0f-1a2b3c4d5e6f',
    title: '',
    agent: 'fake',
    model: 'small',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    events: Array.from({ length: events }, (_, seq) => ({
      seq,
      at: '2026-01-01T00:00:00Z',
      type: 'text',
      text: 'x'.repeat(textLength),
    })),
  } as unknown as Conversation;
  await store.save(conversation);
  return JSON.parse(readFileSync(join(dir, 'conversations', `${conversation.id}.json`), 'utf8')) as Conversation;
}

test('a conversation keeps its latest events only', async () => {
  const file = await stored(maxStoredEvents + 50, 1);

  assert.equal(file.events.length, maxStoredEvents);
  assert.equal(file.events[0].seq, 50);
});

test('a conversation of long answers drops its oldest events beyond the size cap', async () => {
  const file = await stored(1000, 10_000);

  assert.ok(JSON.stringify(file).length <= maxStoredBytes);
  assert.equal(file.events.at(-1)?.seq, 999, 'the newest event is kept');
  assert.ok(file.events.length > 100, 'most of what fits is kept');
});
