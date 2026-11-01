# Mnemarr

> Remember. A self-hosted long-term memory for people and AI assistants, in the spirit of the *arr suite.

**Status: foundation.** The architecture is decided ([docs/SPEC.md](docs/SPEC.md)); the first code is a walking skeleton with the full quality chain. There is no memory feature yet.

Mnemarr stores what assistants and people learn, and finds it again by meaning (semantic search) and by text. It exposes a REST API and an MCP server so any assistant can read and write memories. Conversations are collected from every machine by a small agent. The consolidated memory is written as Markdown notes in a Git repository you can read in Obsidian. It is a companion of [Portarr](https://github.com/MafLabs38/portarr) (see and describe your fleet) and Orchestrarr (act on it).

## Documentation

| Document | Content |
|---|---|
| [docs/VISION.md](docs/VISION.md) | Why, what it must do, how it plugs into Portarr and assistants |
| [docs/SPEC.md](docs/SPEC.md) | Decisions, architecture, data model, features, security, open questions |
| [docs/adr/](docs/adr/README.md) | Architecture decision records |
| [docs/DESIGN.md](docs/DESIGN.md) | Interface guidelines (Portarr's, plus what is specific to Mnemarr) |
| [docs/TESTING.md](docs/TESTING.md) | Testing strategy and CI gates |
| [deploy/](deploy/README.md) | Generic deployment examples (Docker, Kubernetes) |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Workflow, conventions, development setup |
| [SECURITY.md](SECURITY.md) | Reporting a vulnerability, security model |
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
docker run --rm -p 8383:8383 --tmpfs /config:uid=1654,gid=1654 mnemarr:dev
# http://localhost:8383 (health: /health/live and /health/ready, API: /openapi/v1.json)
```

See [CONTRIBUTING.md](CONTRIBUTING.md#development-setup) for the development setup.

## License

GPL-3.0, like the *arr apps (to be confirmed in the spec, question Q-20).
