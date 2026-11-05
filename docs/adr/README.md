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
| [0013](0013-releases-with-semantic-release-consumed-by-pulling.md) | Releases are decided by semantic-release from main, and consumed by pulling | D-33 |
| [0014](0014-the-agent-merges-technical-pull-requests.md) | The agent merges technical pull requests; the maintainer decides verticals, releases and the rules themselves | D-34 |
| [0015](0015-sources-are-read-write-capable.md) | Sources are read-write capable: Mnemarr writes in the document repositories it owns and proposes changes elsewhere | D-35 |
| [0016](0016-accounts-roles-and-service-accounts.md) | Accounts, permission-based roles and service accounts | D-36 |
| [0017](0017-a-notes-reader-in-the-interface.md) | A notes reader in the interface | D-37 |
| [0018](0018-storage-layer-ado-net-and-numbered-schema-steps.md) | Storage layer: ADO.NET over SQLite, a schema in numbered steps | D-38 |
| [0019](0019-sources-are-declared-through-the-api-and-scanned-into-a-searchable-index.md) | Sources are declared through the API and scanned into a searchable index | D-39 |
| [0020](0020-configuration-as-code.md) | Configuration as code: a YAML document, applied through the API or as a file | D-40 |
| [0021](0021-the-reader-reads-links-from-the-index-and-renders-notes-as-untrusted-text.md) | The reader reads links from the index and renders notes as untrusted text | D-41 |
| [0022](0022-git-sources-are-cloned-and-pulled-in-process-over-https.md) | Git sources are cloned and pulled in-process, over HTTPS with a named token | D-42 |
| [0023](0023-notes-are-written-through-the-api-committed-to-git-and-offered-over-mcp.md) | Notes are written through the API, committed to Git, and offered to assistants over MCP | D-43 |
| [0024](0024-conversations-are-collected-by-position-masked-twice-and-written-as-notes.md) | Conversations are collected by position, masked twice, and written as notes | D-44 |
| [0025](0025-the-collector-is-a-service-per-machine-enrolled-by-a-code-and-configured-from-the-server.md) | The collector is a service on each machine, enrolled by a code, and configured from the server | D-45 |
| [0027](0027-machines-join-with-a-code-hold-a-key-bound-to-their-name-and-say-they-are-alive.md) | Machines join with a code, hold a key bound to their name, and say they are alive | D-47 |
