# The collector

The collector is a small program that runs on **each machine where an assistant runs** (not where the screen is: a remote session stores its conversations on the remote machine). It reads the conversation files of Claude Code, hides secrets, and sends what is new to Mnemarr. Mnemarr writes each conversation as a note that any assistant can find over MCP ([ADR 0024](adr/0024-conversations-are-collected-by-position-masked-twice-and-written-as-notes.md)).

## What it does and does not do

- It sends **only new lines**, by position. Stop it, start it, lose the network: nothing is lost and nothing is sent twice (the file it reads is its queue; `state.json` says how far each file was sent).
- It masks secrets **before** a line leaves the machine (private keys, tokens, `sk-` keys, credentials in addresses, `PASSWORD=…` assignments). The server masks again.
- It has an **ingest-only key**: it can add conversations and cannot read any memory.
- It is **outbound only** (HTTPS to Mnemarr): no inbound port.
- It leaves out what a sub-agent said, and Mnemarr keeps only what was said (not the calls of tools or their results).

## Give a machine its key

In Mnemarr, create a **service account** with the role `Collector` (Accounts page, or `POST /api/v1/accounts` with `{"name":"collector-pc","kind":"Service","role":"Collector"}`), then a key for it (`POST /api/v1/accounts/{id}/keys`). The key is shown once: write it to a file on the machine, readable by its user only.

```bash
install -m 600 /dev/null ~/.config/mnemarr-collector.key
printf '%s' 'the-key-shown-once' > ~/.config/mnemarr-collector.key
```

## Run it

```bash
mnemarr-collector run \
  --server https://mnemarr.homelab.example \
  --key-file ~/.config/mnemarr-collector.key \
  --machine my-laptop
```

`run` collects for ever (a pass every 30 seconds, `--interval` to change it); `once` makes one pass and stops. What is not given comes from the environment (`MNEMARR_SERVER`, `MNEMARR_COLLECTOR_KEY`, `MNEMARR_MACHINE`) or from the usual places (`~/.claude/projects` for the conversations, `~/.local/state/mnemarr-collector/state.json` for the position).

The first run sends the whole history of the machine: it can take a few minutes for a large one, and is safe to interrupt.

## See that it works

- `GET /api/v1/ingest/machines` (or the Conversations section of the Sources page) shows which machines sent conversations and when.
- The notes page has a **Conversations** section, one note per conversation, by application.
- Over MCP, an assistant calls `search` (use `topic` to keep one application) and `read_note`.

## Build it

```bash
cd collector && cargo build --release   # target/release/mnemarr-collector
cargo fmt --check && cargo clippy --all-targets --locked -- -D warnings && cargo test --locked
```

Not built yet: the one-command install, service files for Linux and Windows, a heartbeat so that a silent agent can be told from an idle one, other assistants than Claude Code.
