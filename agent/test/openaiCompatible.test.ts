import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http';
import type { AddressInfo } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';

import type { AdapterEvent, PermissionDecision } from '../src/adapters/adapter.ts';
import { maxRounds, OpenAiCompatibleAdapter } from '../src/adapters/openaiCompatible.ts';
import { classify } from '../src/policy.ts';
import { waitFor } from './fakes.ts';

interface Listening {
  server: Server;
  url: string;
  requests: { path: string; headers: IncomingMessage['headers']; body: unknown }[];
}

async function listen(handler: (request: { path: string; body: any }, response: ServerResponse) => void): Promise<Listening> {
  const requests: Listening['requests'] = [];
  const server = createServer((request, response) => {
    let text = '';
    request.on('data', (chunk) => (text += chunk));
    request.on('end', () => {
      const body = text ? JSON.parse(text) : undefined;
      requests.push({ path: request.url ?? '', headers: request.headers, body });
      handler({ path: request.url ?? '', body }, response);
    });
  });
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  // The tests end when their assertions do: an open server must not keep the process alive.
  server.unref();
  return { server, url: `http://127.0.0.1:${(server.address() as AddressInfo).port}`, requests };
}

const tools = [
  { name: 'search', description: 'Searches the notes.', inputSchema: { type: 'object', properties: { query: { type: 'string' } } } },
  { name: 'update_note', description: 'Changes a note.', inputSchema: { type: 'object', properties: {} } },
];

/** A fake MCP server: tools/list, and tools/call answering what the test says. */
async function mcpServer(result: { content: { type: string; text: string }[]; isError?: boolean } = { content: [{ type: 'text', text: 'a note says so' }] }) {
  return listen(({ body }, response) => {
    response.setHeader('Content-Type', 'application/json');
    response.end(JSON.stringify({ jsonrpc: '2.0', id: body.id, result: body.method === 'tools/list' ? { tools } : result }));
  });
}

function sse(response: ServerResponse, chunks: unknown[]): void {
  response.setHeader('Content-Type', 'text/event-stream');
  for (const chunk of chunks) {
    response.write(`data: ${JSON.stringify(chunk)}\n\n`);
  }
  response.end('data: [DONE]\n\n');
}

const words = (content: string, usage = { prompt_tokens: 12, completion_tokens: 3 }) => [
  { choices: [{ delta: { content: content.slice(0, 5) } }] },
  { choices: [{ delta: { content: content.slice(5) } }] },
  { choices: [], usage },
];

const toolCall = (id: string, name: string, args: unknown) => [
  { choices: [{ delta: { tool_calls: [{ index: 0, id, function: { name, arguments: JSON.stringify(args).slice(0, 8) } }] } }] },
  { choices: [{ delta: { tool_calls: [{ index: 0, function: { arguments: JSON.stringify(args).slice(8) } }] } }] },
];

function start(openai: Listening, mcp: Listening, options: { resume?: string; state?: string } = {}) {
  const dir = mkdtempSync(join(tmpdir(), 'mnemarr-openai-test-'));
  const mcpConfigFile = join(dir, 'mcp.json');
  writeFileSync(
    mcpConfigFile,
    JSON.stringify({ mcpServers: { mnemarr: { type: 'http', url: `${mcp.url}/mcp`, headers: { 'X-Api-Key': 'mnm_secret', 'X-Mnemarr-Origin': 'chat:c1' } } } }),
  );
  const adapter = new OpenAiCompatibleAdapter({
    baseUrl: `${openai.url}/v1/`,
    apiKey: 'sk-test',
    name: 'Test API',
    models: [{ id: 'model-a', label: 'Model A' }],
    defaultModel: 'model-a',
    stateDir: options.state ?? join(dir, 'state'),
  });
  const events: AdapterEvent[] = [];
  let exited: number | null | undefined;
  const process = adapter.start({
    model: 'model-a',
    resume: options.resume,
    sessionId: 'aaaaaaaa-0000-4000-8000-000000000001',
    workDir: dir,
    mcpConfigFile,
    onEvent: (event) => {
      events.push(event);
      // The session manager's policy answers every call: reads run, anything else waits for a person (here: the test).
      if (event.type === 'permission-request' && classify(event.toolName) === 'read') {
        process.answer(event.requestId, { allow: true, input: event.input });
      }
    },
    onExit: (code) => (exited = code),
  });
  return { adapter, process, events, exited: () => exited, state: join(dir, 'state') };
}

