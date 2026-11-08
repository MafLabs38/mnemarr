import assert from 'node:assert/strict';
import { test } from 'node:test';

import { McpClient } from '../src/mcpClient.ts';

function client(respond: (body: { method: string }) => { status?: number; body: unknown } | Error) {
  const seen: { headers: Record<string, string>; body: { method: string } }[] = [];
  const fetcher = ((_url: string, init: RequestInit) => {
    const body = JSON.parse(String(init.body)) as { method: string };
    seen.push({ headers: init.headers as Record<string, string>, body });
    const answer = respond(body);
    if (answer instanceof Error) {
      return Promise.reject(answer);
    }
    return Promise.resolve(new Response(JSON.stringify(answer.body), { status: answer.status ?? 200 }));
  }) as unknown as typeof fetch;
  return {
    mcp: new McpClient({ url: 'http://mnemarr.example/mcp', headers: { 'X-Api-Key': 'mnc_x' } }, fetcher),
    seen,
  };
}

test('the tools are listed with the key of the person', async () => {
  const { mcp, seen } = client(() => ({
    body: { result: { tools: [{ name: 'search', description: 'Searches', inputSchema: { type: 'object' } }] } },
  }));

  const tools = await mcp.listTools();

  assert.deepEqual(
    tools.map((tool) => tool.name),
    ['search'],
  );
  assert.equal(seen[0].headers['X-Api-Key'], 'mnc_x');
});

test('a tool that failed is a result the model reads, not an exception', async () => {
  const { mcp } = client(() => ({
    body: { result: { isError: true, content: [{ type: 'text', text: 'No such source.' }] } },
  }));

  assert.deepEqual(await mcp.callTool('index_source', { source: 'x' }), { isError: true, text: 'No such source.' });
});

test('a refusal of the server and a network failure are exceptions', async () => {
  await assert.rejects(client(() => ({ status: 401, body: {} })).mcp.callTool('search', {}));
  await assert.rejects(
    client(() => ({ body: { error: { message: 'Method not found' } } })).mcp.listTools(),
    /Method not found/,
  );
  await assert.rejects(client(() => new TypeError('fetch failed')).mcp.callTool('search', {}), /fetch failed/);
});
