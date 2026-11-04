# Configuration

Mnemarr reads its settings from `appsettings.json`, then environment variables (`MNEMARR__<SECTION>__<NAME>`, double underscore between levels), then the command line. A wrong value stops the start with a message that names the setting. A secret is never an environment variable: it is a file, and the setting holds its path.

## Data

| Setting | Meaning |
|---|---|
| `MNEMARR__DATAFOLDER` | The folder that holds the database and the key ring. It must exist (a volume that was not mounted must not be replaced by an empty folder: `/health/ready` fails). |

## Access (`Auth`)

| Setting | Default | Meaning |
|---|---|---|
| `MNEMARR__AUTH__ADMINUSER` | none | The first administrator, created at start while there is no account. Set together with the password file or not at all. |
| `MNEMARR__AUTH__ADMINPASSWORDFILE` | none | The file that holds that password (a secret mounted as a file), 12 characters at least. |
| `MNEMARR__AUTH__RESETPASSWORDUSER` | none | Break-glass: this account gets the password of the file below at each start, is enabled, loses its lockout and its sessions. Both settings are removed once used. |
| `MNEMARR__AUTH__RESETPASSWORDFILE` | none | The file that holds the new password. |
| `MNEMARR__AUTH__COOKIESECURITY` | `Always` | `Always`: the session cookie is `Secure` (HTTPS only). `SameAsRequest`: for a sandbox in plain HTTP; behind TLS, leave the default. |
| `MNEMARR__AUTH__SESSIONHOURS` | `12` | How long a session lasts without being used (1 to 720). |
| `MNEMARR__AUTH__SESSIONMAXDAYS` | `30` | How long a session lasts at most, used or not (1 to 365). |

`SESSIONHOURS`, `SESSIONMAXDAYS` and the audit retention will move to the settings of the interface when it exists; the first administrator, the password files and the cookie mode stay in the environment.

## Audit log (`Audit`)

| Setting | Default | Meaning |
|---|---|---|
| `MNEMARR__AUDIT__RETENTIONDAYS` | `90` | How long the audit log keeps its events (1 to 3650). The log is shared by every vertical, so it has its own section. |

See [ADR 0016](adr/0016-accounts-roles-and-service-accounts.md) for what each protects.

## Sources and indexing (`Sources`, `Indexing`)

| Setting | Default | Meaning |
|---|---|---|
| `MNEMARR__SOURCES__ROOTS__0`, `__1`… | the `sources` folder of the data folder | The folders a source may be in, each an absolute path (the application refuses to start otherwise). A source is declared in the interface (Sources) or through the API with a folder inside one of them; anything else is refused. Mount a vault read-only and name its folder here. |
| `MNEMARR__INDEXING__SCANINTERVALSECONDS` | `30` | How often every source is compared with its folder (1 to 86400). A source can also be indexed at once from the interface. |

Hidden folders (`.git`, `.obsidian`), links and files larger than 2 MB are not indexed. A pass lists the folder and reads only the notes whose size or last write time changed, so a vault on a share is not read in full every interval; `POST /api/v1/sources/{id}/index?rebuild=true` reads everything. The folder of a source is checked against the roots again before every scan, and a link anywhere between the root and the folder is refused. If a scan fails (the folder was unmounted, or is outside the roots now), the source shows why (`lastError`) and `/health/ready` is degraded with the reason `source-unreadable`; its old notes stay searchable. `POST /api/v1/sources/{id}/index?rebuild=true` cuts every note again. The mode of a source is its write switch: read-only unless you turn writing on. Forgetting a source in the interface forgets its index, never its files.

## Git sources (`Git`)

A source can be the checkout of a Git repository ([ADR 0022](adr/0022-git-sources-are-cloned-and-pulled-in-process-over-https.md)): declare it with a `repository` (an `https` address, optionally a branch and the name of a credential) and a folder inside the roots, where it is cloned. Mnemarr clones into a folder that does not exist or is empty, then fast-forwards; it never merges, never forces, and reports a checkout that has diverged or has uncommitted changes without touching it. SSH is not supported.