const ended = (events: AdapterEvent[]) => events.some((event) => event.type === 'turn-end');

test('it is listed with its own name and models, and is unavailable without an address', () => {
  const settings = { name: 'Local', models: [{ id: 'm', label: 'M' }], defaultModel: 'm', stateDir: tmpdir() };

  assert.equal(new OpenAiCompatibleAdapter({ ...settings, baseUrl: 'http://x/v1' }).available(), true);
  assert.equal(new OpenAiCompatibleAdapter(settings).available(), false);
  assert.equal(new OpenAiCompatibleAdapter(settings).name, 'Local');
});

test('a plain answer streams as words, then as one block, with the usage', async () => {
  const openai = await listen((_, response) => sse(response, words('Hello there')));
  const mcp = await mcpServer();
  const run = start(openai, mcp);

  run.process.send('Hi');
  await waitFor(() => ended(run.events));

  assert.deepEqual(
    run.events.filter((event) => event.type === 'text-delta').map((event) => (event as { text: string }).text),
    ['Hello', ' there'],
  );
  assert.deepEqual(run.events.find((event) => event.type === 'text'), { type: 'text', text: 'Hello there' });
  const end = run.events.find((event) => event.type === 'turn-end') as { isError: boolean; usage: { inputTokens: number; outputTokens: number } };
  assert.equal(end.isError, false);
  assert.deepEqual([end.usage.inputTokens, end.usage.outputTokens], [12, 3]);
  const sent = openai.requests[0];
  assert.equal(sent.path, '/v1/chat/completions');
  assert.equal(sent.headers.authorization, 'Bearer sk-test');
  assert.equal((sent.body as { model: string }).model, 'model-a');
  assert.deepEqual(
    (sent.body as { tools: { function: { name: string } }[] }).tools.map((tool) => tool.function.name),
    ['search', 'update_note'],
  );
  assert.equal(run.events.find((event) => event.type === 'session') !== undefined, true);
});

test('a tool the model asks for is shown, run by the MCP server with the key, and its result given back to the model', async () => {
  let round = 0;
  const openai = await listen((_, response) => sse(response, round++ === 0 ? toolCall('call_1', 'search', { query: 'backup' }) : words('The note says so.')));
  const mcp = await mcpServer();
  const run = start(openai, mcp);

  run.process.send('What do my notes say about backup?');
  await waitFor(() => ended(run.events));

  const call = run.events.find((event) => event.type === 'tool-call') as { name: string; input: unknown };
  assert.equal(call.name, 'mcp__mnemarr__search');
  assert.deepEqual(call.input, { query: 'backup' });
  assert.deepEqual(run.events.find((event) => event.type === 'tool-result'), { type: 'tool-result', id: 'call_1', isError: false, summary: 'a note says so' });
  const called = mcp.requests.find((request) => (request.body as { method: string }).method === 'tools/call');
  assert.deepEqual((called?.body as { params: unknown }).params, { name: 'search', arguments: { query: 'backup' } });
  assert.equal(called?.headers['x-api-key'], 'mnm_secret');
  assert.equal(called?.headers['x-mnemarr-origin'], 'chat:c1');
  const second = openai.requests[1].body as { messages: { role: string; content: string }[] };
  assert.deepEqual(second.messages.at(-1), { role: 'tool', tool_call_id: 'call_1', content: 'a note says so' });
  assert.equal((run.events.at(-1) as { type: string }).type, 'turn-end');
});

