# 0032 — The collector is installed by an installer and updates itself from the Mnemarr it sends to

- **Status**: accepted
- **Date**: 2026-11-08
- **Spec**: D-52; answers Q-40; realises ADR 0031 §6 and §7 ([0031](0031-a-machine-is-a-computer-not-a-name-it-is-deleted-in-two-ways-and-installed-once-for-everyone.md))

## Context

The collector was a program that a person downloaded with a code inside it, or installed with a line pasted in a terminal. It was not in the list of installed
applications, could not be removed from there, was not on the PATH, had no way to learn that a newer version existed, and a new version meant downloading
again for each machine. The maintainer already ships his other Windows applications with an installer that updates them by themselves
([Velopack](https://velopack.io/)), and asked for the same here, with the update taken **from the Mnemarr the collector connects to**.

## Decision

1. **The installer is Velopack's** (`vpk`, and the `velopack` crate in the collector, which the maintainer's answer makes a new dependency). It installs
   **per user, without administrator rights**, in `%LocalAppData%\MnemarrCollector` (a folder that is not the one of the programs of ADR 0027, so that both
   can exist), registers the program in the list of installed applications with an uninstaller, and keeps a **program at the root of its folder that
   an update does not change** (the real one is in `current`, which is replaced). Windows first; the Linux self-updating AppImage and macOS follow.
2. **One installer for everybody, with no secret in it.** It is not made per machine. When the collector first runs and is linked to nothing, it keeps
   running with its icon and **opens the page where a person links it** (ADR 0030: the machine asks, a person approves in Mnemarr and names it). The program
   that carries a code (ADR 0027 §15) stays for a machine with no screen.
3. **What the installer asks of the program** (Velopack calls it with a task, thirty seconds at most, no window): after the install, the entry that starts it
   with the session of the user points at the stable program and its folder is added to the PATH of the user; before the uninstall it stops, and the entry
   and the PATH are taken away; before an update it stops, and the updater starts the new version. **The machine stays joined** after an uninstall: its keys
   and positions are in the folder of the collector, which is not the one that is removed (a hook cannot ask a question, so the choice to delete them is a
   command, in the runbook), and installing again finds them.
4. **Each Mnemarr serves the updates it was built with**, from the folder the installer made: Velopack's feed (`releases.win.json`) and its packages, under
   `/api/v1/ingest/updates/`, and the installer at `/api/v1/ingest/installer/windows`. Anonymous like the programs (the same for everybody, no secret), limited
   like them (four at a time), and **only the files the installer makes are served** (by name, no folder, no link followed). The image carries the folder;
   the CI makes it.
5. **The collector asks all the Mnemarr it sends to, and takes the newest version** (the update source is not one chosen Mnemarr: a development and a
   production Mnemarr carry different builds, and a version number says which is newer). It first asks ninety seconds after it starts, then every four hours,
   apart from the passes; downloads in the background; and, by default, **applies the update between two passes** (positions are kept after each pass, so
   nothing is lost when it restarts). The person can choose to apply by hand (`update --auto off`), from the icon, or with `mnemarr-collector update`.
6. **An update is code that will run**: it is taken only over **HTTPS, or from this very computer**, never over a plain `http://` address on a network, even
   one the person allowed for sending conversations. The trust is the one in the Mnemarr that the person linked, over TLS. The package is checked against the
   checksum of the feed. The program is **not signed** (Windows shows its warning at the first run; signing is for later and changes nothing here).
7. **Versions are semantic versions**: a release is `X.Y.Z`; any other build is `0.0.1-<branch>.<run>` (Velopack refuses anything below 0.0.1), older than
   every release. The collector reports the version the installer knows, and whether it can replace itself; the machines page says **Update available: X**
   for a machine that can and is older, and keeps "another program than this Mnemarr carries" (the hash of ADR 0031 §7) for the others.
8. **The CI makes and tries it.** The job `installer` packages the Windows program that the `collector` job built and tested, at the version of the build,
   in a job of its own that cannot write anywhere (the tool is fetched at a pinned version); the job `installer (windows)` **installs it on a real Windows
   runner without a window, updates it from a feed served on that computer, and uninstalls it**, checking the entry, the PATH, the version and what is left.

## Not here

The Linux AppImage and macOS, delta packages (they need the previous packages), the icon of the installer, signing, the heartbeat answering with the current
name of a renamed machine, and a choice of updating only some machines from the page. They are in the [roadmap](../ROADMAP.md).

## Consequences

- A person downloads one file, double-clicks it, approves the computer in Mnemarr, and has a collector that is in the list of applications, starts with the
  session, answers by name in a terminal, and updates itself. Removing it is the system's *Uninstall*.
- The size of the image grows by the installer and the package (about sixteen megabytes). Releasing a Mnemarr releases the collector it carries.
- A collector that sends to several Mnemarr is updated by the one that carries the newest build: an old Mnemarr never holds it back, and a development build
  never reaches a machine that also sends to a release.
- The programs of ADR 0027 and their lines stay for what has no installer yet (Linux, macOS) and for a machine with no screen.
