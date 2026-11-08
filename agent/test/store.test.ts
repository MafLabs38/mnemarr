import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
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

test('saves that pile up behind a slow one are written once, with the latest state', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'mnemarr-store-test-'));
  const store = new ConversationStore(dir);
  await store.init();
  const conversation = {
    id: '1d5c2f1e-7a3b-4c8d-9e0f-1a2b3c4d5e6f',
    title: 'first',
    agent: 'fake',
    model: 'small',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    events: [],
  } as unknown as Conversation;

  const saves = [store.save(conversation)];
  conversation.title = 'second';
  saves.push(store.save(conversation));
  conversation.title = 'third';
  saves.push(store.save(conversation));
  await Promise.all(saves);

  assert.equal(saves[1], saves[2], 'the two that waited share one write');
  assert.equal(
    (JSON.parse(readFileSync(join(dir, 'conversations', `${conversation.id}.json`), 'utf8')) as Conversation).title,
    'third',
  );
});

test('a save that fails is reported and never thrown', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'mnemarr-store-test-'));
  const reports: string[] = [];
  const store = new ConversationStore(join(dir, 'missing'), (message) => reports.push(message));

  await store.save({ id: '2d5c2f1e-7a3b-4c8d-9e0f-1a2b3c4d5e6f', events: [] } as unknown as Conversation);

  assert.equal(reports.length, 1);
  assert.match(reports[0], /Could not save conversation 2d5c2f1e/);
});

test('loading skips what is not a conversation of the name it has: another id, no events, damaged JSON, a name that is not an id', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'mnemarr-store-test-'));
  const store = new ConversationStore(dir);
  await store.init();
  const folder = join(dir, 'conversations');
  const good = '3d5c2f1e-7a3b-4c8d-9e0f-1a2b3c4d5e6f';
  writeFileSync(join(folder, `${good}.json`), JSON.stringify({ id: good, events: [] }));
  writeFileSync(join(folder, '4d5c2f1e-7a3b-4c8d-9e0f-1a2b3c4d5e6f.json'), JSON.stringify({ id: good, events: [] }));
  writeFileSync(
    join(folder, '5d5c2f1e-7a3b-4c8d-9e0f-1a2b3c4d5e6f.json'),
    JSON.stringify({ id: '5d5c2f1e-7a3b-4c8d-9e0f-1a2b3c4d5e6f' }),
  );
  writeFileSync(join(folder, '6d5c2f1e-7a3b-4c8d-9e0f-1a2b3c4d5e6f.json'), 'not json');
  writeFileSync(join(folder, 'notes.json'), JSON.stringify({ id: 'notes', events: [] }));
  mkdirSync(join(folder, 'sub'));

  assert.deepEqual(
    (await store.loadAll()).map((conversation) => conversation.id),
    [good],
  );
});

test('a saved conversation is read back, and removing it waits for the save that is pending', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'mnemarr-store-test-'));
  const store = new ConversationStore(dir);
  await store.init();
  const conversation = {
    id: '7d5c2f1e-7a3b-4c8d-9e0f-1a2b3c4d5e6f',
    title: 'kept',
    events: [],
  } as unknown as Conversation;

  const saving = store.save(conversation);
  await store.remove(conversation.id);
  await saving;

  assert.deepEqual(await store.loadAll(), []);
});
