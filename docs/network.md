# Network, ports and what is allowed

> Who connects to whom, on which port, in which direction, and with what proof of identity. Read it before opening a port or writing a firewall rule. The names below are examples (`mnemarr.homelab.example`).

## In one table

| From | To | Port | Direction (seen from the first) | What for | Proof of identity |
|---|---|---|---|---|---|
| **A collector** (a workstation or a server) | Mnemarr | 443 (HTTPS) | **outbound** | `POST /api/v1/ingest/enrol`, `/ingest/pair` and `/ingest/pair/poll`, `/ingest/conversations`, `/ingest/positions`, `/ingest/heartbeat` | the key of the machine (`X-Api-Key`); for `enrol`, a one-time code; for `pair`, a secret of its own and the approval of a person |
| An assistant | Mnemarr | 443 (HTTPS) | outbound | `POST /mcp` | an API key of a service account |
| A person | Mnemarr | 443 (HTTPS) | outbound | the interface and the API | a password (a cookie session), usually behind the proxy's single sign-on |
| Mnemarr | a Git host | 443 (HTTPS) | outbound | the Git sources | a named token; only the hosts in `Git:Hosts` |
| A proxy in front of Mnemarr | Mnemarr | 8383 (HTTP) | inbound to the container | everything above, once the proxy ended TLS | none: this port is not published to the outside |

**Nothing listens on the network on a machine that runs a collector.** The collector needs no inbound rule, on Windows or anywhere else; it only makes outbound HTTPS calls. A firewall that lets a workstation browse the web already lets it collect. The one exception stays on the machine: when a person chooses *Link to a Mnemarr…* from the icon, the collector listens for up to thirty minutes on the **loopback address only** (`127.0.0.1`, a random port, under a secret path), to show a page in the person's own browser; nothing outside the machine can reach it.

## What a collector may reach, and how

- **One address for each Mnemarr it sends to** (ADR 0030): the one given to `link` or `enroll` (`--server`), over HTTPS. A plain `http://` address is accepted only for this machine itself (`localhost`), because the key is sent in a header at every call.
- **Paths**, all under `/api/v1/ingest/`: `install.ps1`, `install.sh` and `download/<system>` (anonymous: the installer and the program, which hold no secret; with a code and a machine the program carries them), `enrol` (anonymous, guarded by the code), `pair` and `pair/poll` (anonymous, guarded by a secret the machine made and by the approval of a person), `updates/<file>` and `installer/<system>` (anonymous: the feed of updates of the collector, its packages and its installer, the same for everybody, with no secret; only the files the installer makes are served, four at a time), `conversations` and `heartbeat` (guarded by the key). A key that belongs to a machine can send for that machine and for no other ([ADR 0027](adr/0027-machines-join-with-a-code-hold-a-key-bound-to-their-name-and-say-they-are-alive.md)).
- **Updates are taken over HTTPS, or from this very computer** ([ADR 0032](adr/0032-the-collector-is-installed-by-an-installer-and-updates-itself-from-the-mnemarr-it-sends-to.md)): an update is code that will run, so a plain `http://` address on a network never carries one, even when it is allowed for sending conversations.
- **A redirect is not followed by the collector** (with one exception: the requests for updates are made by Velopack's own client, which follows redirects; what it takes is checked against the checksum in the feed, which came over HTTPS, and a package that does not match is refused): a server that answers with a redirect cannot make the collector carry its key somewhere else.
- **A proxy** is used when the usual environment variables are set (`HTTPS_PROXY`, `ALL_PROXY`, `NO_PROXY`). A proxy that opens the TLS traffic and re-signs it breaks the verification of the certificate: allow Mnemarr's host through, or give the machine the certificate of the proxy.
- **The clock** of the machine matters to TLS: a machine whose time is far off cannot verify a certificate.

## What to publish, and what not to

[Decision D-27](adr/0010-external-exposure-and-delegated-backups.md): Mnemarr authenticates every call of the API and of MCP itself, so the endpoints that collectors and assistants use **may be published outside the proxy's single sign-on**, on a host name of their own, while the interface stays behind it.

| Path | Publish outside the single sign-on? | Why |
|---|---|---|
| `/api/v1/ingest/*` | yes | a collector has no browser; the key is its proof (the installer, the program, `enrol` and `pair` need none: the code or the secret is what is secret, and a person's approval is needed) |
| `/mcp` | yes | an assistant has no browser; the key is its proof |
| `/api/v1/context` | yes, when the hook of an assistant reaches Mnemarr on that host name | the hook ([connect Claude](user/connect-claude.md)) is a program with a read key, not a browser; it makes this one `GET` at the start of a session |
| everything else (`/`, `/api/v1/*` other than `ingest` and `context`, `/health/*`) | **no**: behind the single sign-on | the interface and the administration; the page `/link`, where a person approves a computer that asked to be linked, is part of it: it is opened in the browser of the person, who is signed in. The address of that page is built from `Collectors:PublicUrl` (the address given to collectors), so that address must reach the interface for a person: a Mnemarr whose collectors use a separate host name that publishes only the ingest prefix needs its own setting for it (not built: [ROADMAP](ROADMAP.md)) |

Rules of the proxy for those prefixes (`/api/v1/ingest/`, `/mcp` and `/api/v1/context`): allow-list them by prefix (nothing else on that host name), forward the `X-Api-Key` and `X-Mnemarr-Request` headers, limit the size of a body (a batch is at most 8 MB), keep the response buffering of the proxy on (a client that reads slowly then does not hold one of the program writes of Mnemarr), and **limit the requests per address**: the proxy sees the real address of a caller, Mnemarr does not read forwarded addresses (behind two proxies it would trust a header anyone can write). With nginx, in the `http` context `limit_req_zone $binary_remote_addr zone=mnemarr:10m rate=10r/s;` and in each of the locations `limit_req zone=mnemarr burst=40 nodelay;`. Mnemarr itself counts the wrong enrolment codes (ten a minute for the whole server, then a 429), throttles the sign-in, and writes at most four programs at the same time (a 429 with `Retry-After` for the others) ([ADR 0029](adr/0029-mnemarr-runs-for-real-programs-in-the-image-snapshots-of-the-database-and-brakes-on-the-public-endpoints.md)).

## Try it on a home network without TLS

A Mnemarr served over plain HTTP (a developer's sandbox, a first try at home) shows a warning when a machine is added, and the line it gives carries `--allow-insecure-http`: the collector then accepts the `http://` address, and the key travels without encryption on your network. That is a choice of the person, said in the page; leave it for a network you trust. For anything else serve Mnemarr over HTTPS, and the warning and the flag disappear. An SSH tunnel (`ssh -N -L 8384:127.0.0.1:8384 user@mnemarr-host`, then `--server http://localhost:8384`) keeps the key encrypted without TLS, for those who are at ease with it.

## Not decided here

How an installation publishes these prefixes (a reverse proxy, a host name, the certificate) is the operator's own infrastructure as code, in a repository of theirs. This page says what must be allowed; it does not name anybody's hosts. [The runbook](runbook.md) says what to do once it runs.
