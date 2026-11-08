import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

import type { AdapterEvent } from '../src/adapters/adapter.ts';
import {
  buildArgs,
  childEnvironment,
  ClaudeCodeProcess,
  parseLine,
  permissionResponse,
} from '../src/adapters/claudeCode.ts';
import { waitFor } from './fakes.ts';

function replay(name: string) {
  return readFileSync(new URL(`fixtures/${name}`, import.meta.url), 'utf8')
    .split('\n')
    .filter(Boolean)
    .flatMap((line) => parseLine(line));
}

test('a read turn becomes session, thinking, tool call and result, text deltas, text and turn end', () => {
  const events = replay('read-turn.jsonl');
  assert.deepEqual(
    events.map((event) => event.type),
    ['session', 'thinking', 'tool-call', 'tool-result', 'text-delta', 'text-delta', 'text', 'turn-end'],
  );
  assert.deepEqual(events[0], {
    type: 'session',
    agentSessionId: '0d5c2f1e-7a3b-4c8d-9e0f-1a2b3c4d5e6f',
    model: 'claude-sonnet-example',
    tools: ['mcp__mnemarr__list_sources', 'mcp__mnemarr__search', 'mcp__mnemarr__update_note'],
  });
  assert.deepEqual(events[2], {
    type: 'tool-call',
    id: 'toolu_example_1',
    name: 'mcp__mnemarr__search',
    input: {},
  });
  const result = events[3];
  assert.equal(result.type === 'tool-result' && result.isError, false);
  assert.match(result.type === 'tool-result' ? result.summary : '', /wireguard-tunnel/);
  assert.deepEqual(events.at(-1), {
    type: 'turn-end',
    isError: false,
    usage: {
      inputTokens: 7810,
      outputTokens: 375,
      cacheReadTokens: 120,
      cacheWriteTokens: 5712,
      costUsd: 0.0168,
      durationMs: 5348,
    },
  });
});

test('a write asks for permission; other control requests and garbage are not chat events', () => {
  const events = replay('write-permission.jsonl');
  assert.deepEqual(
    events.map((event) => event.type),
    ['tool-call', 'permission-request', 'tool-result', 'error', 'turn-end'],
  );
  assert.deepEqual(events[1], {
    type: 'permission-request',
    requestId: '11111111-2222-4333-8444-555555555555',
    toolName: 'mcp__mnemarr__update_note',
    input: {
      section: 'providers',
      id: 'object-storage',
      notes: 'Bucket example-backups created on 2026-01-01.',
      version: '0123456789abcdef',
    },
  });
  assert.equal(events[2].type === 'tool-result' && events[2].isError, true);
  assert.equal(events[4].type === 'turn-end' && events[4].isError, true);
});

test('permission answers follow the control protocol', () => {
  assert.deepEqual(JSON.parse(permissionResponse('r1', { allow: true, input: { a: 1 } })), {
    type: 'control_response',
    response: { subtype: 'success', request_id: 'r1', response: { behavior: 'allow', updatedInput: { a: 1 } } },
  });
  assert.deepEqual(JSON.parse(permissionResponse('r2', { allow: false, message: 'no' })).response.response, {
    behavior: 'deny',
    message: 'no',
  });
});

test('the command line has no built-in tool, no settings, only our MCP server and stdio permissions', () => {
  const args = buildArgs({ model: 'sonnet', sessionId: 's-1', mcpConfigFile: '/data/run/x.mcp.json' });
  const value = (flag: string) => args[args.indexOf(flag) + 1];
  assert.equal(value('--tools'), '');
  assert.equal(value('--setting-sources'), '');
  assert.equal(value('--permission-prompt-tool'), 'stdio');
  assert.equal(value('--mcp-config'), '/data/run/x.mcp.json');
  assert.equal(value('--session-id'), 's-1');
  assert.ok(args.includes('--strict-mcp-config'));
  assert.match(value('--disallowedTools'), /Bash.*WebFetch/);
  assert.ok(
    value('--allowedTools')
      .split(',')
      .every((tool) => /^mcp__mnemarr__((get|list|find|read)_|search$)/.test(tool)),
  );
  assert.ok(!args.some((arg) => arg.includes('dangerously') || arg === 'bypassPermissions'));

  const resumed = buildArgs({ model: 'opus', sessionId: 'unused', resume: 's-1', mcpConfigFile: 'f' });
  assert.equal(resumed[resumed.indexOf('--resume') + 1], 's-1');
  assert.ok(!resumed.includes('--session-id'));
});

