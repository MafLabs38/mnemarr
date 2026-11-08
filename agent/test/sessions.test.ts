import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { mock, test } from 'node:test';

import type { ChatEventEnvelope } from '../src/events.ts';
import { ChatError } from '../src/sessions.ts';
import { newManager, waitFor } from './fakes.ts';

const token = `mnc_${'a'.repeat(43)}`;

async function started() {
  const context = await newManager();
  const conversation = await context.manager.create(undefined, undefined, undefined);
  const events: ChatEventEnvelope[] = [];
  context.manager.subscribe(conversation.id, 0, (event) => events.push(event));
  context.manager.send(conversation.id, 'Add a note to the provider object-storage\nsecond line', token);
  return { ...context, id: conversation.id, events, process: context.adapter.last };
}

test('a message starts one process with a fresh session id, and titles the conversation', async () => {
  const { manager, process, id } = await started();
  assert.deepEqual(process.sent, ['Add a note to the provider object-storage\nsecond line']);
  assert.equal(process.options.resume, undefined);
  assert.match(process.options.sessionId, /^[0-9a-f-]{36}$/);
  assert.equal(manager.get(id).title, 'Add a note to the provider object-storage');
  assert.equal(manager.get(id).busy, true);
  assert.throws(
    () => manager.send(id, 'again', token),
    (error: ChatError) => error.status === 409,
  );
});

test('a write waits for the operator; approving answers allow with the same input', async () => {
  const { manager, process, id, events } = await started();
  const input = { section: 'providers', id: 'object-storage', notes: 'x', version: '0123456789abcdef' };
  process.options.onEvent({
    type: 'permission-request',
    requestId: 'r1',
    toolName: 'mcp__mnemarr__update_note',
    input,
  });

  assert.deepEqual(process.answers, []);
  assert.equal(manager.get(id).pendingPermissions, 1);
  assert.deepEqual(
    events
      .filter((event) => event.type === 'permission')
      .map(({ type, requestId, toolName }) => ({ type, requestId, toolName })),
    [{ type: 'permission', requestId: 'r1', toolName: 'mcp__mnemarr__update_note' }],
  );

  manager.answer(id, 'r1', true);
  assert.deepEqual(process.answers, [{ requestId: 'r1', decision: { allow: true, input } }]);
  assert.ok(events.some((event) => event.type === 'permission-resolved' && event.approved));
  assert.throws(
    () => manager.answer(id, 'r1', true),
    (error: ChatError) => error.status === 404,
  );
});

test('rejecting answers deny; reads are allowed and other tools denied without asking', async () => {
  const { manager, process, id, events } = await started();
  process.options.onEvent({
    type: 'permission-request',
    requestId: 'w',
    toolName: 'mcp__mnemarr__create_note',
    input: {},
  });
  process.options.onEvent({
    type: 'permission-request',
    requestId: 'r',
    toolName: 'mcp__mnemarr__read_note',
    input: { app: 'blog' },
  });
  process.options.onEvent({ type: 'permission-request', requestId: 'b', toolName: 'Bash', input: { command: 'id' } });
  manager.answer(id, 'w', false);

  const decisions = Object.fromEntries(process.answers.map((answer) => [answer.requestId, answer.decision.allow]));
  assert.deepEqual(decisions, { r: true, b: false, w: false });
  assert.equal(events.filter((event) => event.type === 'permission').length, 1);
});

test('cancel refuses pending writes and interrupts the CLI', async () => {
  const { manager, process, id, events } = await started();
  process.options.onEvent({
    type: 'permission-request',
    requestId: 'w',
    toolName: 'mcp__mnemarr__append_to_note',
    input: {},
  });
  manager.cancel(id);
  assert.equal(process.interrupted, 1);
  assert.equal(process.answers[0].decision.allow, false);
  assert.ok(
    events.some((event) => event.type === 'permission-resolved' && !event.approved && event.reason === 'cancelled'),
  );
});

