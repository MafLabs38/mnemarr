# Network, ports and what is allowed

> Who connects to whom, on which port, in which direction, and with what proof of identity. Read it before opening a port or writing a firewall rule. The names below are examples (`mnemarr.homelab.example`).

## In one table

| From | To | Port | Direction (seen from the first) | What for | Proof of identity |
|---|---|---|---|---|---|
| **A collector** (a workstation or a server) | Mnemarr | 443 (HTTPS) | **outbound** | `POST /api/v1/ingest/enrol`, `/ingest/conversations`, `/ingest/heartbeat` | the key of the machine (`X-Api-Key`); for `enrol` alone, a one-time code |
| An assistant | Mnemarr | 443 (HTTPS) | outbound | `POST /mcp` | an API key of a service account |
| A person | Mnemarr | 443 (HTTPS) | outbound | the interface and the API | a password (a cookie session), usually behind the proxy's single sign-on |
| Mnemarr | a Git host | 443 (HTTPS) | outbound | the Git sources | a named token; only the hosts in `Git:Hosts` |
| A proxy in front of Mnemarr | Mnemarr | 8383 (HTTP) | inbound to the container | everything above, once the proxy ended TLS | none: this port is not published to the outside |

**Nothing listens on a machine that runs a collector.** The collector opens no port and needs no inbound rule, on Windows or anywhere else; it only makes outbound HTTPS calls. A firewall that lets a workstation browse the web already lets it collect.

## What a collector may reach, and how

- **One address**: the one given to `enroll` (`--server`), over HTTPS. A plain `http://` address is accepted only for this machine itself (`localhost`), because the key is sent in a header at every call.
- **Paths**, all under `/api/v1/ingest/`: `install.ps1`, `install.sh` and `download/<system>` (anonymous: the installer and the program, which hold no secret; with a code and a machine the program carries them), `enrol` (anonymous, guarded by the code), `conversations` and `heartbeat` (guarded by the key). A key that belongs to a machine can send for that machine and for no other ([ADR 0027](adr/0027-machines-join-with-a-code-hold-a-key-bound-to-their-name-and-say-they-are-alive.md)).
- **No redirect is followed**: a server that answers with a redirect cannot make the collector carry its key somewhere else.
- **A proxy** is used when the usual environment variables are set (`HTTPS_PROXY`, `ALL_PROXY`, `NO_PROXY`). A proxy that opens the TLS traffic and re-signs it breaks the verification of the certificate: allow Mnemarr's host through, or give the machine the certificate of the proxy.
- **The clock** of the machine matters to TLS: a machine whose time is far off cannot verify a certificate.

## What to publish, and what not to

[Decision D-27](adr/0010-external-exposure-and-delegated-backups.md): Mnemarr authenticates every call of the API and of MCP itself, so the endpoints that collectors and assistants use **may be published outside the proxy's single sign-on**, on a host name of their own, while the interface stays behind it.

| Path | Publish outside the single sign-on? | Why |
|---|---|---|
| `/api/v1/ingest/*` | yes | a collector has no browser; the key is its proof (the installer, the program and `enrol` need none: the code is what is secret, and it needs a person's approval) |
| `/mcp` | yes | an assistant has no browser; the key is its proof |
| everything else (`/`, `/api/v1/*`, `/health/*`) | **no**: behind the single sign-on | the interface and the administration |

Rules of the proxy for those two prefixes: allow-list them by prefix (nothing else on that host name), forward the `X-Api-Key` and `X-Mnemarr-Request` headers, limit the size of a body (a batch is at most 8 MB), and leave the rate limits to Mnemarr for now. Mnemarr counts the wrong enrolment codes itself: ten a minute for the whole server, then a 429.

## Try it on a home network without TLS

A Mnemarr served over plain HTTP (a developer's sandbox, a first try at home) shows a warning when a machine is added, and the line it gives carries `--allow-insecure-http`: the collector then accepts the `http://` address, and the key travels without encryption on your network. That is a choice of the person, said in the page; leave it for a network you trust. For anything else serve Mnemarr over HTTPS, and the warning and the flag disappear. An SSH tunnel (`ssh -N -L 8384:127.0.0.1:8384 user@mnemarr-host`, then `--server http://localhost:8384`) keeps the key encrypted without TLS, for those who are at ease with it.

## Not decided here

How the home installation publishes these two prefixes (a reverse proxy, a host name, the certificate) is the work of the vertical that runs Mnemarr for real, in the operator's own infrastructure repository. This page says what must be allowed; it does not name anybody's hosts.
