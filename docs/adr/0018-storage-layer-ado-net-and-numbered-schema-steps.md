# 0018 — Storage layer: ADO.NET over SQLite, a schema in numbered steps

- **Status**: accepted
- **Date**: 2026-11-03
- **Spec**: D-38 (answers the data access, migration and WAL parts of Q-22; the shared writer and the `sqlite-vec` connection stay open)

## Context

The accounts vertical (ADR 0016) was the first to keep data, so it had to settle how Mnemarr talks to SQLite. Mnemarr is SQLite only (D-25): full-text search and vectors live in the same file, which Portarr's data layer (Dapper and FluentMigrator over a server database) does not cover. The sources, notes and ingestion verticals build on whatever this vertical chooses, so it is decided here, not by accident.

## Decision

1. **Plain ADO.NET over `Microsoft.Data.Sqlite`**, behind one interface per aggregate (`IAccountStore`, `ISessionStore`, `IAuditLog`…) and a single `IDatabase` that opens connections. No ORM and no Dapper: the queries are few and small, parameters are always bound, and the vector and full-text extensions need raw SQL anyway.
2. **The schema is a list of numbered steps** applied in order, the number kept in `PRAGMA user_version`. A step is never edited once released; a change is a new step. A database with a higher version than the code refuses to open (readiness reports it). The accounts vertical is step 1; the sources vertical becomes step 2 of the same list when it is rebased.
3. **The steps run on the first open of the database**, which the host does at start (the first-administrator check opens it). If the data folder is missing, nothing is created: the database stays down, and `/health/ready` says so. This differs from Q-22's "before the first request" only for a volume that is not mounted, which is the case where creating the folder would hide the mistake.
4. **WAL mode, foreign keys on, one connection per call.** SQLite connections are cheap, and the pool keeps them. The file is readable by its owner only, because it holds the hashes of passwords, sessions and keys.
5. **The calls are synchronous.** `Microsoft.Data.Sqlite` has no asynchronous I/O (its `Async` methods run the synchronous call), and the local file answers in microseconds under WAL, so `Task`-returning stores would add cost and no concurrency. The work that really is slow, hashing a password, is awaited (ADR 0016). If the storage ever leaves the process, the interfaces change then.
6. **A caller that can go on without the database** (a hosted service, a probe) catches one predicate, `StorageErrors.IsUnavailable`, which says the database or its folder cannot be reached; it never catches the driver's types itself.

## Consequences

- Every vertical adds a step and its store interface, and tests it against a real SQLite file in a temporary folder (`TestWorkspace`), not a mock.
- `IDatabase.Open()` is where loading the `sqlite-vec` extension and sharing the one writer will go: the vectors vertical changes `Database`, not the stores, which only ever see an open connection.
- Writers: SQLite allows one at a time. The way ingestion, embedding, re-embedding and snapshots share it, and who loads `sqlite-vec` into the connection, remain Q-22, decided with the vectors vertical.
- Raw SQL is written in the store classes and in `Schema` (the version and the steps) only; controllers, services and health checks never see a connection.
- Every call opens a connection (two to four per authenticated request): cheap on a local file, and measured when it is not. A shared connection or a unit of work is a change inside the stores.

## Alternatives considered

- **Dapper and FluentMigrator, like Portarr**: a migration framework for one file and a handful of tables, and a dependency that does not help with FTS5 or `sqlite-vec`. Rejected for now; stores are behind interfaces, so it can be revisited.
- **Entity Framework Core**: a model-first layer over a schema that the extensions bypass. Rejected.
- **Asynchronous stores**: see decision 5.