test('stored events leave out live-only ones; a reopened conversation resumes the CLI session', async () => {
  const { manager, adapter, process, id, dataDir } = await started();
  process.options.onEvent({ type: 'session', agentSessionId: process.options.sessionId });
  process.options.onEvent({ type: 'text-delta', text: 'Do' });
  process.options.onEvent({ type: 'text', text: 'Done.' });
  process.options.onEvent({ type: 'turn-end', isError: false });
  assert.equal(manager.get(id).busy, false);

  const stored = manager.get(id).events.map((event) => event.type);
  assert.deepEqual(stored, ['user', 'text', 'turn-end']);

  // The process stops (idle timeout, restart…): the next message resumes the same CLI session.
  process.close();
  manager.send(id, 'And now?', token);
  assert.equal(adapter.processes.length, 2);
  assert.equal(adapter.last.options.resume, process.options.sessionId);

  // The save is asynchronous: wait for the file to hold the resumed session, not for a guessed time.
  const path = `${dataDir}/conversations/${id}.json`;
  await waitFor(
    () => existsSync(path) && JSON.parse(readFileSync(path, 'utf8')).agentSessionId === process.options.sessionId,
  );
  const file = JSON.parse(readFileSync(path, 'utf8'));
  assert.equal(file.agentSessionId, process.options.sessionId);
  assert.ok(!file.events.some((event: { type: string }) => event.type === 'text-delta' || event.type === 'status'));
});

test('a subscriber after a seq gets only the newer events', async () => {
  const { manager, process, id } = await started();
  process.options.onEvent({ type: 'text', text: 'one' });
  const replayed: ChatEventEnvelope[] = [];
  manager.subscribe(id, 1, (event) => replayed.push(event));
  assert.deepEqual(
    replayed.map((event) => event.type),
    ['text', 'status'],
  );
});

test('at most maxSessions processes: the idle one makes room, busy ones refuse', async () => {
  const { manager, adapter } = await newManager(1);
  const first = await manager.create(undefined, undefined, 'one');
  const second = await manager.create(undefined, undefined, 'two');
  manager.send(first.id, 'hello', token);
  assert.throws(
    () => manager.send(second.id, 'hello', token),
    (error: ChatError) => error.status === 429,
  );
  adapter.last.options.onEvent({ type: 'turn-end', isError: false });
  manager.send(second.id, 'hello', token);
  assert.equal(adapter.processes[0].closed, true);
  assert.equal(manager.list().filter((conversation) => conversation.running).length, 1);
});

test('unknown agent or model is a 400', async () => {
  const { manager } = await newManager();
  await assert.rejects(manager.create('nope', undefined, undefined), (error: ChatError) => error.status === 400);
  await assert.rejects(manager.create(undefined, 'huge', undefined), (error: ChatError) => error.status === 400);
});

function mcpKeyOf(process: { options: { mcpConfigFile: string } }): string {
  const config = JSON.parse(readFileSync(process.options.mcpConfigFile, 'utf8')) as {
    mcpServers: Record<string, { headers: Record<string, string> }>;
  };
  return Object.values(config.mcpServers)[0].headers['X-Api-Key'];
}

test("a process uses the person's token as its MCP key, and nothing else", async () => {
  const { manager, adapter } = await newManager();
  const conversation = await manager.create(undefined, undefined, undefined);
  manager.send(conversation.id, 'hello', token);
  assert.equal(mcpKeyOf(adapter.last), token);
  const config = readFileSync(adapter.last.options.mcpConfigFile, 'utf8');
  assert.ok(!config.includes('X-Mnemarr-Origin'), 'the audit knows the conversation by the token, not by a header');
});

