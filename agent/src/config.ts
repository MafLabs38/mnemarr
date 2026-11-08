// The runner's settings, from its environment (D-87). Secrets (the shared secret, Mnemarr's MCP key, the
// Claude token) are read here and never logged.

export interface RunnerConfig {
  host: string;
  port: number;
  /** Shared with Mnemarr (MNEMARR__AGENT__SECRET): every request but /healthz carries it as a bearer token. */
  secret: string;
  dataDir: string;
  mnemarrMcpUrl: string;
  mnemarrMcpKey?: string;
  maxSessions: number;
  /** Conversations kept on disk; the oldest idle one makes room for a new one. */
  maxConversations: number;
  idleTimeoutMs: number;
  /** The models offered, in order, and the one used when none is chosen; undefined: the adapter's own. */
  models?: { id: string; label: string }[];
  defaultModel?: string;
  claudeCommand: string;
  /** A model behind an OpenAI-compatible API, as a second agent beside Claude Code (D-87); undefined when no address is given. */
  openai?: {
    baseUrl: string;
    apiKey?: string;
    name: string;
    models: { id: string; label: string }[];
    defaultModel: string;
  };
}

export const minimumSecretLength = 32;

/** `sonnet,opus:Opus 5` → ids with optional labels (`id` or `id:Label`), each id a short lowercase name. */
export function parseModels(
  value: string | undefined,
  variable = 'MNEMARR_AGENT_MODELS',
): { id: string; label: string }[] | undefined {
  if (!value?.trim()) {
    return undefined;
  }
  return value.split(',').map((entry) => {
    const [id, ...label] = entry.trim().split(':');
    if (!/^[a-z0-9][a-z0-9._-]{0,63}$/.test(id)) {
      throw new Error(
        `${variable}: '${entry.trim()}' is not a model name (a short lowercase id, optionally id:Label).`,
      );
    }
    return { id, label: label.join(':').trim() || id };
  });
}

function integer(value: string | undefined, fallback: number, min: number, max: number): number {
  const parsed = value ? Number.parseInt(value, 10) : fallback;
  if (!Number.isFinite(parsed) || parsed < min || parsed > max) {
    throw new Error(`Expected an integer between ${min} and ${max}, got '${value}'.`);
  }
  return parsed;
}

/** The OpenAI-compatible agent, when its address is given: what is wrong with it is added to `errors`, so that the start says everything at once. */
function loadOpenAi(env: NodeJS.ProcessEnv, errors: string[]): RunnerConfig['openai'] {
  const url = env.MNEMARR_AGENT_OPENAI_URL?.trim();
  if (!url) {
    return undefined;
  }
  if (!/^https?:\/\/[^\s/]+(\/\S*)?$/.test(url)) {
    errors.push(
      'MNEMARR_AGENT_OPENAI_URL must be an http(s) address, e.g. https://api.openai.com/v1 or http://ollama:11434/v1.',
    );
  }
  let models: { id: string; label: string }[] | undefined;
  try {
    models = parseModels(env.MNEMARR_AGENT_OPENAI_MODELS, 'MNEMARR_AGENT_OPENAI_MODELS');
  } catch (error) {
    errors.push((error as Error).message);
  }
  if (!models) {
    errors.push(
      'MNEMARR_AGENT_OPENAI_MODELS is required with MNEMARR_AGENT_OPENAI_URL: the model names the API accepts, e.g. gpt-4.1,gpt-4.1-mini:Mini.',
    );
    return undefined;
  }
  const defaultModel = env.MNEMARR_AGENT_OPENAI_DEFAULT_MODEL?.trim() || models[0]?.id;
  if (!defaultModel || !models.some((model) => model.id === defaultModel)) {
    errors.push(`MNEMARR_AGENT_OPENAI_DEFAULT_MODEL '${defaultModel}' is not one of MNEMARR_AGENT_OPENAI_MODELS.`);
    return undefined;
  }
  return {
    baseUrl: url,
    apiKey: env.MNEMARR_AGENT_OPENAI_KEY?.trim() || undefined,
    name: env.MNEMARR_AGENT_OPENAI_NAME?.trim() || 'OpenAI-compatible',
    models,
    defaultModel,
  };
}

export function loadConfig(env: NodeJS.ProcessEnv): RunnerConfig {
  const errors: string[] = [];
  const secret = env.MNEMARR_AGENT_SECRET ?? '';
  if (secret.trim().length < minimumSecretLength) {
    errors.push(`MNEMARR_AGENT_SECRET must be at least ${minimumSecretLength} characters (openssl rand -hex 32).`);
  }
  const mcpUrl = env.MNEMARR_MCP_URL ?? '';
  if (!/^https?:\/\/[^\s]+\/mcp$/.test(mcpUrl)) {
    errors.push("MNEMARR_MCP_URL must be Mnemarr's MCP endpoint, e.g. http://mnemarr:8383/mcp.");
  }
  // Optional: Mnemarr mints a token per message (D-98), which is what the runner normally uses. A shared key only
  // serves callers that send none, and it should be a per-user key, never an Admin-equivalent secret.
  const mcpKey = env.MNEMARR_MCP_APIKEY?.trim() ? env.MNEMARR_MCP_APIKEY : undefined;
  if (mcpKey !== undefined && mcpKey.trim().length < minimumSecretLength) {
    errors.push(`MNEMARR_MCP_APIKEY, when set, must be at least ${minimumSecretLength} characters.`);
  }
  let models: { id: string; label: string }[] | undefined;
  try {
    models = parseModels(env.MNEMARR_AGENT_MODELS);
  } catch (error) {
    errors.push((error as Error).message);
  }
  const defaultModel = env.MNEMARR_AGENT_DEFAULT_MODEL?.trim() || undefined;
  if (defaultModel && models && !models.some((model) => model.id === defaultModel)) {
    errors.push(`MNEMARR_AGENT_DEFAULT_MODEL '${defaultModel}' is not one of MNEMARR_AGENT_MODELS.`);
  }
  const openai = loadOpenAi(env, errors);
  if (errors.length > 0) {
    throw new Error(errors.join('\n'));
  }
  return {
    host: env.MNEMARR_AGENT_HOST ?? '0.0.0.0',
    port: integer(env.MNEMARR_AGENT_PORT, 8489, 1, 65535),
    secret,
    dataDir: env.MNEMARR_AGENT_DATA ?? '/data',
    mnemarrMcpUrl: mcpUrl,
    mnemarrMcpKey: mcpKey,
    maxSessions: integer(env.MNEMARR_AGENT_MAX_SESSIONS, 3, 1, 20),
    maxConversations: integer(env.MNEMARR_AGENT_MAX_CONVERSATIONS, 200, 1, 5000),
    models,
    defaultModel,
    idleTimeoutMs: integer(env.MNEMARR_AGENT_IDLE_MINUTES, 15, 1, 24 * 60) * 60_000,
    claudeCommand: env.CLAUDE_BIN ?? 'claude',
    openai,
  };
}
