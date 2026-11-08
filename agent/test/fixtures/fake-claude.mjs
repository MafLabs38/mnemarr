#!/usr/bin/env node
// A stand-in for the agent CLI, for the tests of its process: it asks for a hook (split in two chunks, as a pipe may deliver a line),
// writes down what it receives on stdin, and stops when stdin ends. With FAKE_CLAUDE_FAIL it complains on stderr and stops at once.
import { appendFileSync } from 'node:fs';
import process from 'node:process';
import { setTimeout } from 'node:timers';

if (process.env.FAKE_CLAUDE_FAIL) {
  process.stderr.write('boom: the login is not valid\nsecond line that is not shown\n');
  process.exit(2);
}

const log = process.env.FAKE_CLAUDE_LOG;
const request = JSON.stringify({
  type: 'control_request',
  request_id: 'hook-1',
  request: { subtype: 'hook_callback' },
});
process.stdout.write(request.slice(0, 25));
setTimeout(() => process.stdout.write(`${request.slice(25)}\n`), 30);

process.stdin.setEncoding('utf8');
process.stdin.on('data', (chunk) => appendFileSync(log, chunk));
process.stdin.on('end', () => process.exit(0));
