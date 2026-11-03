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

## Service accounts and keys

Anything that is not a person (an assistant, a collector, a companion such as Portarr) signs in with a **key** that belongs to a **service account**, created in the interface (Accounts, then Keys) or through the API:

| Use | Role of the service account |
|---|---|
| A companion that watches the status of the system | `Monitor` (`system.read` only) |
| An assistant that reads and searches notes | `Reader` |
| An assistant that also writes notes | `Assistant` |
| A collector that sends conversations (later) | `Collector` |

Send the key as `X-Api-Key: <key>` or `Authorization: Bearer <key>`. It is shown **once**, when it is created, and only a hash of it is kept: if it is lost, create another and revoke the old one. A key can be limited to some sources and can expire. `/health/live` and `/health/ready` need no key.
