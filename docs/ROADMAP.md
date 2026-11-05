# Roadmap: from here to a MVP in production

> Decided on 2026-11-05, and re-cut the same day once the collector was designed as a service ([ADR 0025](adr/0025-the-collector-is-a-service-per-machine-enrolled-by-a-code-and-configured-from-the-server.md)). The heart of Mnemarr is one chain: **assistants talk, a collector gathers it, Mnemarr keeps it as a memory, any assistant asks for it over MCP.** Everything that is not on that chain waits. A vertical is the whole feature, from the collector to the screen (A to Z), and is done when it is merged.

## What exists (and what does not)

| Link of the chain | State |
|---|---|
| Serve notes to any assistant over MCP (read and write tools, API keys, scopes) | Built |
| Notes of the operator (folders, Git, reader, editor) | Built, and beyond what the MVP needs |
| Gather conversations automatically (Claude Code, by hand on one machine) | **Vertical 1**, built, under review |
| The collector as a service: enrol and watch (2a), install (2b), configure (2c) | **Vertical 2** |
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

### 2. The collector as a service (cut in three)

*Demo of the whole: install it on a Windows workstation in a few minutes, put the PC to sleep, wake it, and see its conversations arrive without an error; add the same on a server by a Compose file.* It is built in three verticals, in this order, so that each half is looked at before the next is laid on it.

#### 2a. Machines join and are watched ([ADR 0027](adr/0027-machines-join-with-a-code-hold-a-key-bound-to-their-name-and-say-they-are-alive.md))

*Demo: in the interface add a machine, run the one command it shows, and the machine appears online; stop the collector and see an intermittent machine go "away" and an always-on one go "silent" with a health check degraded; revoke it, and a key can no longer write under another machine's name.*

- **Enrolment**: a one-time code, added in the interface (approved on the spot) or asked for over MCP (pending until a person approves); exchanged for a key **bound to the machine**; revocation.
- **Heartbeat** at every pass, the **states** of a machine (refused, waiting, failing, online, away, silent), the page of the machines, one readiness check.
- The collector's `enroll` command, its key file and bootstrap file.

#### 2b. The collector as an installed service

*Demo: a Windows workstation and a Linux machine each run it as a service started by the person's own session, from a release binary that was checked.*

- `install`, `uninstall`, `start`, `stop`, `status` (built: an entry in the user's own list of programs that start with their session on Windows, a systemd user unit on Linux; launchd for macOS is left); a container for servers; release binaries with checksums built by the CI on real runners (Windows, Linux, macOS) and an install script that verifies them; the guided route over MCP, as **one command** that Mnemarr fills in (address, code, expected checksum) and that waits for a person to approve; tried on a real Windows machine.
- **The person stays in control of a background process** (asked by the maintainer on 2026-11-05): on Windows and macOS an icon in the notification area says that it runs, shows its state, and offers *Pause* (for an hour, or until resumed), *Resume*, *Open Mnemarr* and *Quit*; on a server without a screen the same through `pause`, `resume` and `status`. A paused collector keeps sending its heartbeat, marked paused, so that Mnemarr shows **paused** and not "silent". The pause survives a restart. New dependencies for the icon are a decision of the pull request.
- The collector never prevents sleep and costs the machine nothing it would notice.

#### 2c. Configuration from the server

*Demo: switch a reader off for a machine in the interface and see the collector obey at its next pass.*

- The `collectors:` section of the configuration document, the API, the interface, an MCP tool; the collector reads its entry at each pass (`GET /api/v1/ingest/config`); the minimum-version answer; the limits of the ingest served to the collector.

### 3. Run it: the MVP in production

*Demo: the production URL; the machines send their conversations; an assistant elsewhere reads them.*

- Image released through the existing pipeline, deployed by GitOps; the data folder on a local disk, backed up.
- Exposure of the ingest and MCP endpoints outside the single sign-on, with Mnemarr's own keys (Q-07).
- The runbook to add a machine.
- **The MVP is vertical 1, vertical 2 (2a and 2b at least) and vertical 3.** The order is 2a, 2b, 3, then 2c: exposing the ingest to the Internet waits for keys that are bound to their machine.

### 4. The other assistants

*Demo: a conversation held in GitHub Copilot (VS Code and CLI) is found over MCP like one held in Claude Code; the same for Codex CLI and Gemini CLI.*

- One reader and one parser per assistant, behind the contract of [the formats](conversation-formats.md), each tried against real files; a reader that could not be tried is marked untested.
- **First, the interface of the parsers** (decided with the maintainer on 2026-11-05, raised by the architecture review four times): `IConversationParser { Format; Turns(lines) }` registered in DI, the ingestor choosing by format and refusing a format no parser handles, roles normalised in `Turn`, the Claude Code particulars (sub-agents, noise tags) moved into its parser. It waits for the second reader so that it is shaped by two cases, not one.

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
