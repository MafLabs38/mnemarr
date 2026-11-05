# Roadmap: from here to a MVP in production

> Decided on 2026-11-05, and re-cut the same day once the collector was designed as a service ([ADR 0025](adr/0025-the-collector-is-a-service-per-machine-enrolled-by-a-code-and-configured-from-the-server.md)). The heart of Mnemarr is one chain: **assistants talk, a collector gathers it, Mnemarr keeps it as a memory, any assistant asks for it over MCP.** Everything that is not on that chain waits. A vertical is the whole feature, from the collector to the screen (A to Z), and is done when it is merged.

## What exists (and what does not)

| Link of the chain | State |
|---|---|
| Serve notes to any assistant over MCP (read and write tools, API keys, scopes) | Built |
| Notes of the operator (folders, Git, reader, editor) | Built, and beyond what the MVP needs |
| Gather conversations automatically (Claude Code, by hand on one machine) | **Vertical 1**, built, under review |
| The collector as a service: install, enrol, configure, watch | **Vertical 2** |
| Run it for real: image, exposure, backup | **Vertical 3** |
| The other assistants (Codex, Copilot, Gemini) | **Vertical 4** |
| Memory is used without being asked (session start) | **Vertical 5** |
| Turn conversations into durable notes (consolidation) | **Vertical 6** |
| Find by meaning (vectors) | **Vertical 7** |

## The verticals

### 1. Capture: a conversation becomes searchable memory

*Demo: talk to an assistant here, then ask another assistant over MCP what was decided: it finds the conversation.*

- The **collector** (Rust) reads the conversation files of Claude Code, masks secrets, sends new lines by position (idempotent, resumable, nothing lost when the server is away), with an ingest-only key.
- The **server** has an ingest API, stores the raw lines (compressed, outside Git, ADR 0004), masks again, writes one Markdown note per conversation in a `Conversations` source, filed by machine then application, indexed at once, titled from its first useful sentence.
- Read only by `Admin` and `Assistant` (`conversations.read`).
- No language model, so no cost per conversation.

### 2. The collector as a service

*Demo: install it on a Windows workstation in a few minutes, put the PC to sleep, wake it, and see its conversations arrive without an error; add the same on a server by a Compose file.*

- **Install**: `install`, `uninstall`, `status`; a container for servers; a per-user service (scheduled task at logon) on Windows, a systemd user unit on Linux; release binaries with checksums; the two routes (a release, or guided over MCP with an approval).
- **Enrolment**: a one-time code approved by a person, exchanged for an ingest key; revocation.
- **Configuration on the server**: the `collectors:` section, the API, the interface, an MCP tool; the collector reads its entry at each pass; the machine's own bootstrap file.
- **Health**: heartbeat; machines `always-on` or `intermittent`; the page that tells online, asleep, silent, refused, failing.
- **The collector never prevents sleep** and costs the machine nothing it would notice.

### 3. Run it: the MVP in production

*Demo: the production URL; the machines send their conversations; an assistant elsewhere reads them.*

- Image released through the existing pipeline, deployed by GitOps; the data folder on a local disk, backed up.
- Exposure of the ingest and MCP endpoints outside the single sign-on, with Mnemarr's own keys (Q-07).
- The runbook to add a machine.
- **The MVP is vertical 1, vertical 2 and vertical 3.**

### 4. The other assistants

*Demo: a conversation held in GitHub Copilot (VS Code and CLI) is found over MCP like one held in Claude Code; the same for Codex CLI and Gemini CLI.*

- One reader and one parser per assistant, behind the contract of [the formats](conversation-formats.md), each tried against real files; a reader that could not be tried is marked untested.

### 5. Memory without being asked

*Demo: open a new session in a project and the assistant already knows what happened there.*

- Start-of-session context (D-12) from the recent conversations and notes of the application, within a budget, wired by the install with the operator's consent.
- A read key per machine, separate from the ingest key (Q-26).

### 6. Consolidation: conversations become durable notes

*Demo: after a day of work, the memory vault holds notes of what was decided and learned, linked to the conversations.*

- A background job (through the assistant runner, ADR 0007) reads new conversations and writes or updates notes per application, supersedes the contradicted ones, keeps provenance (D-17). It also writes the **summary title** of each conversation (opt-in).
- Needs a decision on what pays for it (a subscription through the CLI, or an API key); it costs per conversation.

### 7. Search by meaning

*Demo: ask with other words than the note's and still find it.*

- Embeddings computed locally, vectors in `sqlite-vec`, hybrid with the keyword search (ADR 0012).

## Not now

The reader and editor of notes beyond what exists, more kinds of sources, retention policies, the chat panel, the assistant runner for chat. They come after the MVP, in the order the use shows.
