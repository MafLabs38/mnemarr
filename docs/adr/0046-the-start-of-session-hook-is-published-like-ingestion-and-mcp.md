# 0046 — The start-of-session hook is published like ingestion and MCP, and the interface stays behind the single sign-on

- **Status**: accepted
- **Date**: 2026-11-26
- **Spec**: D-67; closes Q-42; refines D-27 ([0010](0010-external-exposure-and-delegated-backups.md))

## Context

D-27 lets the two paths that programs use, `/api/v1/ingest/` (collectors, satellites) and `/mcp` (assistants), be published outside the proxy's single sign-on, while the interface stays behind it. The
start-of-session hook ([ADR 0037](0037-an-assistant-is-told-what-the-memory-knows-when-it-opens-a-session.md), [ADR 0038](0038-a-new-session-resumes-the-thread-of-the-last-one.md)) is a third program
of the same kind: it makes one `GET /api/v1/context` with a read key. Whether to publish it too was left open (Q-42), because its answer is the most sensitive text Mnemarr gives.

Nothing in the product prevented an installation from running with no single sign-on in front of the interface, with only Mnemarr's own password to protect it. The dictation of the chat
([ADR 0047](0047-the-chat-takes-dictation-through-a-swappable-transcriber.md)) puts a paid key behind the interface, which makes that gap a cost as well as an exposure: the interface must
be behind a single sign-on before dictation is turned on.

## Decision

1. **Three paths, and only three, are published outside the single sign-on**: the prefix `/api/v1/ingest/`, the exact path `/mcp` and the exact path `/api/v1/context`. Everything else, the interface, the rest of
   the API (the chat and what it consumes included) and the health endpoints, is behind it.
2. **The hook is published like the other two**: an exact path, the same rate limit per address at the proxy. The risk Q-42 named is held by the key, not by the proxy: the hook's key is a read key of its own (D-57),
   opt-in, shown once and revocable, and Mnemarr authenticates every call (D-27). A proxy that a program cannot pass would only make the hook return nothing.
3. `docs/network.md` gives the three locations and their rules; an example nginx server block is in the deployment repository of the operator, not here.

## Consequences

- A person signs in twice (the single sign-on, then Mnemarr's own password): Mnemarr does not read the proxy's identity, and that stays so (D-27: safety must not rely on the proxy).
- A leaked read key gives the thread of an application until it is revoked; the audit shows every use of the key.
- An operator who cannot put a single sign-on in front of the interface keeps a working Mnemarr, protected by its password and its lockout, and is told in `docs/network.md` that the dictation is then reachable by
  whoever has a password.

## Alternatives considered

- **A host name reachable only from the home network for the hook**: a session on a laptop away from home would start without its thread, which is the need the hook answers.
- **The hook through the MCP server** (a tool that returns the pack): the hook is a plain HTTP call by design (the collector's `context` command, no MCP client), and it would not change who can read the pack.
