import assert from 'node:assert/strict';
import type { AddressInfo } from 'node:net';
import { after, before, test } from 'node:test';

import { createRunnerServer, secretMatches } from '../src/server.ts';
import { newManager } from './fakes.ts';

const secret = 'example-secret-'.repeat(3);
let base = '';
let close: () => void = () => undefined;

before(async () => {
  const { manager } = await newManager();
  const server = createRunnerServer(manager, secret);
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
  close = () => server.close();
});

after(() => close());

const auth = { Authorization: `Bearer ${secret}`, 'Content-Type': 'application/json' };

test('secretMatches compares the bearer token only', () => {
  assert.equal(secretMatches(`Bearer ${secret}`, secret), true);
  assert.equal(secretMatches(`Bearer ${secret}x`, secret), false);
  assert.equal(secretMatches(secret, secret), false);
  assert.equal(secretMatches(undefined, secret), false);
});

test('every route but /healthz needs the secret', async () => {
  assert.equal((await fetch(`${base}/healthz`)).status, 200);
  const missing = await fetch(`${base}/v1/conversations`);
  assert.equal(missing.status, 401);
  assert.match(missing.headers.get('www-authenticate') ?? '', /Bearer/);
  assert.equal((await fetch(`${base}/v1/conversations`, { headers: { Authorization: 'Bearer wrong' } })).status, 401);
  assert.equal((await fetch(`${base}/v1/conversations`, { headers: auth })).status, 200);
});

test('conversations: create, send, validate, delete', async () => {
  const created = await fetch(`${base}/v1/conversations`, {
    method: 'POST',
    headers: auth,
    body: JSON.stringify({ model: 'large' }),
  });
  assert.equal(created.status, 201);
  const { id, model } = (await created.json()) as { id: string; model: string };
  assert.equal(model, 'large');

  const empty = await fetch(`${base}/v1/conversations/${id}/messages`, {
    method: 'POST',
    headers: auth,
    body: JSON.stringify({ text: '  ' }),
  });
  assert.equal(empty.status, 400);
  const sent = await fetch(`${base}/v1/conversations/${id}/messages`, {
    method: 'POST',
    headers: auth,
    body: JSON.stringify({ text: 'hello' }),
  });
  assert.equal(sent.status, 202);
  const permission = await fetch(`${base}/v1/conversations/${id}/permissions/unknown`, {
    method: 'POST',
    headers: auth,
    body: JSON.stringify({ approve: true }),
  });
  assert.equal(permission.status, 404);

  assert.equal((await fetch(`${base}/v1/conversations/not-an-id`, { headers: auth })).status, 404);
  assert.equal((await fetch(`${base}/v1/conversations/${id}`, { method: 'DELETE', headers: auth })).status, 204);
  assert.equal((await fetch(`${base}/v1/conversations/${id}`, { headers: auth })).status, 404);
});

test('a message may carry the per-user MCP token; a malformed one is refused', async () => {
  const created = await fetch(`${base}/v1/conversations`, { method: 'POST', headers: auth, body: '{}' });
  const { id } = (await created.json()) as { id: string };
  const send = (mcpToken: unknown) =>
    fetch(`${base}/v1/conversations/${id}/messages`, {
      method: 'POST',
      headers: auth,
      body: JSON.stringify({ text: 'hello', mcpToken }),
    });

  assert.equal((await send('not-a-token')).status, 400);
  assert.equal((await send(`mnc_${'b'.repeat(43)}`)).status, 202);
  await fetch(`${base}/v1/conversations/${id}`, { method: 'DELETE', headers: auth });
});

test('GET /v1/agents lists the agents, their models and whether they can run', async () => {
  const response = await fetch(`${base}/v1/agents`, { headers: auth });

  assert.equal(response.status, 200);
  const agents = (await response.json()) as {
    id: string;
    name: string;
    available: boolean;
    defaultModel: string;
    models: { id: string; label: string }[];
  }[];
  assert.deepEqual(agents, [
    {
      id: 'fake',
      name: 'Fake CLI',
      available: true,
      defaultModel: 'small',
      models: [
        { id: 'small', label: 'Small' },
        { id: 'large', label: 'Large' },
      ],
    },
  ]);
});

test('a body that is not a JSON object is a 400', async () => {
  for (const body of ['{not json', '[1,2]', '"text"']) {
    const response = await fetch(`${base}/v1/conversations`, { method: 'POST', headers: auth, body });
    assert.equal(response.status, 400, body);
  }
});

test('a body beyond the limit is a 413', async () => {
  const response = await fetch(`${base}/v1/conversations`, {
    method: 'POST',
    headers: auth,
    body: JSON.stringify({ title: 'x'.repeat(200_000) }),
  });

  assert.equal(response.status, 413);
});