test('a change waits for the person: approved, it runs with the arguments unchanged; rejected, the model is told and nothing runs', async () => {
  for (const approve of [true, false]) {
    let round = 0;
    const openai = await listen((_, response) => sse(response, round++ === 0 ? toolCall('call_w', 'update_note', { path: 'a.md' }) : words('Done.')));
    const mcp = await mcpServer();
    const run = start(openai, mcp);

    run.process.send('Change a.md');
    await waitFor(() => run.events.some((event) => event.type === 'permission-request'));
    assert.equal(mcp.requests.some((request) => (request.body as { method: string }).method === 'tools/call'), false, 'nothing runs while it waits');
    const decision: PermissionDecision = approve ? { allow: true, input: { path: 'a.md' } } : { allow: false, message: 'The operator rejected this change.' };
    run.process.answer('call_w', decision);
    await waitFor(() => ended(run.events));

    assert.equal(mcp.requests.some((request) => (request.body as { method: string }).method === 'tools/call'), approve);
    const result = run.events.find((event) => event.type === 'tool-result') as { isError: boolean; summary: string };
    assert.equal(result.isError, !approve);
    if (!approve) {
      assert.match(result.summary, /rejected/);
    }
  }
});

test('stopping a turn rejects what waits and ends the turn as an error', async () => {
  const openai = await listen((_, response) => sse(response, toolCall('call_s', 'update_note', {})));
  const mcp = await mcpServer();
  const run = start(openai, mcp);

  run.process.send('Change it');
  await waitFor(() => run.events.some((event) => event.type === 'permission-request'));
  run.process.interrupt();
  await waitFor(() => ended(run.events));

  assert.equal((run.events.find((event) => event.type === 'turn-end') as { isError: boolean }).isError, true);
  assert.equal(mcp.requests.some((request) => (request.body as { method: string }).method === 'tools/call'), false);
});

test('a model that never stops asking for tools is stopped after a bounded number of rounds', async () => {
  const openai = await listen((_, response) => sse(response, toolCall('call_x', 'search', {})));
  const mcp = await mcpServer();
  const run = start(openai, mcp);

  run.process.send('Loop');
  await waitFor(() => ended(run.events), 5000);

  assert.equal(openai.requests.length, maxRounds);
  assert.equal(run.events.some((event) => event.type === 'error'), true);
});

test('a refused key or an API that fails is said in words, without the key', async () => {
  const openai = await listen((_, response) => {
    response.statusCode = 401;
    response.end('{"error":"nope sk-test"}');
  });
  const mcp = await mcpServer();
  const run = start(openai, mcp);

  run.process.send('Hi');
  await waitFor(() => ended(run.events));

  const error = run.events.find((event) => event.type === 'error') as { message: string };
  assert.match(error.message, /401/);
  assert.doesNotMatch(error.message, /sk-test/);
});

test('arguments that are not JSON are told to the model instead of reaching the MCP server', async () => {
  let round = 0;
  const openai = await listen((_, response) =>
    sse(response, round++ === 0 ? [{ choices: [{ delta: { tool_calls: [{ index: 0, id: 'call_b', function: { name: 'search', arguments: '{not json' } }] } }] }] : words('Sorry.')),
  );
  const mcp = await mcpServer();
  const run = start(openai, mcp);

  run.process.send('Hi');
  await waitFor(() => ended(run.events));

  assert.equal(mcp.requests.some((request) => (request.body as { method: string }).method === 'tools/call'), false);
  assert.equal((run.events.find((event) => event.type === 'tool-result') as { isError: boolean }).isError, true);
});

test('a conversation resumed later takes its messages back', async () => {
  const openai = await listen((_, response) => sse(response, words('First answer')));
  const mcp = await mcpServer();
  const first = start(openai, mcp);
  first.process.send('First question');
  await waitFor(() => ended(first.events));

  const second = start(openai, mcp, { resume: 'aaaaaaaa-0000-4000-8000-000000000001', state: first.state });
  second.process.send('Second question');
  await waitFor(() => ended(second.events));

  const sent = (openai.requests.at(-1)?.body as { messages: { role: string; content: string }[] }).messages;
  assert.deepEqual(
    sent.filter((message) => message.role !== 'system').map((message) => message.content),
    ['First question', 'First answer', 'Second question'],
  );
});

test('closing the process reports its exit once', async () => {
  const openai = await listen((_, response) => sse(response, words('x')));
  const run = start(openai, await mcpServer());

  run.process.close();
  run.process.close();

  assert.equal(run.exited(), 0);
});
