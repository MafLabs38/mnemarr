# Conversation formats

Where each assistant keeps its conversations, and how the collector reads them ([ADR 0025](adr/0025-the-collector-is-a-service-per-machine-enrolled-by-a-code-and-configured-from-the-server.md)). **None of these formats is a public contract**: they change between versions, and most of what follows comes from third-party tools and articles, not from the vendors. Each reader is checked against real files before it is called done, and skips what it does not understand.

This table was written on 5 November 2026 from the pages linked at the end.

## The two families

| Family | Assistants | What the collector sends |
|---|---|---|
| **Append-only JSON lines**: a file that only grows, one JSON object per line | Claude Code, Codex CLI, Copilot CLI | The lines as they are, by position (ADR 0024). |
| **A JSON document that is rewritten** as the conversation goes on | Gemini CLI, Copilot in VS Code | One line per message, in a stable order, flattened by the reader; only the messages past the position. A message rewritten after it was sent is not sent again. |

## By assistant

| Reader | Where (default) | Shape | Status |
|---|---|---|---|
| `claude-code` | `~/.claude/projects/<project>/<session>.jsonl` (`%USERPROFILE%\.claude\projects\…` on Windows). Sub-agents are in a folder of their own, left out. | One JSON object per line: `type` (`user`, `assistant`, …), `message.content` (text, or blocks: text, tool use, tool result, thinking), `timestamp`, `cwd`, `sessionId`. | **Built** and used on real files. |
| `codex` | `~/.codex/sessions/YYYY/MM/DD/rollout-<timestamp>-<id>.jsonl`; `CODEX_HOME` moves the root. | One event per line, an envelope with a timestamp, a type and a payload; the first line is a `session_meta` record (working directory, session id, CLI version); messages and tool calls are `response_item` entries. One source says newer versions may compress the files with zstd, and that a SQLite index sits beside them. | Planned. **Untested**: no account at hand. |
| `copilot-cli` | `~/.copilot/session-state/<session-id>/events.jsonl`; `COPILOT_HOME` moves the root. A `workspace.yaml` beside it holds the repository and branch. An older layout, `~/.copilot/history-session-state/<id>.json`, is a single JSON document. | One event per line (`session.start` with the session, time, directory and model; messages; `session.shutdown` with usage). The schema is undocumented and was reverse-engineered by others. | Planned. To be tried by the maintainer. |
| `copilot-vscode` | `…/Code/User/workspaceStorage/<hash>/chatSessions/<uuid>.json` under `%APPDATA%` (Windows), `~/.config` (Linux), `~/Library/Application Support` (macOS); `Code - Insiders` for the Insiders build; under `~/.vscode-server/…` for WSL, SSH and dev containers. The hash is derived from the workspace folder, and a workspace that was moved leaves its history in an orphaned hash (the `workspace.json` of each folder says which workspace it is). One report mentions a newer `GitHub.copilot-chat/transcripts/*.jsonl` event stream. | A JSON document per session, rewritten as the conversation goes on. | Planned. To be tried by the maintainer. |
| `gemini-cli` | `~/.gemini/tmp/<project_hash>/chats/session-*.json` (the official documentation says a hash of the project root; some tools say a slug: look at the real folder). Manual checkpoints (`/chat save`) are in `~/.gemini/tmp/<project_hash>/`. | A JSON document per chat: `sessionId`, `startTime`, `lastUpdated`, `messages`. | Planned. **Untested**: no account at hand. |

## What a reader must do

- Find its files, by operating system, in the usual place or in the folder the machine's bootstrap file names (never a path from the server).
- Turn a file into lines and tell which line is the first new one: by byte offset for the files that grow, by message index for the documents that are rewritten.
- Give a conversation a stable `session` (a file name, a session id) and the `application` (the last folder of the working directory the conversation was held in, when the file says it).
- Skip a line or a file it does not understand, and say so in its heartbeat, instead of failing.

The server has a parser for each `format` that turns the lines into turns (who, when, what was said), and keeps the raw lines as they were received, so that a better parser can read them again.

## Sources

- Codex CLI: [Codex CLI Session History: Local Search, Rollout Format, and Knowledge Mining](https://codex.danielvaughan.com/2026/06/01/codex-cli-session-history-local-search-rollout-format-knowledge-mining/), [Session Management in Codex CLI](https://codex.danielvaughan.com/2026/04/13/codex-cli-session-management-resume-fork-transcripts), [agent-top-core](https://docs.rs/crate/agent-top-core/latest/source/src/harness/codex.rs).
- Gemini CLI: [Session management](https://geminicli.com/docs/cli/session-management/), [CLI commands](https://geminicli.com/docs/reference/commands), a community parser ([sessionwiki](https://docs.rs/crate/sessionwiki/0.34.6/source/src/adapters/gemini.rs)).
- Copilot in VS Code: [VS Code Copilot Chat Session Files Technical Guide](https://github.com/NiclasOlofsson/remember-mcp-vscode/wiki/VS-Code-Copilot-Chat-Session-Files-Technical-Guide), [Where to find GitHub Copilot Chat History?](https://github.com/orgs/community/discussions/129888).
- Copilot CLI: [kaizen-cli, copilot_cli collector](https://docs.rs/crate/kaizen-cli/latest/source/src/collect/tail/copilot_cli.rs), [toolpath-copilot](https://docs.rs/toolpath-copilot), [Copilot CLI integration](https://docs.entire.io/integrations/copilot-cli).
