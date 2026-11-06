// Mnemarr's title agent (ADR 0028): a companion that lets Mnemarr write the title and the summary of a conversation with an agent CLI
// (Claude Code, headless) signed in with the account's own login, the way Portarr's assistant does. No dependency: Node 22 only.
//
//   TITLE_AGENT_SECRET_FILE=/run/secret TITLE_AGENT_HOST=127.0.0.1 TITLE_AGENT_PORT=8490 CLAUDE_CODE_OAUTH_TOKEN=... node main.mjs
//
// POST /title  Authorization: Bearer <secret>  {"model": "haiku", "system": "...", "transcript": "..."}  ->  {"text": "..."}
// The CLI has no tool at all (no shell, no files, no network of its own), runs in an empty directory, and keeps no session.
import { spawn } from 'node:child_process';
import { timingSafeEqual } from 'node:crypto';
import { mkdirSync, readFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { join } from 'node:path';

const host = process.env.TITLE_AGENT_HOST ?? '127.0.0.1';
const port = Number(process.env.TITLE_AGENT_PORT ?? 8490);
const dataDir = process.env.TITLE_AGENT_DATA ?? join(process.cwd(), '.title-agent');
const claude = process.env.TITLE_AGENT_CLAUDE ?? 'claude';
const maxBody = 256 * 1024;
const timeoutMs = 90_000;
const maxRunning = 2;

const secretFile = process.env.TITLE_AGENT_SECRET_FILE;
if (!secretFile) {
  console.error('TITLE_AGENT_SECRET_FILE is needed: a file that holds the secret shared with Mnemarr.');
  process.exit(1);
}
const secret = Buffer.from(readFileSync(secretFile, 'utf8').trim());
if (secret.length < 16) {
  console.error('The secret must be at least 16 characters.');
  process.exit(1);
}

const workDir = join(dataDir, 'work');
const configDir = join(dataDir, 'claude');
mkdirSync(workDir, { recursive: true, mode: 0o700 });
mkdirSync(configDir, { recursive: true, mode: 0o700 });

const authorised = (header) => {
  const given = Buffer.from((header ?? '').replace(/^Bearer /, ''));
  return given.length === secret.length && timingSafeEqual(given, secret);
};

let running = 0;

const ask = (model, system, transcript) =>
  new Promise((resolve, reject) => {
    // The environment is rebuilt: the CLI refuses to start inside another session, and must see nothing else of ours.
    const env = { PATH: process.env.PATH, HOME: process.env.HOME, CLAUDE_CONFIG_DIR: configDir, CLAUDE_CODE_OAUTH_TOKEN: process.env.CLAUDE_CODE_OAUTH_TOKEN ?? '' };
    const child = spawn(claude, ['-p', '--model', model, '--tools', '', '--no-session-persistence', '--output-format', 'json', '--system-prompt', system], { cwd: workDir, env });
    let out = '';
    const timer = setTimeout(() => child.kill('SIGKILL'), timeoutMs);
    child.stdout.on('data', (chunk) => { out += chunk; });
    child.on('error', (error) => { clearTimeout(timer); reject(error); });
    child.on('close', () => {
      clearTimeout(timer);
      try {
        const result = JSON.parse(out);
        if (result.is_error || typeof result.result !== 'string') { reject(new Error('the model gave no answer')); } else { resolve(result.result); }
      } catch { reject(new Error('the CLI gave no JSON')); }
    });
    child.stdin.end(transcript);
  });

const send = (response, status, body) => {
  response.writeHead(status, { 'content-type': 'application/json' });
  response.end(JSON.stringify(body));
};

createServer(async (request, response) => {
  if (request.method === 'GET' && request.url === '/healthz') { return send(response, 200, { ok: true }); }
  if (request.method !== 'POST' || request.url !== '/title') { return send(response, 404, { error: 'not found' }); }
  if (!authorised(request.headers.authorization)) { return send(response, 401, { error: 'unauthorised' }); }
  if (running >= maxRunning) { return send(response, 429, { error: 'busy' }); }

  let raw = '';
  for await (const chunk of request) {
    raw += chunk;
    if (raw.length > maxBody) { return send(response, 413, { error: 'too large' }); }
  }
  let input;
  try { input = JSON.parse(raw); } catch { return send(response, 400, { error: 'not JSON' }); }
  const { model, system, transcript } = input ?? {};
  if (typeof model !== 'string' || !/^[A-Za-z0-9._-]{1,64}$/.test(model) || typeof system !== 'string' || typeof transcript !== 'string') {
    return send(response, 400, { error: 'model, system and transcript are needed' });
  }

  running++;
  try {
    send(response, 200, { text: await ask(model, system, transcript) });
  } catch (error) {
    console.error(`title failed: ${error.message}`);
    send(response, 502, { error: 'the model could not be asked' });
  } finally {
    running--;
  }
}).listen(port, host, () => console.log(`title agent on ${host}:${port}`));
