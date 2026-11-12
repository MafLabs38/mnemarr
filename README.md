# Mnemarr

> Remember. A self-hosted long-term memory for people and AI assistants, in the spirit of the *arr suite.

**Status: before 1.0.** It is used every day by its author and it works end to end: conversations are collected from every machine, consolidated into notes, searched by meaning and by text, and given back to your assistants (MCP, and a hook that resumes the thread of your last session). Below 1.0 a minor version can still change settings or the API; the release notes say so.

Mnemarr stores what assistants and people learn, and finds it again by meaning (semantic search) and by text. It exposes a REST API and an MCP server so any assistant can read and write memories. Conversations are collected from every machine by a small agent. The consolidated memory is written as Markdown notes in a Git repository you can read in Obsidian. It is a companion of [Portarr](https://github.com/MafLabs38/portarr) (see and describe your fleet) and Orchestrarr (act on it).

## Documentation

**Use it** (start here once Mnemarr runs):

| Guide | Content |
|---|---|
| [Connect Claude to Mnemarr](docs/user/connect-claude.md) | On a computer where you use Claude Code: send its conversations, let Claude search the memory (MCP), and let a new chat resume where the last one stopped (hook) |
| [docs/collector.md](docs/collector.md) | The collector: joining a machine, the Windows installer, updates, what it reads |
| [docs/chat.md](docs/chat.md) | The chat panel of the interface |

**Run it:**

| Document | Content |
|---|---|
| [deploy/](deploy/README.md) | Generic deployment examples (Docker, Kubernetes) |
| [docs/configuration.md](docs/configuration.md) | Every setting, accounts and keys, MCP |
| [docs/embedding-server.md](docs/embedding-server.md) | Compute the vectors of the search by meaning on an NVIDIA GPU, in a small separate container |
| [docs/network.md](docs/network.md) | Ports, directions, what to publish outside the single sign-on, firewalls and proxies |
| [docs/runbook.md](docs/runbook.md) | Operating it: what to watch, what to do when it is degraded |
| [docs/releasing.md](docs/releasing.md) | How versions are made and followed |
| [SECURITY.md](SECURITY.md) | Reporting a vulnerability, security model |

**Understand it:**

| Document | Content |
|---|---|
| [docs/VISION.md](docs/VISION.md) | Why, what it must do, how it plugs into Portarr and assistants |
| [docs/SPEC.md](docs/SPEC.md) | Decisions, architecture, data model, features, security, open questions |
| [docs/adr/](docs/adr/README.md) | Architecture decision records |
| [docs/ROADMAP.md](docs/ROADMAP.md) | The verticals, built and to come |
| [docs/conversation-formats.md](docs/conversation-formats.md) | Where Claude Code, Codex, Copilot and Gemini keep their conversations |

**Contribute:**

| Document | Content |
|---|---|
| [CONTRIBUTING.md](CONTRIBUTING.md) | Workflow, conventions, development setup |
| [docs/TESTING.md](docs/TESTING.md) | Testing strategy and CI gates |
| [docs/DESIGN.md](docs/DESIGN.md) | Interface guidelines (Portarr's, plus what is specific to Mnemarr) |
| [CLAUDE.md](CLAUDE.md) | Rules for AI assistants working in this repository |

## What is in the repository

```text
src/            .NET solution: Core, Api.V1, Host (the container's entry point) and their tests
frontend/       React app (Vite) with the application shell, built into the image
collector/      the collector agent (Rust), for Windows and Linux
tests/e2e/      Playwright tests and the stack they run against
deploy/         deployment examples
docs/           specification, decisions, testing strategy
.claude/        the four review agents and the review skill
```

## Run it

```bash
docker build -t mnemarr:dev .
secrets="$(mktemp -d)"                               # a folder only you can enter
openssl rand -base64 24 > "$secrets/admin-password"  # the first administrator's password: a file, never a variable
chmod 644 "$secrets/admin-password"                  # the container's user must read it; the folder keeps everybody else out
docker run --rm -p 127.0.0.1:8383:8383 --tmpfs /config:uid=1654,gid=1654 \
  -v "$secrets/admin-password:/run/admin-password:ro" \
  -e MNEMARR__AUTH__ADMINUSER=admin -e MNEMARR__AUTH__ADMINPASSWORDFILE=/run/admin-password \
  -e MNEMARR__AUTH__COOKIESECURITY=SameAsRequest mnemarr:dev   # SameAsRequest: this is plain HTTP; behind TLS leave the default
# http://localhost:8383 (published on this machine only: the cookie is not Secure here): sign in as admin, with the password of `cat "$secrets/admin-password"` (health: /health/live and /health/ready)
# afterwards: rm -r "$secrets"
```

See [CONTRIBUTING.md](CONTRIBUTING.md#development-setup) for the development setup.

## License

Mnemarr is free software under the **GNU General Public License, version 3 or (at your option) any later version** (GPL-3.0-or-later), like Portarr and Orchestrarr. The text is in [LICENSE](LICENSE); every package of this repository declares it (`GPL-3.0-or-later`).
