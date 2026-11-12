# Configuration

Mnemarr reads its settings from `appsettings.json`, then environment variables (`MNEMARR__<SECTION>__<NAME>`, double underscore between levels), then the command line. A wrong value stops the start with a message that names the setting. A secret is never an environment variable: it is a file, and the setting holds its path.

## Data

| Setting | Meaning |
|---|---|
| `MNEMARR__DATAFOLDER` | The folder that holds the database and the key ring. It must exist (a volume that was not mounted must not be replaced by an empty folder: `/health/ready` fails). |

The data folder also holds what the collectors send (there is no setting for it): `raw/<machine>/<session>/` for the raw lines, masked and compressed, one file per batch, and `conversations/` for the notes made of them (the source of the kind `Conversations`, created by Mnemarr). Both are part of the backup; the raw lines are what a better reader will read again (a command to write the notes again from them is not built yet). The name `conversations` is reserved for that source, and no source can be declared in these folders. See [the collector](collector.md).

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
| `MNEMARR__INDEXING__MAXNOTEMEGABYTES` | `32` | The largest note that is read and indexed, in megabytes (2 to 64). A long conversation is one note, so this is a guard for the memory of the host, not a size to stay under. A note above it keeps what was indexed and is counted (`notesTooLarge`, shown on the Sources page); it is not removed (a reconstruction, `POST /api/v1/sources/{id}/index?rebuild=true`, drops it, which is how what was kept of a note is purged). **Memory:** a note is read whole, decoded and cut, which took about 20 times its size at its peak in a measure (a 28 MB note: 665 MB in the container, one note at a time); at most two reads of a note above 2 MB run at once (a third is told to try again after a second). Raise it only with the memory limit of the container in mind. |

Hidden folders (`.git`, `.obsidian`) and links are not indexed, nor are files above `Indexing:MaxNoteMegabytes` (they are counted, and keep what was indexed). A pass lists the folder and reads only the notes whose size or last write time changed, so a vault on a share is not read in full every interval; `POST /api/v1/sources/{id}/index?rebuild=true` reads everything. The folder of a source is checked against the roots again before every scan, and a link anywhere between the root and the folder is refused. If a scan fails (the folder was unmounted, or is outside the roots now), the source shows why (`lastError`) and `/health/ready` is degraded with the reason `source-unreadable`; its old notes stay searchable. `POST /api/v1/sources/{id}/index?rebuild=true` cuts every note again. The mode of a source is its write switch: read-only unless you turn writing on. Forgetting a source in the interface forgets its index, never its files.

## The chat (`Agent`)

The chat needs an optional second container, the assistant runner; see [the chat](chat.md) ([ADR 0035](adr/0035-the-chat-is-a-panel-a-proxy-and-a-runner-with-adapters-for-any-model.md)).

| Variable | Default | Meaning |
|---|---|---|
| `MNEMARR__AGENT__URL` | empty | The address of the runner, for example `http://mnemarr-agent:8489`. Empty: the chat is off, its endpoints answer 404 and the interface shows none. |
| `MNEMARR__AGENT__SECRET` | empty | The secret shared with the runner, 32 characters or more (the start fails otherwise). Never returned, never logged. |
| `MNEMARR__AGENT__MAXCONVERSATIONSPERPERSON` | `50` | How many conversations a person keeps (1 to 1000). The runner has its own cap for everybody and deletes nothing to make room: this times the number of people should stay under it. |

The tools with which the chat changes notes are the MCP writing tools (`MNEMARR__MCP__WRITETOOLS=true`), and those with which it looks after sources have their own switch (`MNEMARR__MCP__SOURCETOOLS=true`).

## Search by meaning (`Embeddings`)

The notes and the conversations are also searched by meaning, with a model that runs on this host (ADR 0034). The image carries the model and the library and sets the two paths; a build without them leaves the feature off.

