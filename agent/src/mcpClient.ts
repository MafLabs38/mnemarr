// A minimal MCP client over HTTP, for the adapters that are not a CLI (D-87): the agent loop of such an adapter is its own, and Mnemarr's MCP server is the only
// thing it talks to. One JSON-RPC message per POST, answered as JSON (the transport Mnemarr's server offers). The address and the headers (the key, the origin
// the audit records) come from the per-process configuration file that the session manager writes, never from the model.

import { readFileSync } from 'node:fs';

import { serverName } from './policy.ts';

export interface McpTool {
  name: string;
  description: string;
  inputSchema: Record<string, unknown>;
}

export interface McpToolResult {
  isError: boolean;
  text: string;
}

export interface McpEndpoint {
  url: string;
  headers: Record<string, string>;
}

const requestTimeoutMs = 30_000;

/** The endpoint written by the session manager for one process (`mcpServers.<name>`). */
export function readEndpoint(file: string): McpEndpoint {
  const config = JSON.parse(readFileSync(file, 'utf8')) as {
    mcpServers?: Record<string, { url?: string; headers?: Record<string, string> }>;
  };
  const server = config.mcpServers?.[serverName];
  if (!server?.url) {
    throw new Error('The MCP configuration of this process has no server.');
  }
  return { url: server.url, headers: server.headers ?? {} };
}

export class McpClient {
  private nextId = 1;
  private readonly endpoint: McpEndpoint;
  private readonly fetcher: typeof fetch;

  constructor(endpoint: McpEndpoint, fetcher: typeof fetch = fetch) {
    this.endpoint = endpoint;
    this.fetcher = fetcher;
  }

  async listTools(signal?: AbortSignal): Promise<McpTool[]> {
    const result = (await this.call('tools/list', {}, signal)) as { tools?: McpTool[] };
    return (result.tools ?? []).map((tool) => ({
      name: tool.name,
      description: tool.description ?? '',
      inputSchema: tool.inputSchema ?? { type: 'object', properties: {} },
    }));
  }

  async callTool(name: string, input: unknown, signal?: AbortSignal): Promise<McpToolResult> {
    const result = (await this.call('tools/call', { name, arguments: input ?? {} }, signal)) as {
      content?: { type: string; text?: string }[];
      isError?: boolean;
    };
    const text = (result.content ?? [])
      .map((part) => (part.type === 'text' && typeof part.text === 'string' ? part.text : ''))
      .filter(Boolean)
      .join('\n');
    return { isError: result.isError === true, text };
  }

  private async call(method: string, params: unknown, signal?: AbortSignal): Promise<unknown> {
    const timeout = AbortSignal.timeout(requestTimeoutMs);
    const response = await this.fetcher(this.endpoint.url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json', ...this.endpoint.headers },
      body: JSON.stringify({ jsonrpc: '2.0', id: this.nextId++, method, params }),
      signal: signal ? AbortSignal.any([signal, timeout]) : timeout,
    });
    const body = (await response.json().catch(() => undefined)) as
      { result?: unknown; error?: { message?: string } } | undefined;
    if (!response.ok || body?.error) {
      throw new Error(body?.error?.message ?? `Mnemarr's MCP server answered ${response.status}.`);
    }
    return body?.result ?? {};
  }
}
