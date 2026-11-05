# Roadmap: from here to a MVP in production

> Decided on 2026-11-05 with the maintainer. The heart of Mnemarr is one chain: **assistants talk, a collector gathers it, Mnemarr keeps it as a memory, any assistant asks for it over MCP.** Everything that is not on that chain waits. A vertical is the whole feature, from the collector to the screen (A to Z), and is done when it is merged.

## What exists (and what does not)

| Link of the chain | State |
|---|---|
| Serve notes to any assistant over MCP (read and write tools, API keys, scopes) | Built |
| Notes of the operator (folders, Git, reader, editor) | Built, and beyond what the MVP needs |
| Gather conversations automatically | **Vertical 1** |
| Run it for real: image, exposure, collectors on the machines, backup | **Vertical 2** |
| Memory is used without being asked (session start, one-command install) | **Vertical 3** |
| Turn conversations into durable notes (consolidation) | **Vertical 4** |
| Find by meaning (vectors) | **Vertical 5** |

## The verticals

### 1. Capture: a conversation becomes searchable memory

*Demo: talk to an assistant here, then ask another assistant over MCP what was decided: it finds the conversation.*

- The **collector** (Rust) reads the conversation files of Claude Code, masks secrets, sends new lines by position (idempotent, resumable, nothing lost when the server is away), with an ingest-only key per machine.
- The **server** has an ingest API, stores the raw lines (compressed, outside Git, ADR 0004), checks the masking again, writes one readable Markdown note per conversation, per application, in a new `Conversations` source, indexed at once.
- Machines that sent something, and when, are visible (an agent silent is not an agent idle).
- No language model, so no cost per conversation.

### 2. Run it: the MVP in production

*Demo: the production URL; the three machines send their conversations; an assistant elsewhere reads them.*

- Image released through the existing pipeline, deployed by GitOps; the data folder on a local disk, backed up.
- Exposure of the ingest and MCP endpoints outside the single sign-on, with Mnemarr's own keys (Q-07).
- Collector packages and service files for Linux and Windows, and the runbook to add a machine.
- **The MVP is vertical 1 plus vertical 2.**

### 3. Memory without being asked

*Demo: open a new session in a project and the assistant already knows what happened there.*

- One command installs the collector service and wires the MCP configuration and the start-of-session context of the assistant (D-12), with the operator's consent.
- Context for a session: the recent conversations and notes of the application, within a budget.
- A read key per machine, separate from the ingest key (Q-26).

### 4. Consolidation: conversations become durable notes

*Demo: after a day of work, the memory vault holds notes of what was decided and learned, linked to the conversations.*

- A background job (through the assistant runner, ADR 0007) reads new conversations and writes or updates notes per application, supersedes the contradicted ones, keeps provenance (D-17).
- Needs a decision on what pays for it (a subscription through the CLI, or an API key); it costs per conversation.

### 5. Search by meaning

*Demo: ask with other words than the note's and still find it.*

- Embeddings computed locally, vectors in `sqlite-vec`, hybrid with the keyword search (ADR 0012).

## Not now

The reader and editor of notes beyond what exists, more kinds of sources, other assistants than Claude Code, retention policies, the chat panel, the assistant runner for chat, alerts. They come after the MVP, in the order the use shows.
