import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import type { AgentAdapter, AgentProcess, PermissionDecision, StartOptions } from '../src/adapters/adapter.ts';
import { SessionManager } from '../src/sessions.ts';
import { ConversationStore } from '../src/store.ts';

export class FakeProcess implements AgentProcess {
  readonly sent: string[] = [];
  readonly answers: { requestId: string; decision: PermissionDecision }[] = [];
  interrupted = 0;
  closed = false;
  killed = false;
  readonly options: StartOptions;

  constructor(options: StartOptions) {
    this.options = options;
  }

  send(text: string): void {
    this.sent.push(text);
  }

  answer(requestId: string, decision: PermissionDecision): void {
    this.answers.push({ requestId, decision });
  }

  interrupt(): void {
    this.interrupted += 1;
  }

  close(): void {
    this.closed = true;
    this.options.onExit(0);
  }

  kill(): void {
    this.killed = true;
  }
}

export class FakeAdapter implements AgentAdapter {
  readonly id = 'fake';
  readonly name = 'Fake CLI';
  readonly models = [
    { id: 'small', label: 'Small' },
    { id: 'large', label: 'Large' },
  ];
  readonly defaultModel = 'small';
  readonly processes: FakeProcess[] = [];

  available(): boolean {
    return true;
  }

  start(options: StartOptions): AgentProcess {
    const process = new FakeProcess(options);
    this.processes.push(process);
    return process;
  }

  get last(): FakeProcess {
    const process = this.processes.at(-1);
    if (!process) {
      throw new Error('No process started.');
    }
    return process;
  }
}

/** Polls until the condition holds (bounded), instead of sleeping for a guessed time. */
export async function waitFor(condition: () => boolean, timeoutMs = 2000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (!condition()) {
    if (Date.now() > deadline) {
      throw new Error('Timed out waiting for the condition.');
    }
    await new Promise((resolve) => setImmediate(resolve));
  }
}

export async function newManager(
  maxSessions = 2,
  mnemarrMcpKey: string | null = 'k'.repeat(40),
  maxConversations?: number,
) {
  const dataDir = mkdtempSync(join(tmpdir(), 'mnemarr-agent-test-'));
  const store = new ConversationStore(dataDir);
  await store.init();
  const adapter = new FakeAdapter();
  const manager = new SessionManager([adapter], store, {
    maxSessions,
    idleTimeoutMs: 60_000,
    workDir: dataDir,
    runDir: dataDir,
    mnemarrMcpUrl: 'http://mnemarr.example:8383/mcp',
    mnemarrMcpKey: mnemarrMcpKey ?? undefined,
    maxConversations,
  });
  await manager.init();
  return { manager, adapter, store, dataDir };
}
