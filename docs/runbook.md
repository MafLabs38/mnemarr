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
2. Run the one line it shows in a terminal of that machine, **as the user whose conversations it will read**, or download the program it offers for Windows and open it. The code works for 15 minutes and once.
3. The machine shows **online** within a minute. If it does not: [the collector page](collector.md) lists what it says when it cannot join.
4. An assistant may ask for the code over MCP (`request_collector_enrolment`); it waits for you to approve it on the same page.

The page offers a line per system **that this image carries a program for** (Windows and Linux, x86_64). A system with no program is not offered.

## Take a machine away, or change its key

- **Revoke** it on the Machines page: its key stops working at once and the row stays, as *refused*, so that what it wrote can be found. **Remove from the list** frees its name.
- **A lost or leaked key**: revoke the machine, then add it again with the same name. The new key replaces the old one.
- **A key of an assistant** (for `/mcp`): create a service account with the role you want under *Accounts*, give its key once, and revoke it there when it is no longer used.

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
