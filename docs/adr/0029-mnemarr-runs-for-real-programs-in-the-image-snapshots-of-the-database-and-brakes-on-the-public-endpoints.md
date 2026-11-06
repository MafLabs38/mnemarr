# 0029 — Mnemarr runs for real: the programs in the image, snapshots of the database, brakes on the public endpoints

- **Status**: accepted
- **Date**: 2026-11-06
- **Spec**: D-49; builds on D-27 and D-28 ([0010](0010-external-exposure-and-delegated-backups.md)), D-33 ([0013](0013-releases-with-semantic-release-consumed-by-pulling.md)) and D-47 ([0027](0027-machines-join-with-a-code-hold-a-key-bound-to-their-name-and-say-they-are-alive.md))

## Context

Until now Mnemarr ran in a developer's sandbox: the installer and the **Download** button worked only when somebody copied the programs of the collector by hand, the endpoints a machine uses were not meant for the Internet, and nothing kept a whole copy of the database. The roadmap calls this "run it for real", and it is the first time the chain (an assistant talks, a collector gathers, Mnemarr keeps, another assistant asks) runs on machines whose conversations matter. Three things stand between the sandbox and that.

## Decision

1. **The image carries the programs a machine installs.** The CI job that builds the image takes the programs that the `collector` job built and tested **for the same commit** (Windows and Linux, x86_64), verifies their checksum, and puts them in `/collector-programs` under the names the server serves. `Collectors:DownloadFolder` points there by default. A build without them (`docker build` on a developer's machine) serves none, and the page offers no line to run. The macOS program is not built yet: the page offers what exists.
2. **Mnemarr keeps a consistent copy of its database.** At start if one is due, then every hour it looks whether the newest snapshot is older than `Backup:EveryHours` (24), and takes one with SQLite's `VACUUM INTO` while it keeps working: a whole, compact database from one read transaction, written under a `.partial` name and renamed when it is complete, readable by its owner only, in the `backup` folder of the data folder. The newest `Backup:Keep` (7) are kept. A copy of the live file with its write-ahead log is not always whole; this one is. **What copies the data folder away stays the operator's** (D-28): Mnemarr does not know where a backup goes.
3. **The brakes belong where the real address of a caller is known.** The proxy, which sees the address of a caller, limits the requests per address (the rule is written in [the network page](../network.md)). Mnemarr does not read forwarded addresses itself: behind two proxies it would trust a header that anyone can write. What Mnemarr limits is what costs something and anyone may ask for: **writing a program, at most four at the same time**, the others are refused at once with a 429 and a `Retry-After`. The wrong enrolment codes are already counted for the whole server (0027 §4), and the sign-in has its own throttle.
4. **The deployment is infrastructure as code in the operator's own private repository**, followed by version tags (0013). No value of anybody's infrastructure is in this repository: not a host name, not an address, not a secret. The runbook says what an operator does, with examples.
5. **The assistant that writes titles is not part of this.** The titles are off by default (0028); the companion that runs an agent CLI is a deployment of its own and comes after.

## Consequences

- The first release is the first image that can install a collector by itself. A release carries the programs of its own commit, so a program and its server are always of the same version.
- A snapshot costs the disk of a compact copy of the database times `Keep`, and a few seconds of reading once a day (a read transaction: the writes go on). The raw conversations and the notes are files in the data folder: the backup tool copies them as they are.
- Restoring is replacing the database file by a snapshot, with the application stopped ([the runbook](../runbook.md)); the raw conversations and the notes are restored with the folder.
- A flood from one address is stopped by the proxy, not by Mnemarr: an operator who publishes Mnemarr without that rule has only the global limits. The page says so.
- A snapshot that cannot be taken is in the log (an error) and tried again an hour later; it does not degrade the health check yet, because a readiness failure would take the application out of service for a backup problem.
