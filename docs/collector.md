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

## Send to several Mnemarr, and link another (built, ADR 0030)

A collector is not tied to one Mnemarr: it keeps a list of **destinations** and sends every conversation to each of them, each with its own key, its own name
for the machine and its own positions. A development instance and a production one both receive; one that is down does not hold the other back.

| Command | What it does |
|---|---|
| `mnemarr-collector link [--server URL] [--name NAME]` | links this machine to a Mnemarr **by asking**: it shows an address to open (and opens it), a person approves there and names the machine, and the key comes to the collector by itself. `--no-browser` only prints the address |
| `mnemarr-collector link --server URL --code CODE` | the same with a one-time code that a person made in Mnemarr (what the installers do) |
| `mnemarr-collector destinations` | lists the Mnemarr this machine sends to |
| `mnemarr-collector unlink NAME` | stops sending to that Mnemarr (what it received stays there; revoke the machine there to remove it for good) |

On Windows, the icon has **Link to a Mnemarr…**: a page opens on this computer (it is served on the loopback address only, for a few minutes), you type the
address of the Mnemarr, the page of that Mnemarr opens in the browser where you approve this computer and choose its name, and the local page tells you when
it is done. **Linking a computer that already has a collector adds the Mnemarr; it does not replace the others**, whichever way you do it (the icon, `link`,
the line of the page, or the downloaded program).

A collector of an earlier version is read as a list of one destination, with the positions it had.

## A machine is a computer, not a name (built, ADR 0031)

The collector sends, when it joins a Mnemarr, an identifier of the computer: a SHA-256 hash of the identifier that the system made for itself at its own
installation (`MachineGuid` on Windows, `/etc/machine-id` on Linux, the platform UUID on macOS), never the identifier itself. It survives removing the
collector and installing it again. A Mnemarr that knows the computer keeps it as **the machine it already is**, whatever name you give: it takes the new
name, what it sent is filed under it, and nothing is sent twice. The collector also sends it with each sign of life, so that a machine that joined before identifiers existed learns its own. A system that gives no identifier sends none, and the name is the identity, as before.
Two computers cloned from one image share an identifier, so the second to join takes the first one's machine: remove the one that is not wanted, and name them apart.

## Join a machine (built, ADR 0027)

1. In Mnemarr, open **Machines** and add the machine: its name (the one it will give itself) and its kind. Mnemarr shows, once, for the system you are on, a **Download** button (Windows) and **one line** to paste, each with a one-time code inside.
2. **Windows, the simplest way**: click **Download**, open the file. It already knows this Mnemarr, the machine and the code: it joins the machine and installs the collector. (Windows may say that the publisher is unknown, because the program is not signed yet: choose *More info*, then *Run anyway*.)
   **From a terminal** (any system, and what an assistant runs), as your usual user (no administrator rights), in PowerShell (Windows) or a terminal (Linux, macOS), paste the line:
   ```text
   irm 'https://mnemarr.homelab.example/api/v1/ingest/install.ps1?code=ABCD-EFGH-JKLM&machine=my-laptop' | iex
   curl -fsSL 'https://mnemarr.homelab.example/api/v1/ingest/install.sh?code=ABCD-EFGH-JKLM&machine=my-laptop' | sh
   ```
   It downloads the collector **from your Mnemarr**, verifies its checksum, joins the machine and starts collecting. Nothing has to be installed first. Under the hood it runs `mnemarr-collector enroll`, which exchanges the code for the key of this machine and keeps it in `key-<name>` in the folder of the collector (`~/.config/mnemarr-collector/`; `%APPDATA%` on Windows; a file only your user can read), with the Mnemarr it belongs to listed in `destinations.json` beside it (it is **added** to the Mnemarr the machine already sends to), and then `mnemarr-collector install`. Those two commands are also yours to run by hand when the program is already there.
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

The Linux program that the image serves is built on Ubuntu 24.04 and links the system's C library (glibc 2.39): a Linux older than that (Debian 12, Ubuntu 22.04, RHEL 9) may refuse to start it with a `GLIBC_… not found` error. Build it from the sources there (`cargo build --release` in `collector/`); a fully static program is on the roadmap.

