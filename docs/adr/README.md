# Architecture Decision Records

One file per structural decision, numbered in order: `NNNN-title.md`. Each ADR matches a `D-xx` row in the [spec](../SPEC.md#2-decisions).
An accepted ADR is never edited to change its decision: a new ADR supersedes it.

| ADR | Decision | Spec |
|---|---|---|
| [0001](0001-record-architecture-decisions.md) | Record architecture decisions | — |
| [0002](0002-arr-like-stack-and-external-helpers.md) | *arr-like stack: one container, .NET API, React frontend; external helpers in any language | D-01, D-02 |
| [0003](0003-git-hosted-markdown-vaults-split-by-audience.md) | Markdown vaults hosted in Git, split by audience | D-03, D-04, D-05 |
| [0004](0004-raw-conversations-outside-git.md) | Raw conversations stay outside Git, on a fast local disk | D-06, D-07 |
| [0005](0005-collector-agent.md) | A collector agent on every machine where an assistant runs | D-08, D-09, D-10 |
| [0006](0006-api-first-with-mcp-on-top.md) | The API exposes every capability; the MCP server sits on top, with memory tools and administration tools | D-19, D-20 |
| [0007](0007-assistant-runner-headless-agent-cli.md) | An assistant runner: a headless agent CLI behind an adapter, in an optional container; chat and consolidation | D-21, D-22 |
| [0008](0008-same-quality-chain-as-portarr.md) | The same quality chain and delivery as Portarr | D-23, D-24 |
| [0009](0009-embedded-vector-search-and-local-embeddings.md) | Embedded vector search and local embeddings, both behind interfaces | D-25, D-26 |
| [0010](0010-external-exposure-and-delegated-backups.md) | External exposure without single sign-on, and backups delegated to a backup tool | D-27, D-28 |
| [0011](0011-default-port.md) | Default port 8383 | D-29 |
| [0012](0012-vector-store-and-embeddings-spikes.md) | Vector store and embeddings model: results of the two spikes | D-30, D-31 |
| [0013](0013-releases-with-semantic-release-and-gitops.md) | Releases are decided by semantic-release from main, and deployed by following the registry | D-33 |
