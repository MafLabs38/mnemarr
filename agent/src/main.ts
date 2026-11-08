// mnemarr-agent: runs an agent CLI headless behind Mnemarr's chat panel (D-87). Its only tool is Mnemarr's MCP
// server; it has no shell and no access to the machines (D-88).

import { mkdir, readdir } from 'node:fs/promises';
import { join } from 'node:path';

import type { AgentAdapter } from './adapters/adapter.ts';
import { ClaudeCodeAdapter } from './adapters/claudeCode.ts';
import { OpenAiCompatibleAdapter } from './adapters/openaiCompatible.ts';
import { loadConfig } from './config.ts';
import { createRunnerServer } from './server.ts';
import { SessionManager } from './sessions.ts';
import { ConversationStore } from './store.ts';

function log(message: string): void {
  process.stdout.write(`${new Date().toISOString()} ${message}\n`);
}

async function main(): Promise<void> {
  const config = loadConfig(process.env);
  const dirs = {
    workDir: join(config.dataDir, 'workspace'),
    runDir: join(config.dataDir, 'run'),
    claudeConfig: join(config.dataDir, 'claude'),
    home: join(config.dataDir, 'home'),
  };
  for (const dir of Object.values(dirs)) {
    await mkdir(dir, { recursive: true, mode: 0o700 });
  }
  // The CLI runs here: no project instructions, settings or MCP servers may sit in it.
  const planted = (await readdir(dirs.workDir)).filter((name) =>
    /^(CLAUDE(\.local)?\.md|AGENTS\.md|\.claude|\.mcp\.json)$/i.test(name),
  );
  if (planted.length > 0) {
    throw new Error(`${dirs.workDir} must not hold ${planted.join(', ')}: the agent CLI runs there and would read it.`);
  }

  const store = new ConversationStore(config.dataDir);
  await store.init();
  const adapters: AgentAdapter[] = [
    new ClaudeCodeAdapter(
      config.claudeCommand,
      dirs.claudeConfig,
      dirs.home,
      process.env,
      config.models,
      config.defaultModel,
    ),
  ];
  // A model behind an OpenAI-compatible API, when its address is given: the same panel, the same policy, another model.
  if (config.openai) {
    adapters.push(
      new OpenAiCompatibleAdapter({ ...config.openai, stateDir: join(config.dataDir, 'openai') }),
    );
  }
  const manager = new SessionManager(adapters, store, {
    maxSessions: config.maxSessions,
    maxConversations: config.maxConversations,
    idleTimeoutMs: config.idleTimeoutMs,
    workDir: dirs.workDir,
    runDir: dirs.runDir,
    mnemarrMcpUrl: config.mnemarrMcpUrl,
    mnemarrMcpKey: config.mnemarrMcpKey,
  });
  await manager.init();

  const server = createRunnerServer(manager, config.secret, log);
  server.listen(config.port, config.host, () => {
    const agents = adapters
      .map((adapter) => `${adapter.name}${adapter.available() ? '' : ' (no credentials)'}`)
      .join(', ');
    log(
      `mnemarr-agent listening on ${config.host}:${config.port}; agents: ${agents}; Mnemarr MCP: ${new URL(config.mnemarrMcpUrl).host}`,
    );
  });

  const stop = () => {
    manager.shutdown();
    server.close();
    setTimeout(() => process.exit(0), 1000).unref();
  };
  process.on('SIGTERM', stop);
  process.on('SIGINT', stop);
}

try {
  await main();
} catch (error: unknown) {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exit(1);
}
