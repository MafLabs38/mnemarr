# Mnemarr — Functional and Technical Specification

> **Status**: draft v0.1, requirements gathering (interview #1).
> **Related documents**: [VISION.md](VISION.md) (why, raw notes) · [adr/](adr/README.md) (decision records)
> **Last updated**: 2026-11-01
> **Audience**: the project owner, contributors.
>
> ⚠️ This document is intended for the **public repo**. It contains **no real infrastructure values**. All names (`server-1`, `homelab.example`, `notes-vault`, `memory-vault`…) are examples.

## Legend

| Marker | Meaning |
|---|---|
| ✅ | **Frozen**: decided, does not change without a new ADR |
| 🟡 | **Proposed**: drafted by the analyst, pending validation |
| ❓ | **Open**: unanswered question, see §7 |
| ♻️ | **Superseded**: replaced by a later decision, named in the row |

Identifiers: `D-xx` decision · `F-XXX-nn` feature · `Q-xx` question.

---

## Table of contents

1. [Vision](#1-vision)
2. [Decisions](#2-decisions)
3. [Architecture](#3-architecture)
4. [Data model](#4-data-model)
5. [Feature catalog](#5-feature-catalog)
6. [Security and privacy](#6-security-and-privacy)
7. [Question register](#7-question-register)
8. [Glossary](#8-glossary)
9. [Changelog](#9-changelog)

---

## 1. Vision

Mnemarr is a self-hosted **long-term memory for people and AI assistants**, in the spirit of the *arr suite. See [VISION.md](VISION.md) for the pitch and the problem.

It has **two aspects** that serve two kinds of reader:

- **Consolidated memory**: Markdown notes in a Git repository (Obsidian-compatible), **created, structured and maintained entirely by Mnemarr's AI**. The operator can read them; the AI owns them. It is the readable source of truth of what was learned.
- **Searchable memory**: a search index (by text and by meaning) **derived** from the consolidated notes, from the raw conversations of every assistant on every machine, and from the documentation of the applications. It is compiled automatically and can be rebuilt at any time.

Everything is **automatic**: capture, consolidation and indexing involve no manual step. The goal is that an assistant starting a new session, on any machine, can find what was decided, learned and said before about an application, without the operator re-explaining it or rewriting instruction files by hand.

**What Mnemarr is not**: it does not describe the architecture or the environment (machines, network, deployment). That is Portarr's job, and an assistant learns its environment through Portarr's MCP server. Mnemarr remembers what happened and what was learned; it does not duplicate what Portarr observes.

---

## 2. Decisions

| # | Decision | Rationale | ADR |
|---|---|---|---|
| ✅ D-01 | **Mnemarr is an *arr-style application**: a single container, .NET (C#) API, React (TypeScript) frontend, API first (the UI consumes the REST API). | Same philosophy and stack as the rest of the family. | [0002](adr/0002-arr-like-stack-and-external-helpers.md) |
| ✅ D-02 | **External helpers** (agents, collectors, importers) are not part of the application and may use any language. They talk to the container only through its API. | The philosophy governs the container, not what serves it. A helper never defines the application. | [0002](adr/0002-arr-like-stack-and-external-helpers.md) |
| ✅ D-03 | **Consolidated memory is Markdown in Git** (Obsidian-compatible), the readable source of truth of what was learned. The vector index is **derived** from it and can be rebuilt. | A pure vector memory is unreadable by a person; the operator wants to be able to read the memory, not only the AI. | [0003](adr/0003-git-hosted-markdown-vaults-split-by-audience.md) |
| ✅ D-04 | **Git is the central distribution** of Markdown vaults, synchronised with the Obsidian Git plugin. The paid Obsidian Sync is not used. | Any device gets a vault by cloning it. | [0003](adr/0003-git-hosted-markdown-vaults-split-by-audience.md) |
| ✅ D-05 | **One repository per audience, not per topic.** Three kinds of content, each with a fixed rule: a **knowledge vault** (hand-written notes such as courses; read-only for Mnemarr, which never touches it), **application documentation** (read from each application's repository; read-only), and the **memory vault** (consolidated memory; the only one Mnemarr writes). Selectivity comes from which repository a device clones, with a token scoped to it. | Git cannot restrict what a clone downloads; a work PC must never hold material it should not. | [0003](adr/0003-git-hosted-markdown-vaults-split-by-audience.md) |
| ✅ D-06 | **Three data layers**: raw conversations, consolidated memory, application documentation. **Raw conversations are not stored in Git**; they live in Mnemarr's data folder, compressed, with a retention policy. | They only grow, nobody reads them as Markdown, and they cannot be pruned in Git. | [0004](adr/0004-raw-conversations-outside-git.md) |
| ✅ D-07 | **Database and vector index on a local fast disk**, never on a network share. Bulk raw files and backups may be on a share. | File locking, latency, spun-down disks. | [0004](adr/0004-raw-conversations-outside-git.md) |
| ✅ D-08 | **A collector agent** runs on every machine where an assistant **executes** (not where the screen is), reads its conversation files and sends them to Mnemarr. First implementation in **Rust**; Windows and Linux builds. | Conversations are on the disk of the executing machine; a remote session stores them on the remote machine. | [0005](adr/0005-collector-agent.md) |
| ✅ D-09 | The agent is **outbound-only over HTTPS**, keeps a **local queue** when the server is unreachable, and uses an **ingest-only credential per machine**, revocable. | Must work from any network (mobile connection, away from home) and never lose data. | [0005](adr/0005-collector-agent.md) |
| ✅ D-10 | Secrets are **masked before sending** (agent) and **checked again** on the server before storage and indexing. The agent **reports its own health**. | Conversations can hold pasted secrets; a silent agent must not be mistaken for an idle one. | [0005](adr/0005-collector-agent.md) |
| ✅ D-11 | **Application documentation is indexed in place**, read-only. Specifications and behaviour descriptions stay in their own repository and evolve when the application is implemented; Mnemarr never edits them and never keeps a second copy. | No stale copies; an assistant reads an application's documentation without opening its folder. | — |
| 🟡 D-12 | **Retrieval does not depend on the model's initiative**: a start-of-session hook queries Mnemarr (project, working directory) and injects the result; the MCP server remains for on-demand search. | The previous attempt failed because the assistant did not reliably query the server. | — |
| 🟡 D-13 | **Health is first-class**: the agent and the server expose health; Mnemarr alerts when a machine has been silent for too long and registers in Portarr as a companion so that an outage is visible. | The previous attempt failed silently. | — |
| ✅ D-14 | **The memory vault belongs to the AI.** Mnemarr creates the notes, decides their structure, updates them and supersedes outdated ones; the operator reads. The knowledge vault and the application documentation are never written by Mnemarr. | One writer means no edit conflicts and no review queue; the memory is fully automatic. | — |
| ✅ D-15 | **Mnemarr does not describe the architecture or the environment.** Machines, network and deployment belong to Portarr and its MCP server. | Constellation rule: Portarr sees, Mnemarr remembers. Duplicating it would go stale. | — |
| ✅ D-16 | **Everything is automatic**: capture, consolidation and indexing of all sources into the vector memory run without manual steps. | The point of the product is a memory nobody has to feed. | — |
| 🟡 D-17 | **Consolidation**: a background job reads new raw conversations and turns them into durable notes (what was decided, learned, tried, and what happened), per application. It updates existing notes instead of piling up, supersedes contradicted ones, and keeps a link from each fact to the conversation it came from. | Raw conversations are too noisy to be a readable memory; links let the operator verify. | — |
| ✅ D-18 | **Mnemarr is the orchestrator of the brain.** Agents bring the data; Mnemarr compiles it, writes the Markdown notes and runs the indexing. Its **interface is minimal and only configures the brain's options** (sources, agents, model providers, schedules, retention, masking). Reading the memory is done in Obsidian, not in Mnemarr. | The product's job is to run the memory, not to be a note browser. | — |
| ✅ D-19 | **The REST API exposes every capability of Mnemarr.** Nothing is reachable only from the UI or the MCP server. | The UI, the chat, the agents and the MCP server all consume the same API. | [0006](adr/0006-api-first-with-mcp-on-top.md) |
| ✅ D-20 | **An MCP server sits on top of the API, with two families of tools**: *memory tools* (search, read, add an observation, correct or retire a memory) for any assistant, and *administration tools* (status, trigger pull, indexing, consolidation, read settings). Tools that change settings are off by default and enabled separately. Portarr links to this MCP server. | "Intelligence outward": any agent can use Mnemarr intelligently, and the chat can operate Mnemarr itself. | [0006](adr/0006-api-first-with-mcp-on-top.md) |
| ✅ D-21 | **A chat in the UI, like Portarr's**: an agent CLI runs headless behind an adapter in an **optional separate container** (the assistant runner); its only tool is Mnemarr's MCP server; changes to settings wait for the operator. Claude Code first, through the operator's Claude subscription. | Reuses a design already proven in Portarr; the main image holds no model credentials. | [0007](adr/0007-assistant-runner-headless-agent-cli.md) |
| 🟡 D-22 | **Consolidation runs through the same assistant runner**, under a stricter-scoped, unattended policy (read conversations; create, update and supersede memory notes; nothing else). Without the runner, Mnemarr still ingests, indexes and serves search; it lacks consolidation and the chat. | "Intelligence inward": one mechanism for both uses. | [0007](adr/0007-assistant-runner-headless-agent-cli.md) |
| ✅ D-23 | **Same quality chain as Portarr**: same pipeline, coverage thresholds, SonarQube quality gate, CodeQL, mutation testing and the four review agents (tests, quality, security, architecture), derived from the same template. | The project is free but must be a quality application others can adopt. | [0008](adr/0008-same-quality-chain-as-portarr.md) |
| ✅ D-24 | **Infrastructure as code for delivery**: repository settings stay in the organisation template; the product ships generic deployment examples under `deploy/`; personal deployments live in the maintainer's private infrastructure repository. A foundation vertical brings the pipeline before any feature vertical. | Reproducible, reviewable, and no personal value in the public repo. | [0008](adr/0008-same-quality-chain-as-portarr.md) |
| ✅ D-25 | **Vectors live inside Mnemarr's SQLite database, behind an `IVectorStore` interface**; search is hybrid (full text plus vectors). A dedicated vector database stays a possible provider. The implementation is `sqlite-vec` (D-30, [0012](adr/0012-vector-store-and-embeddings-spikes.md)). | One container, nothing more to run or back up; vectors are rebuildable. | [0009](adr/0009-embedded-vector-search-and-local-embeddings.md) |
| ✅ D-26 | **Embeddings are computed locally** (ONNX Runtime) behind an `IEmbeddingProvider` interface, with a small multilingual model, Granite by default (D-31). The model identifier is stored with each vector; changing it re-embeds automatically. An external API is opt-in only. | Privacy by default; French and English mixed. | [0009](adr/0009-embedded-vector-search-and-local-embeddings.md) |
| ✅ D-27 | **Mnemarr authenticates every API and MCP call itself** (per-machine ingest tokens, per-user API keys). The ingest and MCP endpoints may be published on a dedicated host name outside the proxy's single sign-on; the UI stays behind it. | Agents have no browser; safety must not rely on the proxy. | [0010](adr/0010-external-exposure-and-delegated-backups.md) |
| ✅ D-28 | **Mnemarr produces consistent snapshots of its data; the operator's backup tool ships them** to remote storage (encrypted). The index is rebuildable; raw conversations, settings and credentials are what matter. | Conductor principle; a live SQLite file must not be copied naively. | [0010](adr/0010-external-exposure-and-delegated-backups.md) |
| ✅ D-29 | **Default port 8383.** 8686 was Lidarr's; 8383 is used by no *arr application. | Installed next to the other *arr apps without a collision. | [0011](adr/0011-default-port.md) |
| ✅ D-30 | **The vector store is `sqlite-vec`, pinned (0.1.9), behind `IVectorStore`**; its binary is fetched by version for amd64 and arm64 with its checksum verified. The store is keyed by (model identifier, dimension): one `vec0` table per model, so a model change re-embeds into a new table while search keeps reading the active one. The extension is loaded by code from the pinned path, then extension loading is disabled; every filter is a bound parameter. The in-process search is a documented alternative of the same interface. | Measured on 20,000, 100,000 and 300,000 vectors: flat memory (about 80 MB), under 200 ms at 300,000 on two cores. | [0012](adr/0012-vector-store-and-embeddings-spikes.md) |
| ✅ D-31 | **The default embeddings model is `granite-embedding-97m-multilingual-r2`** (full-precision ONNX, 384 dimensions) behind `IEmbeddingProvider`, which tells a query from a passage; `multilingual-e5-small` is the supported alternative; no quantized model. The model, its location and its dimension are settings with documented defaults, only the listed models are supported, and its files are pinned by revision with a SHA-256 per file verified before use. | Tied with e5-small on quality on French notes, with a 32,768-token context, Matryoshka dimensions and Apache-2.0. | [0012](adr/0012-vector-store-and-embeddings-spikes.md) |

---

## 3. Architecture

### 3.1 Overview 🟡

```text
 Machines where an assistant runs                       Mnemarr host (one container)
 ┌────────────────────────────┐                        ┌───────────────────────────────────┐
 │ Assistant ── conversations │                        │ REST API ── MCP server ── UI       │
 │ files on disk              │                        │   │                                │
 │   ▲                        │  HTTPS, outbound only  │   ├─ ingest (agents)               │
 │ Collector agent ───────────┼───────────────────────►│   ├─ search (text + meaning)       │
 │ (queue, masking, health)   │  ingest-only token     │   └─ sources (Git repositories)    │
 └────────────────────────────┘                        │ SQLite + vector index (local disk) │
                                                       │ Raw conversations (data folder)    │
 Devices                                               └───────────┬───────────────────────┘
 ┌────────────────────────────┐   Obsidian Git plugin              │ clone / commit / push
 │ Obsidian: notes-vault      │◄──────────────────────┐            ▼
 │ Obsidian: memory-vault     │◄─────────┐            └──── Git remote: notes-vault (read-only for Mnemarr)
 └────────────────────────────┘          └───────────────── Git remote: memory-vault (read-write)
                                                            Git remote: application repositories (read-only, docs)
```

### 3.2 Sources 🟡

A **source** is a Git repository that Mnemarr clones and indexes.

| Kind | Mode | Example | Mnemarr does |
|---|---|---|---|
| Memory vault | read-write, Mnemarr only | `memory-vault` | Indexes; writes consolidated notes as commits and pushes (D-14) |
| Knowledge vault | read-only | `notes-vault` | Indexes; never writes |
| Application docs | read-only | `my-app` (its `docs/`) | Indexes in place (D-11) |

Every search result carries its source. Filters and assistant permissions apply per source: an assistant can be allowed `memory-vault` and not `notes-vault`.

### 3.3 Memory vault layout 🟡

The structure belongs to the AI (D-14), so it is not fixed here. A starting convention, which the consolidation job may evolve:

```text
memory-vault/
 └─ <application>/      decisions, lessons, behaviour observed, history
```

Whatever the structure, the application is recorded in each note's front matter, so "everything about application X" is a filter. Nothing about machines or deployment is stored (D-15).

### 3.4 Ingestion pipeline 🟡

1. The agent reads new conversation lines, masks secrets, queues, uploads (idempotent).
2. Mnemarr stores the raw lines as received, then chunks and embeds them in the background.
3. Consolidation (D-17): after a session has been inactive for a while, a background job reads it and creates or updates consolidated notes in the memory vault, then commits and pushes. The job is carried out by the assistant runner with Mnemarr's MCP tools (D-22).
4. Git sources are pulled periodically; changed files (documentation, consolidated notes) are re-read and re-indexed.

### 3.5 Search 🟡

Hybrid: full-text ranking and vector similarity, merged. Filters by source, application, note type, tag, date, author (human or assistant), machine.

### 3.6 Deployment 🟡

One container image (multi-arch, on GHCR), deployable with Docker or Kubernetes, plus an **optional second image, the assistant runner** (D-21, D-22), which alone holds the model login. Its data folder (database, index, raw conversations) is on a local disk; the backup covers the data folder and the credentials, since the index is rebuildable from the sources.

---

## 4. Data model

Draft; the exact schema comes with the first vertical.

| Entity | Description |
|---|---|
| **Source** | A Git repository: URL, kind, mode, credential reference, last pull |
| **Note** | A Markdown file in a source: path, front matter (type, application, status, author, supersedes, related), content hash, indexed chunks. Notes without front matter are valid; type and application are inferred from the source and path |
| **Conversation** | A session from one assistant on one machine: tool, machine, project, start and end |
| **ConversationLine** | One raw line as received, with timestamp and position |
| **Agent** | A registered collector: machine name, credential, last seen, queue size, version |
| **Chunk** | An indexed fragment of a note or conversation, with its embedding and the identifier of the embedding model |
| **AuditEvent** | Who read or wrote what, through which interface |

Note types and structure are chosen by the consolidation job within a small set of conventions (Q-06).

---

## 5. Feature catalog

All 🟡 until validated. Identifiers are provisional.

| ID | Feature |
|---|---|
| F-SRC-01 | Declare, clone and pull Git sources; read-only and read-write modes |
| F-SRC-02 | Index application repositories' `docs/` in place |
| F-COL-01 | Agent: read conversation files, resume by position, upload idempotently |
| F-COL-02 | Agent: local queue and retry; masks secrets; one-command install; reports health |
| F-COL-03 | Server: ingest API with per-agent ingest-only credentials, revocable |
| F-SEA-01 | Hybrid search with filters; results show their source |
| F-SEA-02 | Re-embedding when the embedding model changes |
| F-MCP-01 | MCP tools: search, read, add, update; write tools enabled separately and audited |
| F-MCP-02 | Start-of-session context injection (D-12) |
| F-CON-01 | Consolidation job: raw conversations into durable notes per application (D-17) |
| F-CON-02 | Update and supersede existing notes; detect duplicates and contradictions |
| F-CON-03 | Provenance: each fact links back to the conversation it came from |
| F-IDX-01 | Automatic compilation of all sources into the vector index, incremental on change |
| F-LIF-01 | Supersede or archive notes instead of overwriting; keep history (Git provides it) |
| F-HLT-01 | Health page: agents last seen, queue sizes, pull status, index status, with reason codes. Built on the same `IHealthCheck` registrations as the probes (`/health/*`), exposed as a `/api/v1` resource; no second health mechanism |
| F-HLT-02 | Alert when an agent or source has been silent too long |
| F-UI-01 | Minimal *arr-style UI that consumes the REST API and only configures the brain (D-18): sources, agents and their credentials, language-model and embeddings providers, schedules, retention, masking rules |
| F-UI-02 | Chat panel that operates Mnemarr through its MCP tools ("index the data now"); same principle as Portarr's chat (D-21) |
| F-API-01 | REST API covering every capability, documented with OpenAPI (D-19) |
| F-MCP-03 | Administration tools: status, trigger pull, indexing, consolidation; settings changes behind a separate switch (D-20) |
| F-MCP-04 | Memory tool to correct or retire a wrong memory (answers Q-18) |
| F-RUN-01 | Assistant runner: headless agent CLI behind an adapter, chat policy and job policy (D-21, D-22) |
| F-IMP-01 | One-shot import of an existing Markdown vault |

---

## 6. Security and privacy

- **Nothing leaves the host** unless the operator configures it. Any external embeddings or language-model API is opt-in and documented.
- **Secrets are never stored in memories nor returned by the API.** They are masked in the agent before sending and verified again on the server before storage and indexing (D-10). Masking rules are an open question (Q-13).
- **Agent credentials** are per machine, ingest-only, revocable. A compromised agent can add conversations; it cannot read any memory.
- **Assistant credentials** (MCP, API) are scoped per source and per verb (read-only or read-write); every read and write is audited.
- **Git credentials** are scoped to one repository each. A device clones only the repositories it should hold.
- **The ingest endpoint must be reachable from outside the home network** and therefore cannot rely on an interactive single sign-on; the per-agent credential protects it (Q-07).
- **Anonymous endpoints are an explicit, short list.** Only `/health/live` and `/health/ready` (and the static shell of the UI) are anonymous. Everything under `/api/v1` and `/mcp` requires authentication as soon as the authentication vertical lands, and no later than the first endpoint that exposes memory content or settings. Until then `/api/v1/system/status` (version, runtime, uptime) and the OpenAPI document are anonymous on purpose; the OpenAPI document moves behind authentication in production, since a public host name must not hand out a map of the API and the exact version. Behind a reverse proxy, `ForwardedHeaders` and `AllowedHosts` are set with the ingest vertical, before rate limiting and audit use the client address.
- Raw conversations are sensitive data: they are part of the backup, have a retention policy and are not exposed beyond the authenticated API.

---

## 7. Question register

Status: ❓ open · 💬 under discussion · ✅ resolved (with a reference to the decision).

| # | Topic | Question | Status |
|---|---|---|---|
| Q-01 | Name | Is "Mnemarr" confirmed? Anything explicitly out of scope for v1? | ❓ |
| Q-02 | Storage | Vector store | ✅ Embedded in SQLite behind an interface (D-25); `sqlite-vec`, pinned (D-30). Metadata filtering and concurrent writes are for the search vertical's tests |
| Q-03 | Embeddings | Which embeddings model, and what happens to vectors when it changes? | ✅ Local, small, multilingual, behind an interface; model id stored with each vector (D-26); `granite-embedding-97m-multilingual-r2` by default (D-31) |
| Q-04 | Consolidation model | Which model does the consolidation, and where does it run? | ✅ Claude Code through the operator's subscription, in the assistant runner (D-21, D-22); the adapter leaves room for others. Conversations are sent to the provider, after masking (D-10). Remaining points: Q-19 |
| Q-05 | Writing model | Who writes the memory vault, and how are conflicts avoided? | ✅ Mnemarr's AI only, the operator reads (D-14) |
| Q-06 | Note conventions | Which minimal conventions (front matter fields, naming) does the AI follow so that notes stay searchable and filterable by application? | ❓ |
| Q-07 | Exposure | How is the ingest endpoint exposed? | ✅ Dedicated host name outside the proxy's single sign-on, with Mnemarr's own authentication (D-27); VPN as an alternative |
| Q-08 | Mobile Git | Verify the Obsidian Git plugin's mobile limits (authentication, size, conflicts) and decide which vaults go on a phone, read-only or not. | ❓ |
| Q-09 | Cross-vault links | Front matter convention for references across vaults (Obsidian cannot resolve them), and how Mnemarr resolves them. | ❓ |
| Q-10 | Retention | How long are raw conversations kept, and can they be thinned (for example, tool output dropped) after a delay? Expected volume? | ❓ |
| Q-11 | Assistants | Beyond Claude Code on a machine: web and mobile chat, other assistants? How are they captured (export and import)? | ❓ |
| Q-12 | Conflicts | Conflict handling if the operator edits a memory note anyway. | ✅ The operator opens the memory vault in Obsidian to read and does not intend to edit it (D-14). Devices are set to pull only (no automatic push), so a stray edit cannot reach the remote. Mnemarr pulls before every push; if a conflict still occurs, it keeps the remote state, stops pushing that note and reports it on the health page (F-HLT-01) |
| Q-13 | Masking | Which secret patterns are masked, and how to avoid both leaks and over-masking? | ❓ |
| Q-14 | Users | Single operator with several assistants, or several people? Authentication for the UI and the API. | ❓ |
| Q-15 | Quality | How are duplicates and contradictions detected during consolidation (F-CON-02)? | ❓ |
| Q-16 | Formats | Conversation file formats are not public contracts: how are readers versioned and tested against format changes? | ❓ |
| Q-17 | Workspaces | Application documentation can be read from Git repositories, but some applications live only in a local workspace with no remote. Is documentation also sent by the agent from workspaces, or only read from Git? | ❓ |
| Q-18 | Corrections | When the operator sees a wrong memory, how is it corrected? | 💬 By telling an assistant or the chat, which calls the correction tool of the MCP server (F-MCP-04); to confirm |
| Q-19 | Subscription | Is automated, unattended use of a Claude subscription this way allowed by the provider's terms? How is the shared usage limit protected (cadence, per-run cap, pause when the limit is near)? The operator expects consolidation to be cheap and to run on a light model (Haiku); the model is a setting of the job. | 💬 |
| Q-20 | License | Which license? Portarr uses GPL-3.0 (ADR 0003 there); the foundation code declares GPL-3.0-or-later in its package metadata as a placeholder, and no LICENSE file exists yet. | ❓ |
| Q-21 | URL base | *arr applications can be served under a path of a host name (a URL base). Mnemarr starts as host-name-only: absolute `/api` paths and a root fallback. Is a URL base needed? If yes, a base is added in `apiGet` (the one place the API is addressed, DESIGN.md) and a `basename` on the router; the logo and favicon are the only other absolute paths. | ❓ |

---

## 8. Glossary

| Term | Meaning |
|---|---|
| **Vault** | A folder of Markdown notes opened in Obsidian; here, always a Git repository |
| **Source** | A Git repository declared to Mnemarr, with a mode |
| **Knowledge vault** | Hand-written notes (courses, learning); never touched by Mnemarr |
| **Memory vault** | Consolidated notes created and maintained by Mnemarr's AI; the operator reads |
| **Consolidation** | The background job that turns raw conversations into durable notes |
| **Raw conversation** | Conversation lines as received from an agent, before any processing |
| **Collector agent** | The program on each machine that reads assistant conversations and sends them |
| **Assistant runner** | The optional container that runs an agent CLI headless for the chat and the consolidation. Not to be confused with the collector agent |
| **Embedding** | A list of numbers that stands for the meaning of a passage of text; similar meanings give nearby numbers. Computed by the embeddings model |
| **Vector store** | Where the embeddings are kept and searched for the nearest ones to a question |
| **MCP** | Model Context Protocol; how assistants call Mnemarr's tools |

---

## 9. Changelog

| Date | Change |
|---|---|
| 2026-11-01 | Created. Interview #1 (architecture): D-01 to D-14, ADR 0001 to 0005, Q-01 to Q-16. Two Git vaults split by audience, three data layers, collector agent. |
| 2026-11-01 | Interview #1 (continued). Scope corrected: Mnemarr does not describe the architecture or environment (D-15, Portarr's job). The memory vault belongs to the AI (D-14), everything is automatic (D-16), consolidation proposed (D-17). Q-05 resolved; Q-17, Q-18 added. `infra/` folder and the inbox removed. |
| 2026-11-01 | Interview #1 (continued). D-18: Mnemarr is the orchestrator of the brain, minimal configuration-only UI (F-UI-01 reduced), reading is done in Obsidian. Q-12 resolved (pull-only devices). |
| 2026-11-01 | Interview #1 (continued). D-19 to D-22, ADR 0006 and 0007: API exposes everything, MCP on top with memory and administration tools, chat and consolidation through an assistant runner reusing Portarr's design, Claude subscription. Q-04 resolved, Q-18 proposed, Q-19 added. |
| 2026-11-01 | Interview #1 (continued). D-23, D-24, ADR 0008: same quality chain and infrastructure-as-code delivery as Portarr, foundation vertical first. Consolidation expected on a light model (Q-19). |
| 2026-11-01 | Interview #1 (continued). D-25 to D-28, ADR 0009 and 0010: embedded vector search and local embeddings behind interfaces (spikes pending), exposure outside the proxy's sign-on with own authentication, snapshots shipped by the operator's backup tool. Q-02, Q-03, Q-07 resolved. Glossary: embedding, vector store. |
| 2026-11-01 | Foundation vertical started (issue 3). Q-20 added: the license is not decided. |
| 2026-11-01 | Foundation review (four agents). D-29 and ADR 0011: default port 8383 (8686 is Lidarr's). Rule on anonymous endpoints (section 6), health built on the probes' checks (F-HLT-01), Q-21 (URL base). Fixes: start time of the application, security headers on error responses. |
| 2026-11-01 | Application shell on the foundation: header with the brain logo, left menu (Overview and System built; Sources, Agents and Settings shown as "Soon"), Portarr's theme and fonts, docs/DESIGN.md. F-UI-01 starts here. |
| 2026-11-01 | Review of the shell (four agents). Fixes: one state on the Overview (a failed refresh no longer shows online and unreachable together), the API reachable label until F-HLT-01 exists, a single door to the API (`apiGet`, `useApiQuery`), fonts no longer inlined as `data:` (the Content-Security-Policy refuses them), a skip link, typed status icons. |
| 2026-11-03 | The two spikes of ADR 0009, ADR 0012: D-30 (`sqlite-vec`, pinned) and D-31 (Granite 97M R2 by default). Q-02 and Q-03 resolved. |
