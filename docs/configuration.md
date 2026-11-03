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
| `MNEMARR__AUTH__AUDITRETENTIONDAYS` | `90` | How long the audit log keeps its events (1 to 3650). |

See [ADR 0016](adr/0016-accounts-roles-and-service-accounts.md) for what each protects.
