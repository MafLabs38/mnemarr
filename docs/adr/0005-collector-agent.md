# 0005 — A collector agent on every machine where an assistant runs

- **Status**: accepted
- **Date**: 2026-11-01
- **Spec**: D-08, D-09, D-10

## Context

An MCP server alone cannot capture conversations: the assistant decides whether to call its tools, which is exactly what failed before. Conversations are stored on the disk of the machine where the assistant **executes**, not where the screen is. With a remote development session (an editor on one machine connected over SSH to another), the conversation lives on the remote machine.

Machines are mixed (Windows PCs, Linux servers and VMs) and some leave the home network (a laptop on a mobile connection, a holiday machine).

## Decision

Add a **collector agent**: a small background program, one per machine where an assistant runs, as an external helper (see [0002](0002-arr-like-stack-and-external-helpers.md)).

- **Reads** the assistant's conversation files on disk, keeps the position reached in each file, and sends only new content. Uploads are idempotent (session identifier and position). It also catches up with existing history on first run.
- **Outbound only**, over HTTPS, so it works from any network. No inbound port.
- **Local queue**: if Mnemarr is unreachable, it keeps the data and retries; nothing is lost.
- **Ingest-only credential**, one per machine, revocable. A compromised agent cannot read memories.
- **Masks secrets before sending**; the server checks again.
- **Reports its own health** (last send, queue size) so that Mnemarr can tell "no conversations" from "agent silent".
- **Installation is one command** that registers the server address and credential and, with the operator's consent, wires the assistant's MCP configuration and start-of-session hook.
- Builds for **Windows and Linux**. First implementation in **Rust**: a footprint of well under a megabyte of memory, against tens of megabytes for a .NET runtime. Another implementation could replace it.
- Supported assistant at first: **Claude Code**. Others are added as separate readers behind the same upload contract.

## Consequences

- The ingest endpoint must be reachable from outside the home network and cannot sit behind an interactive single sign-on; it is protected by the per-agent credential (Q-07 covers exposure).
- The conversation file format of each assistant is not a public contract: readers must be tolerant, and the raw lines are stored as received.
- A second toolchain (Rust) and a Windows plus Linux build matrix in CI.
- The agent is the one component besides the container that Mnemarr ships and versions; the upload API needs a compatibility policy.
