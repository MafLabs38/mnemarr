# 0030 — A collector sends to several Mnemarr, and links to one by asking and being approved

- **Status**: accepted
- **Date**: 2026-11-06
- **Spec**: D-50; refines D-45 ([0025](0025-the-collector-is-a-service-per-machine-enrolled-by-a-code-and-configured-from-the-server.md)) and D-47 ([0027](0027-machines-join-with-a-code-hold-a-key-bound-to-their-name-and-say-they-are-alive.md))

## Context

A collector was tied to **one** Mnemarr: one address, one key, one file of positions. Joining a machine that already had a collector to another Mnemarr (a
development instance and a production one, the usual case) meant installing again, and the second install **replaced** the first: the first Mnemarr stopped
receiving. A person also had to carry a long line or a code from the page of Mnemarr to the machine, within 15 minutes, to link it at all.

What the maintainer asked for is the natural model: a machine has a collector; the collector knows the Mnemarr it sends to; every conversation goes to each of
them; and linking another is something done *from the machine*, in a few clicks, not a reinstall.

## Decision

1. **A collector keeps a list of destinations.** Each is a Mnemarr with its own address, its own key, its own name for this machine (each Mnemarr names its
   machines on its own) and its own positions. The list is `destinations.json` in the folder of the collector, beside the keys (`key-<name>`, readable by the
   user only). A folder of an earlier collector (one address, one key, `state.json`) is read as a list of one, with the positions it had, and the list is
   written: nothing is sent twice.
2. **Every pass sends to every destination.** The list is read again at each pass, so that linking or unlinking applies without a restart. Each destination
   has its own positions and its own sign of life; one that cannot be reached does not hold the others back (the pass is reported as not done, and the next
   goes on). Reading a conversation is local and cheap; it is done once per destination. Several sources (readers) feed every destination in the same way.
3. **Linking adds, it does not replace.** `enroll` (a code a person made in Mnemarr), the installers and the downloaded program all **add** a destination,
   or renew the key of the one with the same address (keeping its name and positions). `unlink NAME` takes one away with its key; what it received stays
   there, and revoking the machine in that Mnemarr is how it is removed for good. `destinations` lists them.
4. **A machine can ask to be linked** (`link`, without a code), like `gh auth login`: it asks the Mnemarr, which answers with a short code and the address of
   a page of its own; a person who manages the collectors opens it, sees the name the machine gives itself, **chooses the name it will have in that Mnemarr**
   and its kind, and approves (or refuses). The machine, which keeps asking with a secret only it knows, is given its key **once**. The server does not need
   to find the machine: the machine finds the server.
5. **The request is the reverse of an enrolment, and as guarded.** `POST /api/v1/ingest/pair` and `/pair/poll` are anonymous, under the ingest prefix, and
   give nothing until a person approves: the user code is 12 characters of the same alphabet as an enrolment code (about 60 bits), the secret 256 bits, both
   kept as hashes; a request lives 10 minutes; at most 20 wait at once and 10 are made a minute for the whole server; a wrong secret is counted like a wrong
   code (ten a minute, then a 429). What a machine says of itself is shown as plain, short text. A person's approval names the machine, so an attacker who
   asks cannot choose a name: the worst a stranger can do is put requests in the list, which a person refuses (and, since the limits are for the whole server, keep the list full so that a real computer has to wait: the proxy limits the requests per address, [network](../network.md)). A request is approved with its code (ADR 0031 §3): from the address that the computer opens, the code is filled in; from the list of requests, the person types the code the computer shows. In both cases the person reads the name the computer gives itself and decides, so that a request made by anybody is not approved from its name alone. (Changed on 2026-11-07: the code was first always typed, to prove that the person looked at the computer; the maintainer asked for it to be filled in from the address, which is what a person expects. The risk stays that an address sent by a stranger shows a request the person did not make: the name of the computer is the check.)
6. **From the machine, without a terminal.** On Windows the icon has **Link to a Mnemarr…**: the collector serves, for thirty minutes at most, a page on the
   loopback address of the computer only, under an address nobody can guess and for the two names of the computer only (so that a name made to point there is
   not served), with no script and a closed content policy. The person types the address of the Mnemarr, the collector asks, the browser opens the page of
   that Mnemarr, and the local page shows how it goes. The menu lists the Mnemarr it sends to, and the icon shows the worst state of them.
7. **An address is opened without a shell.** The page to approve comes from a server; the collector opens only `http(s)` addresses without space or control
   character, through the handler of the system (never `cmd`, where `&` in an address would be a command).
8. **Not here.** Finding the machine on the local network (mDNS) is not built: the machine already finds the server, which is what a person needs. Configuring
   what each destination receives (a reader switched off for one) belongs to the configuration from the server (vertical 2c).

## Consequences

- A machine that has a collector linked to the development instance is linked to production by the icon or `mnemarr-collector link`, or by running the
  download or the line of production again: both instances receive, each with the history of the machine.
- Each destination has its own history to send: a new one receives everything from the beginning (the server skips what it has), as a machine that joins
  always did.
- The cost of sending to N destinations is N reads of the same files. Two or three is the expected number.
- The installer lines of earlier Mnemarr (`enroll`) keep working; they now add instead of replace.
- A request to be linked is a new way to put a row in front of an administrator. It is limited and refused by default; it is not a way in.
