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
- **A source is a folder the operator allowed.** Declaring one through the API is a request to read a folder: its path must be inside `Sources:Roots` (by default the `sources` folder of the data folder), and a relative path, a missing folder or a link (anywhere between the root and the folder) is refused, again before every scan, and before each file is read the file and every folder above it are checked for a link. The check and the read are two steps, so a folder swapped for a link in between is a known, narrowed window (a hard link to a file elsewhere on the same disk is not detected either); the Git engine will control the checkout. A key limited to some sources can change the mode of its sources, not their name or folder, and cannot declare or forget one: otherwise it could repoint a source at another folder, or make its own scope match nothing. Forgetting a source never touches its files; a search shows only the sources the caller may touch.
- **A configuration document is input from outside** and is read strictly: at most 256 KB, one document, a bounded depth, no anchors, aliases or tags, and an unknown key is an error. A document with an error changes nothing. A key limited to some sources can neither export nor apply one, a configuration file is the only document when one is given, and what a document owns cannot be changed by hand. Secrets never go in a document ([ADR 0020](docs/adr/0020-configuration-as-code.md)).
- **The first administrator** comes from `MNEMARR__AUTH__ADMINUSER` and a password read from the file named by `MNEMARR__AUTH__ADMINPASSWORDFILE`, only while there is no account. Mount that file as a secret; never put the password in an environment variable.
- Every response, errors included, carries conservative security headers and a strict Content-Security-Policy; an error never shows its cause.
- The image runs as a non-root user, with no shell; the deployment examples run it with a read-only root filesystem and no capability.
- The CI scans for secrets and vulnerable dependencies, and the code is analysed by CodeQL and SonarQube.

**The target model** (decided, not implemented: nothing below holds yet, except what is stated above)

- Nothing leaves the host unless the operator configures it; any external model or embeddings API is opt-in.
- Secrets are never stored in memories nor returned by the API: they are masked before they leave the machine that holds the conversation, and checked again on the server.
- A collector agent holds an ingest-only credential (the `Collector` role), one per machine, revocable: a compromised agent can add conversations, it cannot read any memory. The ingest and MCP endpoints come with their own verticals.

The threat model is in the [specification](docs/SPEC.md), §6.
