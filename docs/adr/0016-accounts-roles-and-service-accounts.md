# 0016 — Accounts, permission-based roles and service accounts

- **Status**: accepted
- **Date**: 2026-11-03
- **Spec**: D-36 (closes Q-14 and Q-25; applies the rule of section 6 on anonymous endpoints)

## Context

The specification requires authentication "no later than the first endpoint that exposes memory content or settings", and Mnemarr authenticates every call itself (D-27). The maintainer wants people to sign in to the interface with rights that differ (a user who may only read the notes, not configure anything) and **service accounts** with keys for the agents and assistants that read or write through the API and the MCP server. Portarr solved the same problem (its ADR 0009); this follows it, for what Mnemarr needs.

## Decision

1. **One kind of principal for every way in: an account.** An account is a **user** (a person: name and password) or a **service account** (an agent or an assistant: no password, keys only). The cookie of a session, an API key and, later, an OAuth token all produce the same principal.
2. **Local accounts with a cookie session** for people: forms login, passwords hashed with ASP.NET Core's `PasswordHasher` (PBKDF2), a minimum length, a lockout after repeated failures (five within fifteen minutes lock the name, whether it exists or not, until the failures age out), the same answer for an unknown name and a wrong password. The cookie is `HttpOnly`, `SameSite=Strict` and `Secure` by default. A custom request header is required on a write sent with the cookie (CSRF).
3. **A role is a named set of permissions, and the permissions are a closed list in code.** There is one authorization policy per permission, declared on each endpoint, and a **fallback policy that requires a sign-in everywhere else**, so a forgotten attribute fails closed. The built-in roles are defined in code, not stored:
   - `Admin`: everything (accounts included);
   - `Operator`: read and write notes, search, manage sources, read the system status;
   - `Reader`: read notes and search, nothing else;
   - `Assistant` (service accounts): read and write notes, search;
   - `Collector` (service accounts): send conversations, nothing else.
4. **Permissions are read from the database on every request**, not copied into the cookie or a token: disabling an account, changing its role or revoking a key applies at once. The last enabled `Admin` can be neither deleted, disabled nor demoted.
5. **API keys** belong to an account, are 256 random bits shown once, stored only as a hash, revocable, optionally expiring, with the date of their last use. A key never exceeds its account's role: its permissions are the account's, **narrowed by its scope**, an optional list of the sources it may touch (section 6 of the specification). Sent as `Authorization: Bearer` or `X-Api-Key`.
6. **The first administrator** is created at start from `MNEMARR__AUTH__ADMINUSER` and a password read from the file named by `MNEMARR__AUTH__ADMINPASSWORDFILE`, only when there is no account yet. There is no default account.
7. **Anonymous is a short list**: `/health/live`, `/health/ready`, the static shell of the interface and the sign-in call. The status of the system and the OpenAPI document need a sign-in. There is **no "trusted network" mode**.
8. **Access is audited**: sign-ins (and their failures), changes to accounts, roles and keys, and refused keys are appended to an audit log the administrator reads; the use of a key is recorded as its date of last use. Reads and writes of notes join the log with the verticals that bring them.
9. **OAuth for agents is a later step** (the client-credentials grant for service accounts, which the MCP specification uses for remote servers). A new scheme only has to produce the same principal.

## Consequences

- Every endpoint has a permission; the matrix role × endpoint is an integration test, and a new endpoint with no policy is a sign-in-only endpoint, visible in review.
- Permissions per source for people (a user who only sees one source) are not in this step: roles are global for a user, and a key can be scoped to sources. The model leaves room for it.
- One Mnemarr instance per database: the lockout counters live in memory.
- The ingest vertical reuses the `Collector` role and the key scoping; the MCP server reuses the keys.

## Alternatives considered

- **Full ASP.NET Identity**: brings its own schema, UI assumptions and migrations, for features (e-mail confirmation, external logins, two-factor) that are not wanted yet.
- **Fixed roles as an enum**: simpler, but permissions as the unit cost little more and a role never has to be guessed.
- **Permissions in the cookie or a token**: stale until it expires; a disabled account or a revoked key would keep working.
- **A "trusted network" or "disabled for local addresses" mode**: the day a port is published or a proxy is misconfigured, it opens the memory. Single sign-on in front of the interface stays a layer, not a replacement (D-27).
