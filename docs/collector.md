# The collector

The collector is a small program (Rust) that runs as a **service on each machine where an assistant runs** (not where the screen is: a remote session stores its conversations on the remote machine). It reads the conversation files of the assistants, hides secrets, and sends what is new to Mnemarr, which writes each conversation as a note that an assistant allowed to read conversations can find over MCP ([ADR 0024](adr/0024-conversations-are-collected-by-position-masked-twice-and-written-as-notes.md), [ADR 0025](adr/0025-the-collector-is-a-service-per-machine-enrolled-by-a-code-and-configured-from-the-server.md)).

It is **not part of Mnemarr's container**: it is an external helper that talks to Mnemarr only through its API, over outbound HTTPS.

> **What is built, and what is designed.** Built: the loop, the reading of Claude Code files, masking, sending by position, the position file, the ingest API (this page, "What it does", "Run it by hand"). Designed in ADR 0025 and not built yet: enrolment, the installer and the service forms, configuration read from the server, the heartbeat, the other readers.

## What it does and does not do

- It sends **only new lines**, by position. Stop it, start it, lose the network: nothing is lost and nothing is sent twice (the conversation file is its queue; `state.json` says how far each file was sent).
- It masks secrets **before** a line leaves the machine (private keys, tokens, `sk-` keys, credentials in addresses, `PASSWORD=…` assignments). The server masks again.
- It has an **ingest-only key**: it can add conversations and cannot read any memory.
- It is **outbound only** (HTTPS to Mnemarr): no inbound port.
- It runs **as the user whose files it reads**, never as root or `SYSTEM`.
- It **never prevents the machine from sleeping**, never wakes it, and runs at a low priority. A machine that sleeps or is switched off collects nothing and nothing is wrong: at wake, it resumes where it stopped.
- It leaves out what a sub-agent said, and Mnemarr keeps only what was said (not the calls of tools or their results).

## Two kinds of machine

Each machine has a kind, declared in Mnemarr:

| Kind | For | When it is silent |
|---|---|---|
| `always-on` | servers, virtual machines | after a threshold (30 minutes by default) Mnemarr says so: something is wrong |
| `intermittent` | a workstation that sleeps or is switched off at night | never an alert: Mnemarr shows when it was last seen |

## Install it (designed, ADR 0025)

**On a server or a virtual machine**: the container, from a Compose file that mounts the folders of conversations read-only.

**On a workstation**, two routes; neither copies a key by hand:

1. *From a release.* Download the binary of the GitHub release for your system, check its checksum, then:
   ```text
   mnemarr-collector enroll --server https://mnemarr.homelab.example --code <one-time code>
   mnemarr-collector install
   ```
   `install` makes it a per-user service (a scheduled task at logon on Windows, a systemd user unit on Linux), with no administrator rights. `status` says whether it runs, and `uninstall` removes it.
2. *Guided over MCP.* From the assistant on that machine (in VS Code, for example), ask Mnemarr for an installation of the collector: it opens an **enrolment request** and answers with the commands and a one-time code. The code works only once a person who may manage collectors has approved the request in Mnemarr. Mnemarr runs nothing on the machine: you run the commands, with the usual approval of your assistant.

The one-time code is valid for 15 minutes, once, for one machine name. It is exchanged for an ingest key that is written to a file only your user can read. To remove a machine, revoke its key.

## Configure it (designed, ADR 0025)

The server holds what each machine collects: a `collectors:` section of the configuration document, the API, the interface, an MCP tool. The collector reads its entry at each pass, so a change applies without touching the machine. It chooses among the readers the collector knows ([the formats](conversation-formats.md)); the server **never names a path**. On the machine only a small bootstrap file remains: the address of Mnemarr, the key file, the name of the machine and, when the usual place is not the right one, the folder of a reader.

## Run it by hand (built)

```bash
mnemarr-collector run \
  --server https://mnemarr.homelab.example \
  --key-file ~/.config/mnemarr-collector.key \
  --machine my-laptop
```

`run` collects for ever (a pass every 30 seconds, `--interval` to change it); `once` makes one pass and stops. What is not given comes from the environment (`MNEMARR_SERVER`, `MNEMARR_COLLECTOR_KEY`, `MNEMARR_MACHINE`) or from the usual places (`~/.claude/projects` for the conversations, `~/.local/state/mnemarr-collector/state.json` for the position). To give a machine its key by hand, create a service account with the role `Collector` (`POST /api/v1/accounts`) and a key for it (`POST /api/v1/accounts/{id}/keys`); the key is shown once.

The first run sends the whole history of the machine: it can take a few minutes for a large one, and is safe to interrupt.

## See that it works

- `GET /api/v1/collectors` shows which machines sent conversations and when (the name of a machine, how many conversations and lines, the last time it sent). It tells something about the conversations, so it needs the right to read them (`conversations.read`: `Admin` and `Assistant`). The health page of vertical 2 will show the silence of a machine as a health check, not through this resource.
- The notes page has a section of the conversations, one folder per machine; only `Admin` and `Assistant` see it.
- Over MCP, an assistant with the right to read conversations calls `search` (use `topic` to keep one machine) and `read_note`.

## Build it

```bash
cd collector && cargo build --release   # target/release/mnemarr-collector
cargo fmt --check && cargo clippy --all-targets --locked -- -D warnings && cargo test --locked
```
