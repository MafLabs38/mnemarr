// Tests of the title agent: its guards are its security (the secret, the size of a body, how many models run at once). Run: node --test tools/title-agent
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { chmodSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { after, before, describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';

const main = join(dirname(fileURLToPath(import.meta.url)), 'main.mjs');
const secret = 'a-shared-secret-of-16+';
let folder;
let port;
let agent;

const freePort = () => new Promise((resolve) => {
  const probe = createServer().listen(0, '127.0.0.1', () => {
    const { port: found } = probe.address();
    probe.close(() => resolve(found));
  });
});

// A stand-in for the CLI: it answers with a JSON result after a pause, or fails when the transcript says so.
const fakeClaude = (folder) => {
  const path = join(folder, 'claude');
  writeFileSync(path, `#!/bin/sh
input=$(cat)
sleep "\${FAKE_PAUSE:-0}"
case "$input" in
  *FAIL*) echo '{"is_error":true,"result":"no"}';;
  *) echo '{"is_error":false,"result":"{\\"title\\":\\"T\\",\\"summary\\":\\"S\\"}"}';;
esac
`);
  chmodSync(path, 0o755);
  return path;
};

const start = async (extra = {}) => {
  port = await freePort();
  const child = spawn('node', [main], {
    env: { PATH: process.env.PATH, HOME: folder, TITLE_AGENT_HOST: '127.0.0.1', TITLE_AGENT_PORT: String(port), TITLE_AGENT_SECRET_FILE: join(folder, 'secret'), TITLE_AGENT_DATA: join(folder, 'data'), TITLE_AGENT_CLAUDE: join(folder, 'claude'), ...extra },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  await new Promise((resolve, reject) => {
    child.stdout.on('data', (chunk) => String(chunk).includes('title agent on') && resolve());
    child.on('exit', (code) => reject(new Error(`exited with ${code}`)));
  });
  return child;
};

const ask = (body, token = secret) =>
  fetch(`http://127.0.0.1:${port}/title`, { method: 'POST', headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' }, body: typeof body === 'string' ? body : JSON.stringify(body) });
const good = { model: 'haiku', system: 'Write a title.', transcript: 'hello' };

describe('title agent', () => {
  before(async () => {
    folder = mkdtempSync(join(tmpdir(), 'title-agent-'));
    writeFileSync(join(folder, 'secret'), `${secret}\n`);
    fakeClaude(folder);
    agent = await start({ FAKE_PAUSE: '1' });
  });
  after(() => {
    agent?.kill();
    rmSync(folder, { recursive: true, force: true });
  });

  it('answers the title of the model to a caller that has the secret', async () => {
    const response = await ask(good);
    assert.equal(response.status, 200);
    assert.deepEqual(JSON.parse((await response.json()).text), { title: 'T', summary: 'S' });
  });

  it('refuses a call without the secret, with a wrong one or of another length, whatever the body', async () => {
    for (const token of ['', 'wrong', `${secret}x`, secret.slice(0, -1)]) {
      assert.equal((await ask(good, token)).status, 401, token);
    }
    assert.equal((await fetch(`http://127.0.0.1:${port}/title`, { method: 'POST', body: JSON.stringify(good) })).status, 401);
  });

  it('refuses a body that is not JSON, lacks a field, names a model with odd characters, or is too large', async () => {
    assert.equal((await ask('not json')).status, 400);
    assert.equal((await ask({ model: 'haiku', system: 's' })).status, 400);
    assert.equal((await ask({ ...good, model: 'haiku; rm -rf /' })).status, 400);
    assert.equal((await ask({ ...good, transcript: 'x'.repeat(300 * 1024) })).status, 413);
  });

  it('gives a 502 without saying why when the model gives no answer', async () => {
    const response = await ask({ ...good, transcript: 'FAIL please' });
    assert.equal(response.status, 502);
    assert.deepEqual(await response.json(), { error: 'the model could not be asked' });
  });

  it('runs at most two models at once and says it is busy to the third', async () => {
    const statuses = (await Promise.all([ask(good), ask(good), ask(good)])).map((response) => response.status).sort();
    assert.deepEqual(statuses, [200, 200, 429]);
  });

  it('answers the health check without the secret and nothing else of the paths', async () => {
    assert.equal((await fetch(`http://127.0.0.1:${port}/healthz`)).status, 200);
    assert.equal((await fetch(`http://127.0.0.1:${port}/other`)).status, 404);
  });
});

describe('title agent start', () => {
  it('does not start with a secret of fewer than 16 characters', async () => {
    const small = mkdtempSync(join(tmpdir(), 'title-agent-'));
    writeFileSync(join(small, 'secret'), 'short\n');
    const child = spawn('node', [main], { env: { PATH: process.env.PATH, TITLE_AGENT_SECRET_FILE: join(small, 'secret'), TITLE_AGENT_DATA: join(small, 'data'), TITLE_AGENT_PORT: String(await freePort()) }, stdio: 'ignore' });
    const code = await new Promise((resolve) => child.on('exit', resolve));
    rmSync(small, { recursive: true, force: true });
    assert.equal(code, 1);
  });
});