| Setting | Default | Meaning |
|---|---|---|
| `MNEMARR__GIT__SYNCINTERVALSECONDS` | `300` | How often every Git source is brought up to date (10 to 86400). A source can also be synchronised at once from the interface (`POST /api/v1/sources/{id}/sync`). |
| `MNEMARR__GIT__HOSTS__0`, `__1`… | none | The hosts a source may be cloned from, as host names (no scheme, port or path). **Empty means none: no Git source is accepted until you name the hosts.** The list is read again at every sync. |
| `MNEMARR__GIT__SYNCTIMEOUTSECONDS` | `600` | The longest one sync of one source may take (10 to 3600); it is then given up, and the next interval tries again. |
| `MNEMARR__GIT__CREDENTIALS__<name>__HOST` | none | The host the token is for. A credential is sent to this host and to no other. |
| `MNEMARR__GIT__CREDENTIALS__<name>__TOKENFILE` | none | A file that holds the access token (read at each sync, so a rotation needs no restart). Prefer it to `TOKEN`; mount it as a secret. |
| `MNEMARR__GIT__CREDENTIALS__<name>__TOKEN` | none | The token itself, when a file is not possible. Exactly one of `TOKEN` and `TOKENFILE`. |
| `MNEMARR__GIT__CREDENTIALS__<name>__USERNAME` | `x-access-token` | The user name sent with the token (GitHub, GitLab and Gitea accept any name with a token). |

A source names a credential (`credential: github`), never a token; a public repository needs none. If a sync fails, the source shows a code (`lastSyncError`: `auth-failed`, `repository-not-found`, `unreachable`, `branch-not-found`, `diverged`, `local-changes`, `folder-not-empty`, `different-repository`, `credential-missing`, `outside-roots`, `host-not-allowed`, `sync-failed`) and `/health/ready` is degraded with the reason `source-unreadable`; the notes already read stay searchable. The words never carry an address, a path or a message from the host. A repository address uses the https port, and a checkout folder cannot be inside the folder of another source nor contain one. Changing the branch of a source moves a clean checkout to it; changing the address of a source whose folder is already a checkout needs an empty folder.

## Configuration as code (`Config`)

`GET /api/v1/config/status` says whether a file is in charge, whether it was applied and why not (in words that give no path); the interface shows it and does not offer to apply a document while a file is in charge.

| Setting | Default | Meaning |
|---|---|---|
| `MNEMARR__CONFIG__FILE` | none | A configuration document (YAML) applied at start and again whenever it changes, or after a failure (a folder mounted late). An absolute path. A file that cannot be applied does not stop Mnemarr: `/health/ready` is degraded with the reason. |
| `MNEMARR__CONFIG__RELOADSECONDS` | `30` | How often the file is looked at again (1 to 86400). |

The same document can be previewed and applied through the API (`PUT /api/v1/config`, `?dryRun=true` to preview; `GET /api/v1/config` exports the current state) or in the interface (Sources). It describes the memory vault (one object) and the knowledge vaults and application documentation (lists), each with an optional `repository` for a Git source; see [deploy/mnemarr.config.example.yaml](../deploy/mnemarr.config.example.yaml) and [ADR 0020](adr/0020-configuration-as-code.md). A section that is absent is left alone; one that is present, even empty, is managed. What a document owns is marked *managed by configuration* and is changed by changing the document. When `MNEMARR__CONFIG__FILE` is set, the file is the one document: `PUT /api/v1/config` only previews (`?dryRun=true`) and refuses to apply (409). Never put a secret in it.

## Service accounts and keys

Anything that is not a person (an assistant, a collector, a companion such as Portarr) signs in with a **key** that belongs to a **service account**, created in the interface (Accounts, then Keys) or through the API:

| Use | Role of the service account |
|---|---|
| A companion that watches the status of the system | `Monitor` (`system.read` only) |
| An assistant that reads and searches notes | `Reader` |
| An assistant that also writes notes | `Assistant` |
| A collector that sends conversations (later) | `Collector` |

Send the key as `X-Api-Key: <key>` or `Authorization: Bearer <key>`. It is shown **once**, when it is created, and only a hash of it is kept: if it is lost, create another and revoke the old one. A key can be limited to some sources and can expire. `/health/live` and `/health/ready` need no key.