test("the CLI's environment never carries the runner's secret or Mnemarr's key", () => {
  const env = childEnvironment(
    {
      PATH: '/usr/bin',
      MNEMARR_AGENT_SECRET: 's'.repeat(40),
      CLAUDE_CODE_OAUTH_TOKEN: 'example-token',
      HOME: '/home/operator',
    },
    '/data/claude',
    '/data/home',
  );
  assert.equal(env.MNEMARR_AGENT_SECRET, undefined);
  assert.equal(env.CLAUDE_CODE_OAUTH_TOKEN, 'example-token');
  assert.equal(env.HOME, '/data/home');
  assert.equal(env.CLAUDE_CONFIG_DIR, '/data/claude');
});

const fakeCli = fileURLToPath(new URL('fixtures/fake-claude.mjs', import.meta.url));

function startFake(env: NodeJS.ProcessEnv) {
  const dir = mkdtempSync(join(tmpdir(), 'mnemarr-fake-claude-'));
  const log = join(dir, 'stdin.log');
  writeFileSync(log, '');
  const events: AdapterEvent[] = [];
  const exits: (number | null)[] = [];
  const process = new ClaudeCodeProcess(
    fakeCli,
    [],
    { ...env, PATH: globalThis.process.env.PATH, FAKE_CLAUDE_LOG: log },
    {
      model: 'sonnet',
      sessionId: 'aaaaaaaa-0000-4000-8000-000000000001',
      workDir: dir,
      mcpConfigFile: join(dir, 'mcp.json'),
      onEvent: (event) => events.push(event),
      onExit: (code) => exits.push(code),
    },
  );
  return { process, events, exits, stdin: () => readFileSync(log, 'utf8') };
}

test('a request that the runner does not serve gets an error answer, so the CLI never waits (a line may arrive in pieces)', async () => {
  const run = startFake({});

  await waitFor(() => run.stdin().includes('hook-1'));

  const answer = JSON.parse(run.stdin().trim().split('\n')[0]) as {
    type: string;
    response: { subtype: string; request_id: string };
  };
  assert.equal(answer.type, 'control_response');
  assert.deepEqual([answer.response.subtype, answer.response.request_id], ['error', 'hook-1']);
  run.process.close();
});

test('a message, an interruption and the end of input reach the CLI as it expects them, and its exit is reported', async () => {
  const run = startFake({});
  await waitFor(() => run.stdin().includes('hook-1'));

  run.process.send('Where are the backups?');
  run.process.interrupt();
  await waitFor(() => run.stdin().includes('"interrupt"'));
  run.process.close();
  await waitFor(() => run.exits.length > 0);

  const lines = run
    .stdin()
    .trim()
    .split('\n')
    .map((line) => JSON.parse(line) as { type: string; message?: { content: unknown } });
  assert.ok(
    lines.some((line) => line.type === 'user' && JSON.stringify(line.message).includes('Where are the backups?')),
  );
  assert.deepEqual(run.exits, [0]);
  assert.equal(
    run.events.some((event) => event.type === 'error'),
    false,
  );
});

test('a CLI that stops at once tells why, with the first line of what it said and nothing more', async () => {
  const run = startFake({ FAKE_CLAUDE_FAIL: '1' });

  await waitFor(() => run.exits.length > 0);

  assert.deepEqual(run.exits, [2]);
  const error = run.events.find((event) => event.type === 'error') as { message: string };
  assert.match(error.message, /stopped \(2\): boom: the login is not valid/);
  assert.doesNotMatch(error.message, /second line/);
});
