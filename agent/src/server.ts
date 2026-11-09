// The runner's HTTP API (ADR 0035), for Mnemarr only: a bearer shared secret on every route but /healthz, JSON in and
// out, server-sent events for a conversation's stream. Mnemarr proxies it to the chat panel (ADR 0035).

import { createHash, timingSafeEqual } from 'node:crypto';
import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http';

import { ChatError, type SessionManager } from './sessions.ts';
import { idPattern } from './store.ts';

const maxBodyBytes = 512 * 1024;
export const maxMessageLength = 20_000;
/** A job (the consolidation) is given its instructions and an excerpt of a conversation in one message: Mnemarr cuts the excerpt, the runner bounds the message. */
export const maxJobMessageLength = 150_000;
const keepAliveMs = 15_000;
const requestIdPattern = /^[A-Za-z0-9_-]{1,100}$/;
const namePattern = /^[a-z0-9][a-z0-9._-]{0,63}$/;
/** The per-turn token Mnemarr mints for the user: used as the MCP key instead of the shared one. */
const mcpTokenPattern = /^mnc_[A-Za-z0-9_-]{20,100}$/;

function digest(value: string): Buffer {
  return createHash('sha256').update(value, 'utf8').digest();
}

/** Constant time, whatever the lengths: both sides are hashed first. */
export function secretMatches(header: string | undefined, secret: string): boolean {
  const presented = header?.startsWith('Bearer ') ? header.slice('Bearer '.length).trim() : '';
  return presented.length > 0 && timingSafeEqual(digest(presented), digest(secret));
}

function send(response: ServerResponse, status: number, body?: unknown): void {
  if (body === undefined) {
    response.writeHead(status).end();
    return;
  }
  const json = JSON.stringify(body);
  response
    .writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' })
    .end(json);
}

async function readJson(request: IncomingMessage): Promise<Record<string, unknown>> {
  let size = 0;
  const chunks: Buffer[] = [];
  for await (const chunk of request) {
    size += (chunk as Buffer).length;
    if (size > maxBodyBytes) {
      throw new ChatError(413, 'The request is too large.');
    }
    chunks.push(chunk as Buffer);
  }
  if (chunks.length === 0) {
    return {};
  }
  try {
    const value: unknown = JSON.parse(Buffer.concat(chunks).toString('utf8'));
    if (typeof value === 'object' && value !== null && !Array.isArray(value)) {
      return value as Record<string, unknown>;
    }
  } catch {
    // Falls through to the 400.
  }
  throw new ChatError(400, 'The body must be a JSON object.');
}

function optionalName(body: Record<string, unknown>, key: string): string | undefined {
  const value = body[key];
  if (value === undefined || value === null || value === '') {
    return undefined;
  }
  if (typeof value !== 'string' || !namePattern.test(value)) {
    throw new ChatError(400, `${key} must be a short lowercase name.`);
  }
  return value;
}

/** Every message carries the token Mnemarr minted for the person: the runner holds no key of its own. */
function requiredMcpToken(body: Record<string, unknown>): string {
  const value = body.mcpToken;
  if (typeof value !== 'string' || !mcpTokenPattern.test(value)) {
    throw new ChatError(400, 'mcpToken is required: it is the token Mnemarr minted for this message.');
  }
  return value;
}

function stream(
  manager: SessionManager,
  id: string,
  request: IncomingMessage,
  response: ServerResponse,
  after: number,
): void {
  response.writeHead(200, {
    'Content-Type': 'text/event-stream',
    'Cache-Control': 'no-cache',
    'X-Accel-Buffering': 'no',
    Connection: 'keep-alive',
  });
  response.write('retry: 2000\n\n');
  const unsubscribe = manager.subscribe(id, after, (event) => {
    response.write(`id: ${event.seq}\nevent: chat\ndata: ${JSON.stringify(event)}\n\n`);
  });
  const keepAlive = setInterval(() => response.write(': keep-alive\n\n'), keepAliveMs);
  request.on('close', () => {
    clearInterval(keepAlive);
    unsubscribe();
  });
}

type Route = (request: IncomingMessage, response: ServerResponse, parts: string[], url: URL) => Promise<void> | void;

/** What a handler needs to answer one request. */
interface Call {
  manager: SessionManager;
  request: IncomingMessage;
  response: ServerResponse;
  url: URL;
  method: string;
}

/** /v1/conversations: list the conversations or start one. */
async function collection({ manager, request, response, method }: Call): Promise<void> {
  if (method === 'GET') {
    send(response, 200, manager.list());
    return;
  }
  if (method !== 'POST') {
    throw new ChatError(405, 'Method not allowed.');
  }
  const body = await readJson(request);
  const title = typeof body.title === 'string' ? body.title.slice(0, 200) : undefined;
  if (body.policy !== undefined && body.policy !== 'chat' && body.policy !== 'job') {
    throw new ChatError(400, "policy is 'chat' or 'job'.");
  }
  send(
    response,
    201,
    await manager.create(optionalName(body, 'agent'), optionalName(body, 'model'), title, body.policy),
  );
}

