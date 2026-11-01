# 0004 — Raw conversations stay outside Git, on a fast local disk

- **Status**: accepted
- **Date**: 2026-11-01
- **Spec**: D-06, D-07

## Context

The maintainer wants every assistant conversation, from every machine, centralised and searchable, including verbatim ("what was said at 9:30 on that other machine"). A previous attempt, a memory service fed by a periodic job on a single machine, failed for three reasons: it covered only one machine, its outages went unnoticed, and the assistant did not reliably query it.

Raw conversations differ from curated notes in every way that matters for Git: they only grow, nobody reads them in a Markdown editor, they are not needed offline on devices, and they cannot be pruned without rewriting history. They may also contain secrets pasted by mistake.

## Decision

Data has three layers with different readers and storage:

| Layer | Content | Reader | Storage |
|---|---|---|---|
| **Raw** | Full conversations, timestamped, with machine, tool and project | Machines; the operator for verbatim lookup | Mnemarr's own data folder, compressed, with configurable retention, included in the backup |
| **Consolidated** | Durable notes distilled from conversations: decisions, lessons, observed behaviour, history, per application | Assistants, and the operator who reads | The memory vault, as Markdown in Git, written only by Mnemarr |
| **Documentation** | Specifications and behaviour descriptions of each application | Operator and assistants | Left in the application's repository and indexed in place, read-only |

- Raw conversations are **not** stored in Git. A dedicated repository for them is not created.
- The database and the vector index live on a **local fast disk**, never on a network share (file locking, latency, spun-down disks). Bulk raw files and backups may live on a network share.
- Secrets are masked **before** storage and indexing (details in the spec, §6).

## Consequences

- The Git history stays small and readable; the volume problem is contained in a store designed for it.
- Raw conversations are searchable by meaning and by date without being a Markdown corpus.
- A retention policy and a backup are required for the raw store.
- Consolidating conversations into notes needs a language model, which is a separate, opt-in decision (Q-04).
