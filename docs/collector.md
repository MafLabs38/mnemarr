# The collector

The collector is a small program (Rust) that runs as a **service on each machine where an assistant runs** (not where the screen is: a remote session stores its conversations on the remote machine). It reads the conversation files of the assistants, hides secrets, and sends what is new to Mnemarr, which writes each conversation as a note that an assistant allowed to read conversations can find over MCP ([ADR 0024](adr/0024-conversations-are-collected-by-position-masked-twice-and-written-as-notes.md), [ADR 0025](adr/0025-the-collector-is-a-service-per-machine-enrolled-by-a-code-and-configured-from-the-server.md)).

It is **not part of Mnemarr's container**: it is an external helper that talks to Mnemarr only through its API, over outbound HTTPS.

> **What is built, and what is designed.** Built (besides what follows): `install`, `start`, `stop`, `status` and `uninstall` (Windows and Linux; macOS runs it in the background without starting it with the session). Built too: the icon in the notification area (Windows) and the pause. Built: the loop, the reading of Claude Code files, masking, sending by position, the position file, the ingest API, **enrolment with a one-time code, a key bound to the machine, the heartbeat and the page of the machines** ([ADR 0027](adr/0027-machines-join-with-a-code-hold-a-key-bound-to-their-name-and-say-they-are-alive.md)). Designed in ADR 0025 and not built yet: the container form for servers, configuration read from the server, the other readers.

## What it does and does not do

- It sends **only new lines**, by position. Stop it, start it, lose the network: nothing is lost and nothing is sent twice (the conversation file is its queue; `state.json` says how far each file was sent).
- It masks secrets **before** a line leaves the machine (private keys, tokens, `sk-` keys, credentials in addresses, `PASSWORD=…` assignments). The server masks again.
- It has an **ingest-only key**: it can add conversations and cannot read any memory.
- It is **outbound only** (HTTPS to Mnemarr): no inbound port, no firewall rule to open. Which paths and ports are allowed, proxies, and how to try it on a network without TLS: [network.md](network.md).
- It runs **as the user whose files it reads**, never as root or `SYSTEM`.
- It **never prevents the machine from sleeping**, never wakes it, and runs at a low priority. A machine that sleeps or is switched off collects nothing and nothing is wrong: at wake, it resumes where it stopped.
- It leaves out what a sub-agent said, and Mnemarr keeps only what was said (not the calls of tools or their results).

## Two kinds of machine

Each machine has a kind, declared in Mnemarr:

| Kind | For | When it is silent |
|---|---|---|
| `always-on` | servers, virtual machines | after a threshold (30 minutes by default) Mnemarr says so: something is wrong |
| `intermittent` | a workstation that sleeps or is switched off at night | never an alert: Mnemarr shows when it was last seen |

## Join a machine (built, ADR 0027)

1. In Mnemarr, open **Machines** and add the machine: its name (the one it will give itself) and its kind. Mnemarr shows, once, for the system you are on, a **Download** button (Windows) and **one line** to paste, each with a one-time code inside.
2. **Windows, the simplest way**: click **Download**, open the file. It already knows this Mnemarr, the machine and the code: it joins the machine and installs the collector. (Windows may say that the publisher is unknown, because the program is not signed yet: choose *More info*, then *Run anyway*.)
   **From a terminal** (any system, and what an assistant runs), as your usual user (no administrator rights), in PowerShell (Windows) or a terminal (Linux, macOS), paste the line:
   ```text
   irm 'https://mnemarr.homelab.example/api/v1/ingest/install.ps1?code=ABCD-EFGH-JKLM&machine=my-laptop' | iex
   curl -fsSL 'https://mnemarr.homelab.example/api/v1/ingest/install.sh?code=ABCD-EFGH-JKLM&machine=my-laptop' | sh
   ```
   It downloads the collector **from your Mnemarr**, verifies its checksum, joins the machine and starts collecting. Nothing has to be installed first. Under the hood it runs `mnemarr-collector enroll`, which exchanges the code for the key of this machine and keeps it in `~/.config/mnemarr-collector/key` (a file only your user can read; `%APPDATA%` on Windows) with the address and the name in `collector.json` beside it, and then `mnemarr-collector run`. Those two commands are also yours to run by hand when the program is already there.