/** /v1/conversations/{id}: read or delete one conversation. */
async function single({ manager, response, method }: Call, id: string): Promise<void> {
  if (method === 'GET') {
    send(response, 200, manager.get(id));
    return;
  }
  if (method !== 'DELETE') {
    throw new ChatError(405, 'Method not allowed.');
  }
  await manager.delete(id);
  send(response, 204);
}

function events({ manager, request, response, url }: Call, id: string): void {
  const header = request.headers['last-event-id'];
  const after = Number.parseInt(String(header ?? url.searchParams.get('after') ?? '0'), 10);
  stream(manager, id, request, response, Number.isFinite(after) && after > 0 ? after : 0);
}

async function message({ manager, request, response }: Call, id: string): Promise<void> {
  const body = await readJson(request);
  const text = typeof body.text === 'string' ? body.text : '';
  const limit = manager.get(id).policy === 'job' ? maxJobMessageLength : maxMessageLength;
  if (text.trim().length === 0 || text.length > limit) {
    throw new ChatError(400, `text must be 1 to ${limit} characters.`);
  }
  manager.send(id, text, requiredMcpToken(body), optionalName(body, 'model'));
  send(response, 202, { accepted: true });
}

async function permission({ manager, request, response }: Call, id: string, requestId: string): Promise<void> {
  const body = await readJson(request);
  if (typeof body.approve !== 'boolean') {
    throw new ChatError(400, 'approve must be true or false.');
  }
  manager.answer(id, requestId, body.approve);
  send(response, 200, { approved: body.approve });
}

/** /v1/conversations/{id}/{events|messages|cancel|permissions/{requestId}}. */
async function action(call: Call, parts: string[]): Promise<void> {
  const [, , id = '', name, requestId = ''] = parts;
  const { method, manager, response } = call;
  if (parts.length === 4 && name === 'events' && method === 'GET') {
    events(call, id);
    return;
  }
  if (parts.length === 4 && name === 'messages' && method === 'POST') {
    await message(call, id);
    return;
  }
  if (parts.length === 4 && name === 'cancel' && method === 'POST') {
    manager.cancel(id);
    send(response, 202, { accepted: true });
    return;
  }
  if (parts.length === 5 && name === 'permissions' && method === 'POST' && requestIdPattern.test(requestId)) {
    await permission(call, id, requestId);
    return;
  }
  throw new ChatError(404, 'Not found.');
}

function routes(manager: SessionManager): Route {
  return async (request, response, parts, url) => {
    const call: Call = { manager, request, response, url, method: request.method ?? 'GET' };

    if (parts[0] === 'v1' && parts[1] === 'agents' && parts.length === 2 && call.method === 'GET') {
      send(response, 200, manager.agents());
      return;
    }
    if (parts[0] !== 'v1' || parts[1] !== 'conversations') {
      throw new ChatError(404, 'Not found.');
    }
    if (parts.length === 2) {
      await collection(call);
      return;
    }
    const id = parts[2] ?? '';
    if (!idPattern.test(id)) {
      throw new ChatError(404, 'No such conversation.');
    }
    if (parts.length === 3) {
      await single(call, id);
      return;
    }
    await action(call, parts);
  };
}

export function createRunnerServer(
  manager: SessionManager,
  secret: string,
  log: (message: string) => void = () => undefined,
): Server {
  const route = routes(manager);
  return createServer((request, response) => {
    // The base only lets URL parse a relative path: nothing is fetched from it.
    // eslint-disable-next-line sonarjs/no-clear-text-protocols
    const url = new URL(request.url ?? '/', 'http://runner');
    if (url.pathname === '/healthz') {
      send(response, 200, { status: 'ok' });
      return;
    }
    if (!secretMatches(request.headers.authorization, secret)) {
      response.setHeader('WWW-Authenticate', 'Bearer realm="mnemarr-agent"');
      send(response, 401, { error: 'Missing or wrong secret.' });
      return;
    }
    const parts = url.pathname.split('/').filter(Boolean);
    Promise.resolve(route(request, response, parts, url)).catch((error: unknown) => {
      const status = error instanceof ChatError ? error.status : 500;
      if (status === 500) {
        log(`${request.method} ${url.pathname}: ${error instanceof Error ? error.message : String(error)}`);
      }
      if (!response.headersSent) {
        send(response, status, { error: error instanceof ChatError ? error.message : 'Internal error.' });
      }
    });
  });
}