**On Windows it shows an icon in the notification area** (the small arrow near the clock, where hidden icons are kept): a line drawing of a brain that is **green** when all is well, **orange** when paused, **red** when Mnemarr cannot be reached or something is wrong (grey-blue while it starts). Right-click it for the menu: the state in words, *Pause for one hour*, *Pause until I resume*, *Resume*, *Open Mnemarr* and *Quit the collector*. Windows hides new icons behind the arrow: drag it onto the bar to keep it in sight. `--no-tray` removes the icon.

On Windows it is in your own list of programs that start with your session (Task Manager, *Startup apps*), where you can also switch it off; on Linux it is a unit of your own systemd (`systemctl --user status mnemarr-collector`). Its log is `collector.log` in its folder. No administrator rights are needed.

If the address of Mnemarr is plain `http://` (a sandbox), the page says that the key will travel without encryption, and the line makes the collector accept it (`--allow-insecure-http`). Over HTTPS there is nothing of the kind. A Mnemarr that carries no collector program (`MNEMARR__COLLECTORS__DOWNLOADFOLDER`) shows only the command that joins a machine that has the program.

A computer that already has the collector does not need this page: run `mnemarr-collector link --server https://mnemarr.homelab.example`, or use the icon (see above). It then appears in **Computers asking to be linked**, with a link to approve it.

The code works once, for 15 minutes, for that machine name. A machine that enrols again in the same Mnemarr replaces its key there (the old one stops working); the other Mnemarr it sends to are not touched. To remove a machine, **revoke** it on the page: its key stops working at once, and it can join again with a new code.

An assistant can ask for a machine over MCP (`request_collector_enrolment`): the request waits on the page until a person approves it.

## Install it on Windows, and keep it up to date (built, ADR 0032)

The usual way on a workstation is the **installer**. On the Machines page, *Add a machine* offers **Download the installer for Windows**; it is the same file for every machine and holds no code.

1. Open it. Windows shows its warning once (the installer is not signed yet): *More info*, then *Run anyway*. It installs for you alone, without administrator rights, in `%LocalAppData%\MnemarrCollector`.
2. The collector starts, with its icon near the clock, and **opens a page in your browser** where you give the address of your Mnemarr. The page shows a code and opens the page of Mnemarr, where you check that the code is the one on the computer, name the machine and approve it ([ADR 0030](adr/0030-a-collector-sends-to-several-mnemarr-and-links-to-one-by-asking-and-being-approved.md)). Nothing to copy by hand.
3. It is now **in Windows' list of installed applications** (*Settings, Apps*), starts with your session, and `mnemarr-collector` is found by name in a new terminal.

**Updating.** The collector asks each Mnemarr it sends to, ninety seconds after it starts and then every four hours, for the newest version it carries, and takes **the newest of them** (a development and a production Mnemarr carry different builds; a build that is not a release is older than every release). It downloads it in the background and, by default, **applies it between two passes** and restarts: nothing is lost, its positions are kept after each pass. It takes an update only over HTTPS, or from this very computer, never over a plain `http://` address on a network.

- *Check now, or apply a downloaded update*: the icon (**Check for updates**, then **Update to X: restart now**), or `mnemarr-collector update` (`--check` only looks).
- *Apply by hand only*: `mnemarr-collector update --auto off` (and `--auto on` to go back). The update is then downloaded and waits.
- `mnemarr-collector status` says the version and where the update is at. The machines page says **Update available: X** for a collector installed this way that is older than the Mnemarr's.
- A collector that was **not** installed with the installer (run by hand, from a build, from a downloaded program that carries a code) is not touched: `update` says so.

**Removing it.** *Settings, Apps, Mnemarr collector, Uninstall*. It stops, no longer starts with your session and leaves the PATH. **The machine stays joined**: its keys and positions are in `%AppData%\mnemarr-collector`, which is not removed, so installing again finds them. To remove them as well, revoke the machine in Mnemarr and delete that folder.

## Install it another way (ADR 0025, ADR 0027)

The installer above is for Windows. **On Linux and macOS**, and on a machine with no screen, the line the page shows downloads the program and installs it as a user service; on a machine with no screen the download that carries a code joins it by itself. Linux and macOS installers that update themselves are in the [roadmap](ROADMAP.md).

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