test('a process that cannot start leaves the conversation as it was', async () => {
  const { manager } = await newManager(1);
  const busy = await manager.create(undefined, undefined, 'busy');
  const other = await manager.create(undefined, undefined, undefined);
  manager.send(busy.id, 'hello', token);

  assert.throws(
    () => manager.send(other.id, 'a new idea', token, 'large'),
    (error: ChatError) => error.status === 429,
  );

  const kept = manager.list().find((conversation) => conversation.id === other.id);
  assert.equal(kept?.model, 'small');
  assert.equal(kept?.title, '');
});

test('at the conversation cap nothing is deleted to make room: the new one is refused', async () => {
  const { manager } = await newManager(2, 2);
  const first = await manager.create(undefined, undefined, 'first');
  const second = await manager.create(undefined, undefined, 'second');

  await assert.rejects(manager.create(undefined, undefined, 'third'), (error: ChatError) => error.status === 429);

  assert.deepEqual(
    manager
      .list()
      .map((conversation) => conversation.id)
      .sort(),
    [first.id, second.id].sort(),
  );
});

test('a process that cannot start leaves no file with the token and no CLI session that never existed', async () => {
  const { manager, adapter, dataDir } = await newManager();
  const conversation = await manager.create(undefined, undefined, undefined);
  adapter.failNextStart = true;

  assert.throws(() => manager.send(conversation.id, 'hello', token), /CLI could not start/);

  await waitFor(() => readdirSync(dataDir).every((name) => !name.endsWith('.mcp.json')));
  manager.send(conversation.id, 'again', token);
  assert.equal(
    adapter.last.options.resume,
    undefined,
    'the CLI session is created by the process that starts, not resumed',
  );
});

test("the files with a person's token that a killed runner left behind are removed when it starts", async () => {
  const dataDir = mkdtempSync(join(tmpdir(), 'mnemarr-agent-test-'));
  const leftover = join(dataDir, 'one.mcp.json');
  writeFileSync(leftover, '{}');
  writeFileSync(join(dataDir, 'keep.txt'), 'x');

  await newManager(2, undefined, dataDir);

  assert.equal(existsSync(leftover), false);
  assert.equal(existsSync(join(dataDir, 'keep.txt')), true);
});

test('a process older than the life of its token is replaced at the next message', async () => {
  mock.timers.enable({ apis: ['Date'], now: 0 });
  try {
    const { manager, adapter } = await newManager();
    const conversation = await manager.create(undefined, undefined, undefined);
    manager.send(conversation.id, 'hello', token);
    adapter.last.options.onEvent({ type: 'turn-end', isError: false });

    mock.timers.tick(10 * 60 * 60 * 1000);
    manager.send(conversation.id, 'ten hours later', token);
    assert.equal(adapter.processes.length, 1, 'the same process still serves');
    adapter.last.options.onEvent({ type: 'turn-end', isError: false });

    mock.timers.tick(2 * 60 * 60 * 1000);
    manager.send(conversation.id, 'twelve hours later', `mnc_${'b'.repeat(43)}`);
    assert.equal(adapter.processes.length, 2, 'a new process carries the new token');
    assert.equal(mcpKeyOf(adapter.last), `mnc_${'b'.repeat(43)}`);
  } finally {
    mock.timers.reset();
  }
});

test('an idle process stops after the idle time; one that is busy or waits for a person is kept', async () => {
  mock.timers.enable({ apis: ['Date', 'setInterval'], now: 0 });
  try {
    const { manager, adapter } = await newManager(3);
    const idle = await manager.create(undefined, undefined, undefined);
    const busy = await manager.create(undefined, undefined, undefined);
    manager.send(idle.id, 'hello', token);
    adapter.last.options.onEvent({ type: 'turn-end', isError: false });
    manager.send(busy.id, 'hello', token);

    mock.timers.tick(120_000);

    assert.equal(adapter.processes[0].closed, true, 'idle');
    assert.equal(adapter.processes[1].closed, false, 'busy');
  } finally {
    mock.timers.reset();
  }
});
