# Security Policy

Mnemarr stores sensitive material: full conversations with AI assistants and notes about your work. We take security reports seriously.

## Reporting a vulnerability

**Do not open a public issue.** Use GitHub's [private vulnerability reporting](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability) on this repository ("Security" tab, then "Report a vulnerability").

Please include the affected version, reproduction steps and the impact you observed. We aim to acknowledge reports within 7 days.

## Supported versions

Until 1.0, only the latest release receives security fixes.

## Security model (summary)

Mnemarr is at the foundation stage: it has no memory feature yet. It **does authenticate**: every call but the probes, the interface's shell and the sign-in call needs a session or a key.

**True today**

- **Access is closed by default** ([ADR 0016](docs/adr/0016-accounts-roles-and-service-accounts.md)). Anonymous is `/health/live`, `/health/ready`, the static shell of the interface and the sign-in call; the system status and the OpenAPI document need a sign-in. A fallback policy requires a sign-in on any endpoint that declares nothing, and a test lists the anonymous ones. There is no "trusted network" mode.
- **People** sign in with a name and a password (hashed with PBKDF2, 12 characters at least, five failures lock the name for up to fifteen minutes) and get an `HttpOnly`, `SameSite=Strict`, `Secure` session cookie (named with the `__Host-` prefix) backed by a session on the server, of which only a hash is kept (signing out, a new password, disabling the account and a lifetime limit end it, wherever the cookie was copied); the sign-in call is limited in concurrency and in size; a write that does not carry an API key needs a custom request header. **Services and assistants** have service accounts, and only they have API keys (a person has none): 256 random bits, shown once, stored hashed, revocable, optionally expiring and limited to some sources, and never above their account's role.
- **Roles** are sets of permissions from a closed list in code (`Admin`, `Operator`, `Reader`, `Assistant`, `Collector`, `Monitor`). Permissions are read from the database on every request: disabling an account, changing its role or revoking a key applies at once. The last enabled administrator cannot be removed.
- **Sign-ins, their failures, changes to accounts and keys, and refused keys are audited** (what anyone can provoke is sampled, and the log has a retention); the audit log never holds a secret.
- **If the only administrator cannot sign in**, set `MNEMARR__AUTH__RESETPASSWORDUSER` and `MNEMARR__AUTH__RESETPASSWORDFILE` for one start, then remove them ([configuration](docs/configuration.md)). `/health/ready` reports "degraded" while there is no account at all.
- **The first administrator** comes from `MNEMARR__AUTH__ADMINUSER` and a password read from the file named by `MNEMARR__AUTH__ADMINPASSWORDFILE`, only while there is no account. Mount that file as a secret; never put the password in an environment variable.
- Every response, errors included, carries conservative security headers and a strict Content-Security-Policy; an error never shows its cause.
- The image runs as a non-root user, with no shell; the deployment examples run it with a read-only root filesystem and no capability.
- The CI scans for secrets and vulnerable dependencies, and the code is analysed by CodeQL and SonarQube.

**The target model** (decided, not implemented: nothing below holds yet, except what is stated above)

- Nothing leaves the host unless the operator configures it; any external model or embeddings API is opt-in.
- Secrets are never stored in memories nor returned by the API: they are masked before they leave the machine that holds the conversation, and checked again on the server.
- A collector agent holds an ingest-only credential (the `Collector` role), one per machine, revocable: a compromised agent can add conversations, it cannot read any memory. The ingest and MCP endpoints come with their own verticals.

The threat model is in the [specification](docs/SPEC.md), §6.
