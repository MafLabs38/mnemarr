# Runbook: running Mnemarr

What an operator does once Mnemarr runs for real. The examples use `mnemarr.example` and `server-1`; use yours. How it is deployed (a Compose file, a cluster) is yours: see [releasing](releasing.md) for the versions and [the network page](network.md) for what to publish.

## Before anything: what must be true

- The data folder (`/config` in the image) is on a **local disk**, not a network share, and it is copied away by your backup tool (see [Back up](#back-up)).
- The interface is behind your single sign-on or your own network; only the two prefixes of [the network page](network.md) are published without it, with a per-address limit at the proxy.
- `MNEMARR__AUTH__COOKIESECURITY` is `Always` when a proxy ends the TLS.
- The first administrator exists (`MNEMARR__AUTH__ADMINUSER` and a password in a file). Its name is not obvious: whoever knows it can lock that account for fifteen minutes.

## Look at it

- **Is it up?** `GET /health/ready` answers 200 and a status; the page *System > Status* says the version and what is degraded (a source that failed to sync, an always-on machine that went silent).
- **Which version?** *System > Status* shows `X.Y.Z.<build>`. The image tag is the version.
- **What happened?** The audit log, `GET /api/v1/audit` (an administrator), lists who did what (adding a machine, approving, revoking, a failed code), kept for `Audit:RetentionDays`.

## Add a machine

1. In the interface: **Machines > Add a machine**, give it a name (letters, digits, dots, hyphens; 50 characters at most) and say whether it is always on or comes and goes.
2. **On Windows, download the installer** it offers and open it (the same file for every machine, no code in it): the collector asks to be linked, and you approve it in **Computers asking to be linked**. Or run the one line it shows in a terminal of that machine, **as the user whose conversations it will read**, or download the program that carries a code and open it. The code works for 15 minutes and once.
3. The machine shows **online** within a minute. If it does not: [the collector page](collector.md) lists what it says when it cannot join.
4. An assistant may ask for the code over MCP (`request_collector_enrolment`); it waits for you to approve it on the same page.
5. **A computer that already has the collector** needs none of this to be linked to this Mnemarr: on that computer, `mnemarr-collector link --server <address of this Mnemarr>` (or **Link to a Mnemarr…** on the icon, on Windows). It shows an address and a code; you open it signed in, check the name the computer gives itself, **choose the name it will have in this Mnemarr** and approve. The computer then sends here as well as to the Mnemarr it already sent to. A computer that asked and is not approved waits in **Computers asking to be linked** for 10 minutes.

The page offers a line per system **that this image carries a program for** (Windows and Linux, x86_64). A system with no program is not offered.

## Update the collectors

A collector installed with the installer **updates itself** from the Mnemarr it sends to (see [the collector](collector.md)). Updating Mnemarr updates what it carries: the new image has the installer and the packages of the version it was built with, and collectors that send to it take the newer version within four hours. The machines page says **Update available: X** for those that are older. Nothing is to be done on the machines. To see what a Mnemarr carries: `GET /api/v1/ingest/updates/releases.win.json`.

The folder it serves is `updates` in the folder of the programs (`MNEMARR__COLLECTORS__DOWNLOADFOLDER`, `/collector-programs` in the image), or the folder
`MNEMARR__COLLECTORS__UPDATEFOLDER` when it is somewhere else. It holds what the installer makes for the build: `releases.win.json` (the feed), the packages
`MnemarrCollector-<version>-full.nupkg` and the installer `MnemarrCollector-win-Setup.exe`. The image carries it; a Mnemarr built without it serves no installer and the
page offers none. A machine that sends over plain `http://` is told on its row why it cannot update (an update is code, so it is only taken over HTTPS).

If an update does not happen: `mnemarr-collector status` on the machine says where it is at (`updates:`), and `mnemarr-collector update` asks now and says why it cannot (not installed with the installer, no Mnemarr reachable over HTTPS, nothing newer).

## The search by meaning

Mnemarr also searches by meaning, with a model that runs on this host (the image carries it; [configuration](configuration.md)). Nothing leaves the host.

- **Is it working?** The notes page says nothing when it is up to date, *The search by meaning is catching up: X of Y passages are read* while it reads, and why when it does not work. `GET /api/v1/search/status` says the same (`off`, `ready` or `failed`).
- **The first start over a long history is slow** (about ten passages a second on two threads): the search works by words meanwhile, and by meaning for the passages that are read. It resumes after a restart; it does not start over.
- **`degraded` with `embeddings-failing`**: the notes page says in a sentence what, and the log of Mnemarr has the detail: the model files do not match what is pinned (an image that was altered, a folder that was swapped), the library cannot be loaded, or a pass failed. The search goes on by words. A model or a library that could not be loaded is tried again every five minutes: no restart is needed once the cause is fixed.
- **`starting`**: the model is being loaded (a few seconds); until then the answer is by words.
- **To change the model or its settings**, set the variables of the configuration; a change of model reads every passage again in the background.
- It uses about 1 GB of memory and two CPU threads while it reads. Lower `MNEMARR__EMBEDDINGS__THREADS` on a small host, or leave `MNEMARR__EMBEDDINGS__MODELFOLDER` empty to turn it off.

## Take a machine away, or change its key

**Uninstall the collector from a computer**: *Settings, Apps, Mnemarr collector, Uninstall* (it keeps its keys and positions, so that installing again finds them). To forget everything on that computer, also delete the folder `%APPDATA%\mnemarr-collector` and, in Mnemarr, revoke the machine below.

- **Revoke** it on the Machines page: its key stops working at once and the row stays, as *refused*, so that what it wrote can be found. **Remove from the list** frees its name.
- **A lost or leaked key**: revoke the machine, then add it again with the same name. The new key replaces the old one.
- **A key of an assistant** (for `/mcp`): create a service account with the role you want under *Accounts*, give its key once, and revoke it there when it is no longer used.

On the machines page, **Manage** on a row offers (ADR 0031):

- **Rename**: the machine, or a name that conversations came from, gets a new name; what it sent is filed under it. The name must be free.
- **Merge into another machine**: what a name sent is filed under a machine that is enrolled, and the name leaves the list. A conversation that the target
  holds already is not made twice. This is what repairs a computer that was installed again under another name before Mnemarr knew computers.
- **Reinstall**: a new code for a machine that exists, without revoking it, to install again or to update the collector on that computer.
- **Remove, keep its conversations**: its keys stop working and it leaves the list; its conversations stay, under its name, where they can be renamed, merged or deleted.
- **Delete with its conversations**: the machine and everything it sent leave the notes and the search. It cannot be undone: the daily snapshots of the database
  still hold a copy until they are rotated out, and what is computed from conversations (consolidated notes) is not deleted.

## Back up

Mnemarr writes a consistent snapshot of its database every 24 hours into the `backup` folder of the data folder and keeps the last seven (`Backup:*`, [configuration](configuration.md)); the health check says so when it stops. **Copy the whole data folder away with your own tool**: the snapshots, the raw conversations (`raw/`), the notes (`conversations/` and the sources you added) and the keys of the sessions (`keys/`) are all in it. A copy of the live `mnemarr.db` while the application runs is not always whole: restore from a snapshot.

Check from time to time that a restore works (below) on a spare folder, not on the live one.

## Restore

1. **Stop** Mnemarr (a second instance on the same files would corrupt the database).
2. Put the data folder back from your backup. In `backup/`, pick the snapshot you want (the newest is the last of the list: `mnemarr-YYYYMMDD-HHMMSS.db`, in UTC).
3. Replace the database by it, and remove what belongs to the old file:
   ```sh
   cp backup/mnemarr-20261106-100000.db mnemarr.db
   rm -f mnemarr.db-wal mnemarr.db-shm
   chmod 600 mnemarr.db
   ```
4. **Redo what you did after the snapshot.** A restore brings back the database as it was: a key that was revoked since is valid again, a password that was changed is the old one, and a machine that was removed is back. Revoke again what you revoked (keep a note of it when you do), and change again what you changed, before you start Mnemarr to the Internet again.
5. Start Mnemarr. A conversation that **goes on** after the restore is caught up: the server tells the collector where to resume and it sends again from there. A conversation that **ended after the snapshot** is not: the collector remembers having sent it and does not look at it again. To get everything back, on each machine stop the collector, delete its `state.json` (the folder `mnemarr-collector` under `$XDG_STATE_HOME`, `~/.local/state`, or `%LOCALAPPDATA%`), and start it: it sends every conversation again, and the server skips what it already has.

## Update

A deployment that follows the version tags (see [releasing](releasing.md)) picks a release up by itself. With Compose, change the tag and `docker compose up -d`. The database is migrated at start, and a migration is not undone: Mnemarr keeps a whole copy of the database before it migrates it (`backup/before-migration-<moment>-vN-to-vM.db`, the last three), and does not start if it cannot write it. **Check that your tool copies `backup/`**, and keep the copy for a while after an update. To go back to the previous version, restore that copy with the previous image (see Restore).

## When something is wrong

| You see | Look at |
|---|---|
| A machine **silent** or **failing** | the Machines page says why; [the collector page](collector.md) |
| *System > Status* **degraded** with `collector-silent` | an always-on machine has not reported for 30 minutes: is it on, can it reach Mnemarr? |
| 429 on the installer or a download | too many programs are being written at once, or the proxy's per-address limit: wait a few seconds |
| 429 on `enrol` | ten wrong codes in a minute for the whole server: wait a minute |
| No line to run on the page | this image carries no program for that system |
| *System > Status* **degraded** with `backup-failing` or `backup-late`, or the error `A snapshot of the database could not be taken` in the log | the log line says why: usually the data folder is full or read-only, or a file is where the `backup` folder should be, or the folder cannot be read. Fix it; it is tried again an hour later and the status goes back to healthy |