3. The line **installs and hands the window back**: the collector runs in the background, with no window, and starts with your session. The machine appears on the page as **waiting** until its first pass, then **online**.

To see or control it afterwards (the same program, in the folder it was installed in):

| Command | What it does |
|---|---|
| `mnemarr-collector status` | says whether it runs, whether it starts with your session, and where its log is |
| `mnemarr-collector stop` | stops it now (it starts again with your next session) |
| `mnemarr-collector start` | starts it again in the background |
| `mnemarr-collector pause [--minutes N]` | makes it quiet for N minutes, or until `resume`: it reads and sends nothing, and says so to Mnemarr, which shows the machine as **paused** and not silent |
| `mnemarr-collector resume` | ends the pause |
| `mnemarr-collector uninstall` | stops it and stops it starting with your session; the machine stays joined |

**On Windows it shows an icon in the notification area** (the small arrow near the clock, where hidden icons are kept): a line drawing of a brain that is **green** when all is well, **orange** when paused, **red** when Mnemarr cannot be reached or something is wrong (grey-blue while it starts). Right-click it for the menu: the state in words, *Pause for one hour*, *Pause until I resume*, *Resume*, *Open Mnemarr* and *Quit the collector*. Windows hides new icons behind the arrow: drag it onto the bar to keep it in sight. `--no-tray` removes the icon.

On Windows it is in your own list of programs that start with your session (Task Manager, *Startup apps*), where you can also switch it off; on Linux it is a unit of your own systemd (`systemctl --user status mnemarr-collector`). Its log is `collector.log` in its folder. No administrator rights are needed.

If the address of Mnemarr is plain `http://` (a sandbox), the page says that the key will travel without encryption, and the line makes the collector accept it (`--allow-insecure-http`). Over HTTPS there is nothing of the kind. A Mnemarr that carries no collector program (`MNEMARR__COLLECTORS__DOWNLOADFOLDER`) shows only the command that joins a machine that has the program.

The code works once, for 15 minutes, for that machine name. A machine that enrols again replaces its key (the old one stops working). To remove a machine, **revoke** it on the page: its key stops working at once, and it can join again with a new code.

An assistant can ask for a machine over MCP (`request_collector_enrolment`): the request waits on the page until a person approves it.

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

`run` collects for ever (a pass every 30 seconds, `--interval` to change it); `once` makes one pass and stops. What is not given comes from the environment (`MNEMARR_SERVER`, `MNEMARR_COLLECTOR_KEY`, `MNEMARR_MACHINE`) or from the usual places (`~/.claude/projects` for the conversations, `~/.local/state/mnemarr-collector/state.json` for the position). A key made by hand for a `Collector` account belongs to no machine, and the server refuses it unless `MNEMARR__INGEST__REQUIREENROLMENT` is off (a sandbox).

The first run sends the whole history of the machine: it can take a few minutes for a large one, and is safe to interrupt.

## See that it works

- The **Machines** page (and `GET /api/v1/collectors`) shows each machine: its kind, its state (**online**, **away** when an intermittent machine sleeps, **silent** when an always-on machine has not been heard from, **failing** when a pass reported an error, **refused** when it was revoked, **waiting** before its first pass), when it was last seen, the version of its collector and how many conversations it sent. It tells something about the conversations, so it needs the right to read them (`conversations.read`: `Admin` and `Assistant`).
- `/health/ready` is degraded, with the reason `collector-silent` or `collector-failing`, when an **always-on** machine has been silent for 30 minutes or reports an error. A workstation that sleeps never raises it.
- The notes page has a section of the conversations, one folder per machine; only `Admin` and `Assistant` see it.
- Over MCP, an assistant with the right to read conversations calls `search` (use `topic` to keep one machine) and `read_note`.

## Build it

```bash
cd collector && cargo build --release   # target/release/mnemarr-collector
cargo fmt --check && cargo clippy --all-targets --locked -- -D warnings && cargo test --locked
```
