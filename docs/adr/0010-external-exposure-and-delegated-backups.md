# 0010 — External exposure without single sign-on, and backups delegated to a backup tool

- **Status**: accepted
- **Date**: 2026-11-01
- **Spec**: D-27, D-28

## Context

**Exposure.** Collector agents and assistants must reach Mnemarr from any network (a mobile connection, a machine away from home). They have no browser, so they cannot complete an interactive single sign-on that usually protects the user interface.

**Backups.** Some data is irreplaceable (raw conversations, settings, credentials); the rest can be rebuilt (the vector index, the search index, and the memory notes, which live in Git). Copying a live SQLite file while it is being written can produce a corrupt backup.

## Decision

**Exposure**
- Mnemarr **authenticates every API and MCP call itself**: per-machine ingest-only tokens for agents, per-user API keys for assistants, all hashed at rest, revocable, with rate and size limits.
- A deployment may publish the **ingest endpoint and the MCP endpoint on a dedicated host name** that the reverse proxy lets through without single sign-on, while the user interface stays behind it. Safety does not depend on the proxy's sign-on.
- A VPN to every machine remains a documented alternative for operators who prefer a closed network.

**Backups**
- Mnemarr **produces a consistent snapshot** of its data on a schedule, using SQLite's online backup mechanism, in a backup folder. It does not upload anything.
- **Shipping the snapshot to remote storage (for example an S3-compatible bucket) is the job of the operator's backup tool**, which should encrypt it. Mnemarr documents what to back up: the snapshot, the raw conversations and the settings. The index can be rebuilt.
- Backup health is visible to Portarr, which already tracks backups.

## Consequences

- The ingest and MCP endpoints are internet-facing: their authentication, limits and logging are security-critical and need dedicated tests and review.
- Mnemarr does not reimplement a backup uploader, consistent with the conductor principle.
- A restore procedure must be documented and tested.
