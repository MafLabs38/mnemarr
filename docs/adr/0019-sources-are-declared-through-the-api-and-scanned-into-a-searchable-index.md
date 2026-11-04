# 0019 — Sources are declared through the API and scanned into a searchable index

- **Status**: accepted
- **Date**: 2026-11-04
- **Spec**: D-39 (builds on D-35 and D-38; answers part of Q-29)

## Context

The first feature vertical has to turn a folder of Markdown notes into something that can be searched, without anyone running a command: the specification asks for automatic, incremental indexing (F-IDX-01) and for results that show their source (F-SEA-01). It also has to settle which settings are bootstrap settings and which are runtime data (Q-29), because declaring a source is how an API user asks Mnemarr to read a folder.

## Decision

1. **A source is runtime data, kept in the database and managed through the API** (and so by the interface): a name, a kind (knowledge vault, memory vault, application documentation), a mode (read only, read and write, D-35) and a folder. It is created, listed, indexed on demand and forgotten with `/api/v1/sources`. Forgetting a source forgets its notes and chunks; **it never touches the folder or its files.**
2. **Where a source may be is a bootstrap setting**, `Sources:Roots` (by default the `sources` folder of the data folder). A path outside the roots, a relative path, a folder that does not exist and a link are refused. Declaring a source is a request to read a folder: the operator decides where that can be, and an API user cannot widen it.
3. **Notes are found by scanning, not by watching.** At start and then every `Indexing:ScanIntervalSeconds` (30 by default), each source is compared with its folder, and a source can be indexed on demand. A folder can change by a pull, by a person or by Obsidian; a scan is the same answer for all of them and survives a restart. Hidden folders (`.git`, `.obsidian`), links and files above 2 MB are skipped.
4. **The index is incremental by content hash.** A note whose SHA-256 did not change is left alone; a new or changed one is cut again and replaced; one that has gone is removed. One pass runs at a time.
5. **A note is cut into chunks that follow its headings** (a section that is too long is cut again at paragraphs, then at a space; a heading inside a code fence is code). The location characterises the note: **the first folder is its topic**, and no front matter is required (D-35). The chunks are what the vectors vertical will embed, so the cut is made once.
6. **Search is full text in SQLite (FTS5), ranked by BM25**, accent-insensitive, any of the words, the best first, with the source, the note, the heading and the words around the match. What a person types is turned into quoted words, so it can never be a query syntax. Vectors and hybrid ranking come on the same chunks (D-25, D-30).
7. **A search takes the caller's `SourceScope`** (ADR 0016): a key limited to some sources sees those, one limited to none sees nothing, and a source out of scope is as absent as one that does not exist.
8. **Every kind of source has a rule, and the rules are a closed list in code** (like the roles; a new kind is a decision): the **memory vault** is required and unique (exactly one, D-14), **knowledge vaults** are as many as there are (courses, notes taken for work, each its own vault), **application documentation** is one source per application. A kind that is unique cannot be declared twice (409); a source the base requires cannot be removed, only changed (409); a source's kind never changes, so a source of another kind is another source. What is missing from the base is said, not left to errors: `GET /sources/kinds` gives each kind with its rule and its count, the interface shows the required slot to fill, and `/health/ready` is degraded with the reason `source-missing` and the kind.
9. **The schema is step 2** of ADR 0018: `sources`, `notes`, `chunks`, and the full-text table kept in step by triggers.

## Consequences

- The interface is organised by kind, not by a generic list: a required kind is a slot (filled or to fill), an optional kind is a list with its own add form, and there is no "kind" to choose.

- An operator mounts a vault read-only, names its folder in `Sources:Roots`, and declares the source in the interface: nothing else to configure.
- A scan of a large vault costs a read of every file; the hash makes the writes proportional to the changes. Watching files would be cheaper, and less reliable across network shares and Git pulls: it can be added as an optimisation without changing the model.
- Q-29 is partly answered: bootstrap settings (paths, roots, the first administrator, cookies) stay in the environment; sources, and later the rest of what the interface configures, are runtime data. The settings API stays to be decided.
- The folder of a source is shown to whoever may manage sources: a path of the operator's setup, to someone the operator trusts with sources.

## Alternatives considered

- **Sources in a configuration file**: simple, but not manageable by the interface or by an assistant, and a change needs a restart.
- **A file watcher**: fast, but unreliable on shares and during a `git pull`, and lost across restarts: the scan is needed anyway.
- **Accept any folder the process can read**: the API would be a way to read the machine. The roots keep the decision with the operator.
