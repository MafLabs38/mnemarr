# 0035 — The chat is a panel, a proxy and a runner, with an adapter for each kind of model

- **Status**: accepted
- **Date**: 2026-11-08
- **Spec**: D-55; builds D-21 and F-UI-02, F-RUN-01 on the design of [0007](0007-assistant-runner-headless-agent-cli.md) and the chat of Portarr (its ADR 0008)

## Context

Search by meaning ([0034](0034-search-by-meaning-is-computed-on-the-host-and-merged-with-the-words-by-rank.md)) gives passages, not answers. The maintainer wants a chat in the interface where a model
uses Mnemarr as a person would: asks the memory, adds a line to a course note and pushes it, declares a source. It must work with the maintainer's Claude subscription, as the titles of
conversations do (no pay-per-use key), and it must not depend on Claude: someone with another provider, a key, or a local model plugs it in.

## Decision

1. **Three parts, as in Portarr.** A panel in the interface; a proxy in Mnemarr (`/api/v1/chat/*`, off and 404 without `MNEMARR__AGENT__URL`); and an optional **runner**
   (`agent/`, its own image `mnemarr-agent`, Node 22 running TypeScript as it is, no dependency) that holds the model's credentials and runs the model. The main image holds none.
2. **An adapter for each kind of model; nothing else knows which.** The contract (`agent/src/adapters/adapter.ts`): `start` a process for a conversation, `send` a message,
   `answer` a permission, `interrupt`, `close`; the adapter turns what its model does into the runner's events (text, tool calls, permission requests, usage). Two adapters
   shape it, so that it is not shaped by one case: **Claude Code** (a CLI driven headless, on the maintainer's subscription: `CLAUDE_CODE_OAUTH_TOKEN`, never an API key by default)
   and **OpenAI-compatible** (any `/chat/completions` API with tool calls: OpenAI, Azure OpenAI, Ollama, vLLM, a gateway: an address, a key if it needs one, the model names). The second
   has no CLI, so it runs the agent loop itself, with a small MCP client; it is tried against stand-ins, **not against a real provider** (no key was available): it is
   marked untested with one until someone tries it. Codex or Copilot CLIs are one adapter each, later.
3. **The only tool is Mnemarr's MCP server, and the policy is the runner's, not the model's.** Reads (`get_`, `list_`, `find_`, `read_`, `search`) run; every other Mnemarr tool becomes
   a **card** that waits for the person (Approve sends the arguments as shown, Reject tells the model not to retry); any other tool is refused unasked. The CLI has no built-in tool.
4. **The model acts as the person, within what their role gives, and the log says it was the chat.** Each message carries a token (`mnc_…`, kept as a hash) tied to the person and the
   conversation: 30 minutes from the last message, 12 hours at most, valid on the MCP server **and nowhere else**, with no permission of its own: what it may do is the person's
   role **as it is now**, read at each call, so a person who loses the role or the account loses the chat's hands at once. The audit records `alice (via chat:<id>)`. A new permission
   `chat.use` goes to Admin, Operator and Reader; the roles of services have none, and a key of a service cannot use the chat even with a role that has it: the chat is for people. A conversation belongs to the person who
   started it; another person's is "not found". A person keeps 50 conversations at most (`MNEMARR__AGENT__MAXCONVERSATIONSPERPERSON`; the runner's cap is one for everybody, it deletes nothing to make room and answers 429 when it is full, so this times the number of people should stay under it), and the runner carries
   no key of its own to the MCP server: a message without the token Mnemarr minted for it reaches nothing.
5. **What the model can look after.** The MCP server gains the tools that look after Mnemarr: `get_status`, `create_source`, `update_source`, `delete_source`, `index_source`,
   `sync_source` (with an optional Git repository), and `request_collector_enrolment` moves beside them. Those that change something are offered only when the operator turned the
   writing tools on (`Mcp:WriteTools`, as for notes), under the permission of the REST API. **"Add this to my courses and push it" needs nothing new**: a knowledge source that is
   a Git checkout and open for writing commits and pushes what `create_note`, `append_to_note` and `update_note` write (ADR 0023): the chat asks the person, then the notes tools do it.
   The source tools follow `Mcp:WriteTools` and not a setting of their own: a source is declared in Mnemarr's database, not in its environment, so it is not one of the "settings"
   that D-20 keeps off by default; the same switch that lets the model write notes lets it declare sources. **Settings of Mnemarr are not changed by the chat**: they are the operator's environment or configuration as code.
6. **Where it is kept.** The conversations (what was said, the tool calls) are in the runner's volume, **and are sent to the model's provider**: the operator accepts this by choosing it.
   Mnemarr keeps whose a conversation is, and the tokens. The runner's secret (32 characters or more) is shared with Mnemarr; the runner is not published. The volume is backed up with the runner's other data, apart from Mnemarr's
   database (a lost volume loses the conversations, and Mnemarr forgets their owners at its next listing). Mnemarr passes the runner's JSON through, so the two images move together: update both.

## Consequences

- Another image to build and run (as in Portarr) and a second one to publish; without it everything else works.
- A subscription has usage limits shared with the maintainer's own use; a chat turn costs what a Claude Code turn does.
- The model sees what the person's role lets them see, conversations included when the role reads them; nothing is filtered for the model that is not for the person.
- The chat does not know Portarr: what the infrastructure is today is for Portarr's MCP server (a second server for the agent is not now).
- Mnemarr's own chat is not captured into its conversations pipeline (D-16) for now: what is said in the chat stays in the runner's volume. The natural path is for the runner to post finished turns to `/api/v1/ingest`, through the API (D-02).
- The readiness probe (`/health/ready`) reports the chat: degraded with `agent-unreachable`, `agent-secret-refused` or `agent-no-credentials`, so that a companion that watches Mnemarr sees what a person would learn by opening the panel.
- Not now: a second MCP server for the agent, Codex and Copilot adapters, a real provider for the OpenAI adapter, attachments, the limit of a conversation to some sources, retention of the runner's history.