| Variable | Default | Meaning |
|---|---|---|
| `MNEMARR__EMBEDDINGS__MODELFOLDER` | the image sets it | The folder with `model.onnx` and `tokenizer.json`. Empty: the search by meaning is off and nothing is computed. The files are checked against pinned SHA-256 before they are loaded. |
| `MNEMARR__EMBEDDINGS__EXTENSIONPATH` | the image sets it | The `sqlite-vec` library (`vec0.so`). Required when a model folder is given. |
| `MNEMARR__EMBEDDINGS__MODEL` | `granite-embedding-97m-multilingual-r2` | The model; only the listed ones are supported, and today there is one. Another one needs its files in `MODELFOLDER`. A change computes every passage again in the background. |
| `MNEMARR__EMBEDDINGS__THREADS` | `2` | CPU threads of the model (1 to 64). The first run over a long history takes about a second per ten passages on two threads. |
| `MNEMARR__EMBEDDINGS__BATCHSIZE` | `8` | Passages computed together (1 to 64). |
| `MNEMARR__EMBEDDINGS__MAXTOKENS` | `512` | The longest a passage may be read, in tokens (16 to 8192). |
| `MNEMARR__EMBEDDINGS__IDLESECONDS` | `300` | The safety timer of the indexer (1 to 3600): it is woken when the index has a new passage, so it does not look for work every few seconds; this is how long it waits at most for a change it was not told of. |
| `MNEMARR__EMBEDDINGS__CACHEDAYS` | `30` | How many days a computed vector is kept after it was last used (1 to 3650), so that a passage that comes back in a note that moved, was renamed or was indexed again is not computed again (ADR 0039). A delete that a person asked for (a note, the conversations of a machine, a source) empties the cache (see ADR 0039). |
| `MNEMARR__EMBEDDINGS__DEVICE` | `Cpu` | Where the model runs on this host: `Cpu`, `Cuda` (a GPU, in an image that carries the GPU build of the engine), or `Auto` (the GPU when the engine can use it, else the CPU). The image of Mnemarr is CPU only; for a GPU see [the embedding server](embedding-server.md). |
| `MNEMARR__EMBEDDINGS__GPUMEMORYMB` | `1024` | The most video memory the model may take (256 to 262144). |
| `MNEMARR__EMBEDDINGS__GPUDEVICEID` | `0` | Which GPU. |
| `MNEMARR__EMBEDDINGS__REMOTE__URL` | none | An embedding server that computes the vectors on a GPU. When it does not answer, the CPU of this host computes. The other `REMOTE__*` settings (`KEYFILE`, `TIMEOUTSECONDS`, `RETRYAFTERSECONDS`, `ALLOWINSECUREHTTP`) are in [the embedding server](embedding-server.md#point-mnemarr-at-it). |
| `MNEMARR__EMBEDDINGS__MAXDISTANCE` | the model's (`0.2` for the default one) | The cosine distance beyond which a passage is not an answer by meaning. Empty: the one measured for the model. Set it only to override, and measure. |

## Git sources (`Git`)

A source can be the checkout of a Git repository ([ADR 0022](adr/0022-git-sources-are-cloned-and-pulled-in-process-over-https.md)): declare it with a `repository` (an `https` address, optionally a branch and the name of a credential) and a folder inside the roots, where it is cloned. Mnemarr clones into a folder that does not exist or is empty, then fast-forwards; it never merges, never forces, and reports a checkout that has diverged or has uncommitted changes without touching it. SSH is not supported.

| Setting | Default | Meaning |
|---|---|---|
| `MNEMARR__GIT__SYNCINTERVALSECONDS` | `300` | How often every Git source is brought up to date (10 to 86400). A source can also be synchronised at once from the interface (`POST /api/v1/sources/{id}/sync`). |
| `MNEMARR__GIT__HOSTS__0`, `__1`… | none | The hosts a source may be cloned from, as host names (no scheme, port or path). **Empty means none: no Git source is accepted until you name the hosts.** The list is read again at every sync. |
| `MNEMARR__GIT__COMMITEMAIL` | `mnemarr@localhost` | The address on the commits Mnemarr makes when notes are written in a Git source (the author is the account that wrote the note). It is not a mailbox. |
| `MNEMARR__GIT__SYNCTIMEOUTSECONDS` | `600` | The longest one sync of one source may take (10 to 3600); it is then given up, and the next interval tries again. |
| `MNEMARR__GIT__WRITETIMEOUTSECONDS` | `60` | The longest the pull before a change of a note, and the push after it, may take each (5 to 600). A person or an assistant waits for them while the source is locked; a push that does not leave in time is kept as a commit and the next sync pushes it. |
| `MNEMARR__GIT__CREDENTIALS__<name>__HOST` | none | The host the token is for. A credential is sent to this host and to no other. |
| `MNEMARR__GIT__CREDENTIALS__<name>__TOKENFILE` | none | A file that holds the access token (read at each sync, so a rotation needs no restart). Prefer it to `TOKEN`; mount it as a secret. |
| `MNEMARR__GIT__CREDENTIALS__<name>__TOKEN` | none | The token itself, when a file is not possible. Exactly one of `TOKEN` and `TOKENFILE`. |
| `MNEMARR__GIT__CREDENTIALS__<name>__USERNAME` | `x-access-token` | The user name sent with the token (GitHub, GitLab and Gitea accept any name with a token). |

A source that is declared read-write is also **written**: each change of a note is one commit, pushed to the branch (a fast-forward, never forced), so the token must be allowed to write; a push that fails is kept as a commit and pushed by the next sync (`lastSyncError`: `push-rejected`, …). A source names a credential (`credential: github`), never a token; a public repository needs none. If a sync fails, the source shows a code (`lastSyncError`: `auth-failed`, `repository-not-found`, `unreachable`, `branch-not-found`, `diverged`, `local-changes`, `folder-not-empty`, `different-repository`, `credential-missing`, `outside-roots`, `host-not-allowed`, `push-rejected`, `sync-failed`) and `/health/ready` is degraded with the reason `source-unreadable`; the notes already read stay searchable. The words never carry an address, a path or a message from the host. A repository address uses the https port, and a checkout folder cannot be inside the folder of another source nor contain one. Changing the branch of a source moves a clean checkout to it; changing the address of a source whose folder is already a checkout needs an empty folder.

## MCP (`Mcp`)

Assistants reach Mnemarr over the Model Context Protocol at `POST /mcp` ([ADR 0023](adr/0023-notes-are-written-through-the-api-committed-to-git-and-offered-over-mcp.md)): HTTP, one JSON-RPC message per request, answered as JSON; no stream, no session. Create a **service account** (role `Assistant` to read and write, `Reader` to read) and an API key for it (optionally limited to some sources), and give the key as the `X-Api-Key` header. For Claude Code: `claude mcp add --transport http mnemarr https://<host>/mcp --header "X-Api-Key: <key>"`. What a key sees and does is what it may through the REST API.

| Setting | Default | Meaning |
|---|---|---|
| `MNEMARR__MCP__WRITETOOLS` | `false` | Whether the tools that write notes (`create_note`, `update_note`, `append_to_note`, `rename_note`, `delete_note`) are offered. Off by default, and an environment setting on purpose: neither the database nor the API can turn it on. A tool is only offered to a role that holds its permission, and a source that is read-only refuses writes whatever this says. |
| `MNEMARR__MCP__SOURCETOOLS` | `false` | Whether the tools that look after sources (`create_source`, `update_source`, `delete_source`, `index_source`, `sync_source`) are offered, **as well as** `WRITETOOLS` being on. Apart from the notes' switch on purpose: an operator who let assistants write notes has not let them declare or forget sources. Every key whose role holds `sources.manage` (Operator) gets them, not only the chat: turn it on only for assistants you trust with that. |

Tools that read: `list_sources`, `search`, `list_notes`, `read_note` (gives the `version` to send back with `update_note`), `list_tags`. A tool that asks: `request_collector_enrolment` (role `Assistant` or `Admin`) opens a request to add a machine and returns a one-time code and the command to run; it is of no use until a person who manages the collectors approves it in the interface.

## Machines and collectors (`Collectors`, `Ingest`)

A key of a collector belongs to one machine ([ADR 0027](adr/0027-machines-join-with-a-code-hold-a-key-bound-to-their-name-and-say-they-are-alive.md)): it is made when the machine joins with a one-time code, and it can send for that machine only.

| Setting | Default | Meaning |
|---|---|---|
| `MNEMARR__INGEST__REQUIREENROLMENT` | `true` | Whether a key may send only for the machine it was enrolled for. Turn it off only for a sandbox, or for the day before every machine is enrolled: a hand-made `Collector` account can then write under any machine name. |
| `MNEMARR__COLLECTORS__DOWNLOADFOLDER` | none | A folder with the programs a machine installs, one file per system: `mnemarr-collector-windows-x86_64.exe`, `mnemarr-collector-linux-x86_64`, `mnemarr-collector-macos-aarch64`. Mnemarr serves them (and no other file), with their checksum, to the machines that join, and gives a line to install for each system it has. The image carries the Windows and Linux programs of its own commit in `/collector-programs`, and this setting points there by default; set it to serve other programs, or to nothing to serve none. |
| `MNEMARR__COLLECTORS__UPDATEFOLDER` | `updates` in the folder above | The folder of the installer and the updates of the collector that the installer's packaging made ([ADR 0032](adr/0032-the-collector-is-installed-by-an-installer-and-updates-itself-from-the-mnemarr-it-sends-to.md)): `releases.win.json` (the feed), `MnemarrCollector-<version>-full.nupkg` (the packages) and `MnemarrCollector-win-Setup.exe`. Mnemarr serves them (and no other file) under `/api/v1/ingest/updates/` and `/api/v1/ingest/installer/<system>`, so that an installed collector asks it for a newer version and a person can download the installer. The image carries it; without it, no installer is offered. |
| `MNEMARR__COLLECTORS__SERVEUPDATES` | `true` | Whether this Mnemarr serves the installer and the updates of the collector it carries. Set it to `false` to serve none: no installer is offered, and a collector linked to it never takes an update from it (an update is code that runs on the computer: a Mnemarr that you would not trust to run it can be told not to offer it). |
| `MNEMARR__COLLECTORS__PUBLICURL` | the address of the request | The address the lines and installers tell machines to reach Mnemarr at, such as `https://mnemarr.homelab.example`. Set it behind a proxy that does not pass the original host. |
| `MNEMARR__COLLECTORS__ONLINEWITHINMINUTES` | `5` | A machine heard from within this many minutes is online (1 to 10080). |
| `MNEMARR__COLLECTORS__SILENCEAFTERMINUTES` | `30` | An `always-on` machine that has not been heard from for this long degrades `/health/ready` (reason `collector-silent`); one that reports an error does too (`collector-failing`). An `intermittent` machine never does. At least the previous setting. |

An existing hand-made `Collector` account stops working at the upgrade unless the first setting is turned off: enrol the machine instead (`mnemarr-collector enroll`).

## Backup (`Backup`)

Mnemarr keeps a consistent copy of its database in the `backup` folder of the data folder, readable by its owner only ([ADR 0029](adr/0029-mnemarr-runs-for-real-programs-in-the-image-snapshots-of-the-database-and-brakes-on-the-public-endpoints.md)); what copies the data folder away is yours (see [the runbook](runbook.md)).

| Variable | Default | Meaning |
|---|---|---|
| `MNEMARR__BACKUP__ENABLED` | `true` | Whether snapshots are taken, and whether a whole copy of the database is kept before a migration. |
| `MNEMARR__BACKUP__EVERYHOURS` | `24` | A snapshot is taken when the newest is this old (1 to 168). |
| `MNEMARR__BACKUP__KEEP` | `7` | How many are kept; older ones are deleted (1 to 60). |

A snapshot holds what the database held that day: **what you delete from Mnemarr stays readable in the snapshots** until they are replaced (`Keep` × `EveryHours`, a week by default), in the **last three copies made before a migration** (kept whatever their age), and in whatever your backup tool keeps beyond that. Keep this in mind for content you must be able to erase.

When the last snapshot failed, or none has been taken for more than twice the interval, `/health/ready` says **degraded** with the reason `backup-failing` or `backup-late` (it stays 200: the application works).

## Consolidation into the memory vault (`Consolidation`)

Turns the conversations into short notes of the memory vault ([ADR 0036](adr/0036-the-consolidation-reads-new-turns-and-writes-notes-of-the-memory-vault-as-an-account-of-its-own.md)). **Off by default**: turning it on sends the conversations (already masked) to the model of the assistant runner and spends your subscription, a little for each conversation. It needs `MNEMARR__AGENT__URL` (the runner) and a memory vault whose mode is **ReadWrite** (the Sources page; a vault that is read-only is only read).

| Variable | Default | Meaning |
|---|---|---|
| `MNEMARR__CONSOLIDATION__ENABLED` | `false` | Whether conversations are turned into notes. |
| `MNEMARR__CONSOLIDATION__MODEL` | `haiku` | The model of the job: a light one is enough to write notes. |
| `MNEMARR__CONSOLIDATION__IDLEMINUTES` | `5` | A conversation is consolidated once nothing was added to it for this long (1 to 1440). The thread that a new session is told (ADR 0038) lags the end of a conversation by this time, a pass and the run. |
| `MNEMARR__CONSOLIDATION__PASSSECONDS` | `60` | How often the job looks for conversations to consolidate. |
| `MNEMARR__CONSOLIDATION__PERPASS` | `3` | Conversations consolidated by one pass (1 to 20). |
| `MNEMARR__CONSOLIDATION__PERDAY` | `60` | Times the model is asked in a day, UTC (1 to 1000): the cost of a day is bounded (a conversation read in several runs, or tried again, costs each time). |
| `MNEMARR__CONSOLIDATION__AGENT` | empty | The agent of the runner that does the job (`claude-code`, `openai`); empty: the first that can run. The default model is a Claude one: with another agent, set `MODEL` too. |
| `MNEMARR__CONSOLIDATION__MAXCHARS` | `40000` | What one run reads of a conversation (2000 to 100000); a long conversation is read in several runs. |
| `MNEMARR__CONSOLIDATION__MAXTRIES` | `3` | A conversation that failed this often is left alone until it has grown by 200 lines. |
| `MNEMARR__CONSOLIDATION__TIMEOUTMINUTES` | `10` | A run that lasts longer is stopped. |

The status is on the Sources page (and `GET /api/v1/consolidation`); the readiness probe says `consolidation-*` (degraded) when it is on and cannot work.

## Titles of conversations (`Titles`)

A conversation is titled by its first sentence, or by what a person types. A model can write a better title and a short summary ([ADR 0028](adr/0028-the-title-of-a-conversation-is-its-own-written-by-a-model-or-typed-by-a-person.md)). **It is off by default, and turning it on sends the start and the end of conversations (the secrets already masked) to the provider of the model.**

| Setting | Default | Meaning |
|---|---|---|
| `MNEMARR__TITLES__ENABLED` | `false` | Whether a model writes titles and summaries, in the background and on request. |
| `MNEMARR__TITLES__PROVIDER` | `Anthropic` | Who writes: `Anthropic` (the Messages API, with an API key), `OpenAI` (any API that speaks OpenAI's chat completions: OpenAI, Ollama, LM Studio, vLLM, OpenRouter…) or `Agent` (a companion; see [Using another model](#using-another-model-for-titles)). |
| `MNEMARR__TITLES__APIKEYFILE` | none | Providers `Anthropic` (needed) and `OpenAI` (optional: a local server has no key): a file that holds the API key (a mounted secret). |
| `MNEMARR__TITLES__AGENTURL` | none | Provider `Agent`: the address of the companion, on the host or its network (`http://host:8490`). Needed then. |
| `MNEMARR__TITLES__AGENTSECRETFILE` | none | Provider `Agent`: a file that holds the secret shared with the companion. Needed then. |
| `MNEMARR__TITLES__MODEL` | `claude-haiku-5-5` | The model, in the words of the provider (`gpt-5-mini`, `llama3`, or an alias such as `haiku` for an agent). |
| `MNEMARR__TITLES__ENDPOINT` | Anthropic's | The address of the API (providers `Anthropic`, where it defaults to Anthropic's, and `OpenAI`, where it is needed and is the base with its version path: `https://api.openai.com/v1`, `https://openrouter.ai/api/v1`, `http://localhost:11434/v1` for a local model; an address with no path, such as `http://localhost:11434`, is given `/v1`). A key is only sent over HTTPS, or to this host. |
| `MNEMARR__TITLES__IDLEMINUTES` | `10` | A conversation is titled once nothing has been added to it for this long. |
| `MNEMARR__TITLES__PERPASS` | `5` | How many conversations a pass titles (a pass is every `PASSSECONDS`, 60): the cost is bounded. |
| `MNEMARR__TITLES__MAXTRIES` | `3` | A conversation the model fails to title this many times keeps its first sentence. |
| `MNEMARR__TITLES__MAXCHARS` | `6000` | How much of a conversation is sent, in characters. |

### Using another model for titles

Titles are written through one small interface, `ITitleWriter` (a transcript in, a title and a summary out); the name in `Titles:Provider` selects the `ITitleProvider` that builds it. Three come with Mnemarr. To use something else, pick the way that fits:

- **An OpenAI-compatible API, or a local model**: `MNEMARR__TITLES__PROVIDER=OpenAI`, `ENDPOINT` to its base address (with `/v1`) and `MODEL` to a model it serves. Nothing to write.
- **Any other tool or system, without touching Mnemarr**: run a small companion that speaks the *agent protocol*, and set `PROVIDER=Agent`, `AGENTURL` and `AGENTSECRETFILE`. The protocol is one call: `POST {AgentUrl}/title` with `Authorization: Bearer <secret>` and `{"model", "system", "transcript"}`; the answer is `200 {"text": "..."}` where the text holds one JSON object `{"title": "...", "summary": "..."}` (a fence around it is tolerated). [tools/title-agent](../tools/title-agent/main.mjs) is an example: it runs the Claude Code CLI with an account's own login, so that the subscription is used and not an API.
- **A provider in the code** (a fork, or a contribution): write a class that implements `ITitleProvider` (a name, the check of its settings, and the creation of an `ITitleWriter`) and register it with `services.AddSingleton<ITitleProvider, MyProvider>()`. [TitleProviders.cs](../src/Mnemarr.Core/Titles/TitleProviders.cs) holds the three built-in ones.

Whatever the provider, turning titles on sends the start and the end of conversations (already masked) to it: with a local model, that text stays on your network.

A person who holds `conversations.write` (`Admin`) changes a title, or asks the model to write it again, on the page of the note; the Machines page counts the titles and queues the first sentences for the model. What a person typed is never replaced by a model's.

## Notifications (`Notifications`)

Mnemarr can write to one person when a machine asks for them: a satellite whose session ended and that needs a sign-in ([ADR 0042](adr/0042-mnemarr-writes-to-a-person-through-a-swappable-notification-sender.md)). **It is off by default.** The mail says the name of the machine, a sentence of Mnemarr's own and where to look (the Machines page of this Mnemarr, from `Collectors:PublicUrl`); it never holds a token, a key, a conversation or an address that a machine gave.

| Setting | Default | Meaning |
|---|---|---|
| `MNEMARR__NOTIFICATIONS__ENABLED` | `false` | Whether Mnemarr writes. |
| `MNEMARR__NOTIFICATIONS__PROVIDER` | `Smtp` | The channel: `Smtp`. See [Another channel](#another-channel-for-notifications). |
| `MNEMARR__NOTIFICATIONS__RECIPIENT` | none | The address the mails go to. Needed then, and the only one ever written to. |
| `MNEMARR__NOTIFICATIONS__FROM` | none | The address the mails come from (often the mailbox that sends them). Needed then. |
| `MNEMARR__NOTIFICATIONS__SMTP__HOST` | none | The mail server (`smtp.gmail.com` for a Google account). Needed then. |
| `MNEMARR__NOTIFICATIONS__SMTP__PORT` | `587` | The submission port, upgraded with STARTTLS. Port 465 (implicit TLS) is not supported. |
| `MNEMARR__NOTIFICATIONS__SMTP__USERNAME` | none | The sign-in of the mailbox (for Google, the whole address). Goes with `PASSWORDFILE`. |
| `MNEMARR__NOTIFICATIONS__SMTP__PASSWORDFILE` | none | A file that holds the password (a mounted secret). For Google, an **application password** (the account needs two-step verification; create it under Security, then App passwords), not the account password. |
| `MNEMARR__NOTIFICATIONS__SMTP__STARTTLS` | `true` | Whether the connection is made secure. Off only without a password (a catcher on a test machine): a password is never sent in clear. |
| `MNEMARR__NOTIFICATIONS__SMTP__TIMEOUTSECONDS` | `30` | How long to wait for the server before giving a mail up (it is tried again after the pause). |
| `MNEMARR__NOTIFICATIONS__REMINDERHOURS` | `24` | Hours between two reminders while a machine still asks. |
| `MNEMARR__NOTIFICATIONS__MAXREMINDERS` | `3` | How many reminders follow the first mail of an episode. |
| `MNEMARR__NOTIFICATIONS__MAXPERDAY` | `10` | The most mails tried in a day, all machines together. |
| `MNEMARR__NOTIFICATIONS__MAXPERMACHINEPERDAY` | `3` | The most mails tried for one machine in a day, sent or not. |
| `MNEMARR__NOTIFICATIONS__RETRYMINUTES` | `15` | The pause after a mail that could not be sent. |
| `MNEMARR__NOTIFICATIONS__PASSSECONDS` | `60` | How often Mnemarr looks for mails that are due (10 to 3600). |

One mail leaves when an episode begins, a reminder at most once a day while it lasts, nothing when it ends; the two daily caps hold whatever a machine does. Only a machine that is enrolled and not revoked is mailed. An administrator sees where mails go, and sends a test mail, on the Machines page (`POST /api/v1/notifications/test`). `/health/ready` is degraded with `notifications-secret` (the password file cannot be read) or `notifications-failing` (the last mail did not leave); it is never unhealthy.

### Another channel for notifications

A channel is one small interface, `INotificationSender` (a recipient, a subject and a text in; delivered or a short reason out); the name in `Notifications:Provider` selects the `INotificationProvider` that checks its settings and builds the sender. To add one (a chat message, a push, another mail service), write a class that implements `INotificationProvider` and register it with `services.AddSingleton<INotificationProvider, MyProvider>()`. [SmtpNotificationSender.cs](../src/Mnemarr.Core/Notifications/SmtpNotificationSender.cs) is the built-in example; the policy (when, how often, the caps) is not the provider's.

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
| The start-of-session hook of an assistant ([connect Claude](user/connect-claude.md)) | `Assistant` for the thread and the end of the last conversation (`Reader` gives the thread only). With the writing tools on, an `Assistant` key can also write notes: make a key for the hook of its own |
| A collector that sends conversations (later) | `Collector` |

Send the key as `X-Api-Key: <key>` or `Authorization: Bearer <key>`. It is shown **once**, when it is created, and only a hash of it is kept: if it is lost, create another and revoke the old one. A key can be limited to some sources and can expire. `/health/live` and `/health/ready` need no key.
