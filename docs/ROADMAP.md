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

#### Linking: several Mnemarr and linking by asking ([ADR 0030](adr/0030-a-collector-sends-to-several-mnemarr-and-links-to-one-by-asking-and-being-approved.md), built)

*Demo: a computer that already has the collector is linked to a second Mnemarr from its icon, in a few clicks, without reinstalling: both receive its conversations.*

- The collector sends every conversation to every destination (each with its own key, name and positions); `link` (by asking, or with a code), `unlink`, `destinations`; the icon's **Link to a Mnemarr…** and its menu of Mnemarr; the page that answers a computer that asks, and the list of those that wait. Not now: finding computers by mDNS, per-destination reader settings (2c), macOS and Linux icons.

#### Follow-ups of running it for real

- A **static Linux program** (musl), so that any distribution runs it, and **aarch64** programs for Linux and macOS (the image carries x86_64 only); the **release version** passed into the collector build and shown per machine (a collector older than its server is visible); a **`resend`** command for a collector after a restore of the server.

#### The installer and the updates of the collector ([ADR 0032](adr/0032-the-collector-is-installed-by-an-installer-and-updates-itself-from-the-mnemarr-it-sends-to.md), built for Windows)

- **Built:** a Velopack installer for Windows (per user, no administrator rights, in the list of installed applications with an uninstaller, the program on the PATH and started with the session), one file for every machine with no secret in it, which opens the page that links the computer; each Mnemarr serves the installer and the feed of updates it was built with; the collector asks all the Mnemarr it sends to, takes the newest version over HTTPS (or from this computer), downloads it in the background and applies it between two passes; `update`, the icon, `status`; the page says *Update available: X*; the CI packages it and **installs, updates and uninstalls it on a real Windows runner**.
- **The reinstall of a machine that exists** (built, ADR 0031 §8: *Manage → Reinstall*), for what has no installer.
- **Next, for the installer:** who may update a collector (Q-41: marked destinations or signed packages); the **Linux** self-updating AppImage and **macOS** (the Rust SDK and `vpk` support both: what is left is a systemd user unit and the first run on a system with no icon); **delta packages** (the CI keeps the previous packages of a release so that an update is a few hundred kilobytes); the **icon** of the installer and of the entry in the list of applications; **code signing** (the installer shows Windows' warning at the first run).
- **Smaller follow-ups of the review of the machines vertical.** The heartbeat answers with the machine's current name so that a renamed collector rewrites its own configuration (until then the old name is kept as an alias); the address of the approval page has a setting of its own for a Mnemarr whose collectors reach a host that publishes only the ingest prefix; the program hash is streamed and could use `ring::digest`, already in the build through the TLS library, instead of a hand-written SHA-256 (a direct dependency is the maintainer's decision); the "sending right now" window of ADR 0031 follows the collector's own pass interval.

#### 2c. Configuration from the server

*Demo: switch a reader off for a machine in the interface and see the collector obey at its next pass.*

- The `collectors:` section of the configuration document, the API, the interface, an MCP tool; the collector reads its entry at each pass (`GET /api/v1/ingest/config`); the minimum-version answer; the limits of the ingest served to the collector.

### 3. Run it: the MVP in production

*Demo: the production URL; the machines send their conversations; an assistant elsewhere reads them.*

- Image released through the existing pipeline, **carrying the collector programs** (done: [ADR 0029](adr/0029-mnemarr-runs-for-real-programs-in-the-image-snapshots-of-the-database-and-brakes-on-the-public-endpoints.md)), deployed by GitOps; the data folder on a local disk, backed up.
- Exposure of the ingest and MCP endpoints outside the single sign-on, with Mnemarr's own keys (Q-07).
- The runbook to add a machine, to back up and to restore ([runbook](runbook.md)); a consistent snapshot of the database and a brake on the public endpoints ([ADR 0029](adr/0029-mnemarr-runs-for-real-programs-in-the-image-snapshots-of-the-database-and-brakes-on-the-public-endpoints.md)).
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

- A background job (through the assistant runner, ADR 0007) reads new conversations and writes or updates notes per application, supersedes the contradicted ones, keeps provenance (D-17). The **summary title** of each conversation is built already ([ADR 0028](adr/0028-the-title-of-a-conversation-is-its-own-written-by-a-model-or-typed-by-a-person.md)).
- Needs a decision on what pays for it (a subscription through the CLI, or an API key); it costs per conversation.

### 7. Search by meaning

*Demo: ask with other words than the note's and still find it.*

- Embeddings computed locally, vectors in `sqlite-vec`, hybrid with the keyword search (ADR 0012). **Built** ([ADR 0034](adr/0034-search-by-meaning-is-computed-on-the-host-and-merged-with-the-words-by-rank.md)): the model and the library are in the image, the passages are computed in the background, search merges words and meaning, and the notes page says where it is. Follow-ups: another model (`multilingual-e5-small`), vectors for titles alone, a bigger evaluation on conversations, the health page of F-HLT-01.

## Not now

The reader and editor of notes beyond what exists, more kinds of sources, retention policies, the chat panel, the assistant runner for chat. They come after the MVP, in the order the use shows.
