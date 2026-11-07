# 0031 — A machine is a computer, not a name; it is deleted in two ways; the collector is installed once, for everyone

- **Status**: accepted (the tool that builds the Windows installer is Q-40)
- **Date**: 2026-11-07
- **Spec**: D-51; refines D-47 ([0027](0027-machines-join-with-a-code-hold-a-key-bound-to-their-name-and-say-they-are-alive.md)) and D-50 ([0030](0030-a-collector-sends-to-several-mnemarr-and-links-to-one-by-asking-and-being-approved.md))

## Context

Trying the collector on a real computer showed four defects that have one root: **the identity of a machine is its name**.

- Installing again under another name (a typo, then the right spelling) made a second machine, a second folder, and every conversation twice: the positions
  of a collector belong to a name, so it sent everything again.
- A machine that was revoked and removed left its conversations behind, shown as a row with no action, which a person can neither delete nor rename.
- The program that is downloaded carries a one-time code for one name. It cannot be run again, so that "install again" and "update" mean "make a new
  machine and download again".
- The collector is installed from a console, does not appear in the list of installed applications, cannot be uninstalled from there and cannot update.

## Decision

1. **A machine is a computer.** The collector computes a stable identifier of the computer: the SHA-256 of a fixed text and the identifier the system made at
   its own installation (`MachineGuid` on Windows, `/etc/machine-id` on Linux, the hardware UUID on macOS), sent as that hash, never in clear. It survives
   removing and installing the collector again, which is the case that counts: a collector removed for months and put back is the same machine. A MAC address
   is not used (several, changed by a VPN, a container or the system).
2. **The name is a label.** Joining (by a code or by asking) sends the identifier. A Mnemarr that knows it does not make a second machine: the computer joins
   **the machine it already is**, gets a new key for it (the old one stops working), and the person who approves sees "this computer is already
   `gaming-pc`" and may rename it. Nothing is sent twice, the folder is the same. Renaming a machine renames its folder.
3. **Cloned computers are two machines when the system tells them apart**, and are reported (not merged in silence) when two different collectors claim one
   identifier at once. This case is not solved further: a person with cloned virtual machines names them.
4. **A conversation is stored once.** A conversation held under a name that is not a machine (a computer that was installed again under another name, or
   a machine that was removed) is taken over by the machine that sends it now: it goes on from the lines it has, and its note and raw lines are filed under
   the new machine. One held by a machine that exists is left to it. This is the net under rule 2 for what was already duplicated; *Merge* is what a person
   uses for the rest, and it drops the copy of a conversation that the target holds already.
5. **Two ways to remove a machine**, both asking to confirm:
   - **Remove the machine**: its keys stop working and it leaves the list; its conversations stay, filed under its name, shown as "no longer enrolled", **with
     actions** (rename, merge into another machine, delete).
   - **Delete the machine and its conversations**: also deletes its raw lines and its conversation notes, so that they leave the notes and the search. The
     confirmation says that it cannot be undone, and says what survives it (the snapshots of the database, until their retention ends; what the history of
     the notes keeps), as far as that is known.
   **What is computed from conversations is never deleted** (the consolidated notes of vertical 6, summaries, titles of other notes): it is not raw matter and
   cannot be recomputed once the conversations are gone. Their provenance may point to a conversation that no longer exists, and says so.
6. **One installer per system, with no secret in it**, the same for everyone and for every machine. It installs the collector like an application of the
   user (no administrator rights): in the list of installed applications, with a real *Uninstall* (stops it, removes the program and its start-up, asks
   whether to keep the keys and the positions), started with the session. It is not a system service: a service has no session, so no icon and no access to
   the conversation files of the user. On first launch it asks which Mnemarr to link to, with the linking of ADR 0030 (a person approves, names the machine);
   a code stays a way to link without a screen.
7. **Updating is part of it.** The collector says at each pass which system it is for and the SHA-256 of the program it runs; Mnemarr compares it with the
   program it carries for that system, and the machines page shows *Update available* when they differ. The hash is used, not a version number: it works for
   a build that is not a release, and it cannot be wrong about what runs. (Built.) The update itself, **not built**, replaces the program from the Mnemarr it
   sends to, after checking its checksum, without losing the destinations; whether it is one click or automatic is the person's choice, off by default for a
   server, and is decided with the installer (Q-40).
8. **Reinstall is not a revoke.** Until updating exists, a machine row offers *Reinstall* (a one-time code for the same machine, replacing its key on that
   Mnemarr only).

## Consequences

- The model of the download changes: the programs in the image stay generic; the per-machine setting appended to the program (ADR 0027 §15) stays for a
  link by code and is no longer the way a person installs.
- A migration gives each existing machine its identifier at its next heartbeat; two machines that turn out to share one are shown to a person to merge.
- Merging two machines (the duplicates that exist) moves the conversations of one under the other and drops the copies by conversation identifier.
- The collector gains the installer, the identifier, the update and the real uninstall; the server gains the identifier on a machine, rename, merge and the two
  removals; the interface gains their actions and confirmations.
