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
| `MNEMARR__SOURCES__ROOTS__0`, `__1`… | the `sources` folder of the data folder | The folders a source may be in. A source is declared in the interface (Sources) or through the API with a folder inside one of them; anything else is refused. Mount a vault read-only and name its folder here. |
| `MNEMARR__INDEXING__SCANINTERVALSECONDS` | `30` | How often every source is compared with its folder (1 to 86400). A source can also be indexed at once from the interface. |

Hidden folders (`.git`, `.obsidian`), links and files larger than 2 MB are not indexed. Forgetting a source in the interface forgets its index, never its files.

## Configuration as code (`Config`)

| Setting | Default | Meaning |
|---|---|---|
| `MNEMARR__CONFIG__FILE` | none | A configuration document (YAML) applied at start and again whenever it changes, or after a failure (a folder mounted late). An absolute path. A file that cannot be applied does not stop Mnemarr: `/health/ready` is degraded with the reason. |
| `MNEMARR__CONFIG__RELOADSECONDS` | `30` | How often the file is looked at again (1 to 86400). |

The same document can be previewed and applied through the API (`PUT /api/v1/config`, `?dryRun=true` to preview; `GET /api/v1/config` exports the current state) or in the interface (Sources). It describes the memory vault (one object) and the knowledge vaults and application documentation (lists); see [deploy/mnemarr.config.example.yaml](../deploy/mnemarr.config.example.yaml) and [ADR 0020](adr/0020-configuration-as-code.md). A section that is absent is left alone; one that is present, even empty, is managed. What a document owns is marked *managed by configuration* and is changed by changing the document. Never put a secret in it.

## Service accounts and keys

Anything that is not a person (an assistant, a collector, a companion such as Portarr) signs in with a **key** that belongs to a **service account**, created in the interface (Accounts, then Keys) or through the API:

| Use | Role of the service account |
|---|---|
| A companion that watches the status of the system | `Monitor` (`system.read` only) |
| An assistant that reads and searches notes | `Reader` |
| An assistant that also writes notes | `Assistant` |
| A collector that sends conversations (later) | `Collector` |

Send the key as `X-Api-Key: <key>` or `Authorization: Bearer <key>`. It is shown **once**, when it is created, and only a hash of it is kept: if it is lost, create another and revoke the old one. A key can be limited to some sources and can expire. `/health/live` and `/health/ready` need no key.
