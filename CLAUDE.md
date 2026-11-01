# Mnemarr

> Self-hosted long-term memory for people and AI assistants, in the spirit of the *arr suite.

**Project stage: architecture decided, foundation vertical next.** The first interview is done: the decisions are in [docs/SPEC.md](docs/SPEC.md) and [docs/adr/](docs/adr/README.md) (see [docs/VISION.md](docs/VISION.md) for the original need). The next step is the foundation vertical (pipeline, quality chain and a minimal walking skeleton, ADR 0008), then the first functional vertical. Two spikes come before the search vertical: the vector store implementation and the embeddings model (ADR 0009).

This file is read automatically by Claude Code. It applies to every task in this repository.

## Read before any task

| Document | When |
|---|---|
| [docs/VISION.md](docs/VISION.md) | Always: why this exists, what it must do, open questions |
| [docs/SPEC.md](docs/SPEC.md) | Always: frozen decisions `D-xx`, features `F-xx`, open questions `Q-xx` (draft, interview in progress); decision records are in [docs/adr/](docs/adr/README.md) |

## Planned tech stack (not yet scaffolded)

Same as Portarr, so the three products share conventions:

- **Backend**: .NET 10 (LTS), C# 14, ASP.NET Core, FluentValidation
- **Frontend**: React, TypeScript (strict), Vite, TanStack Query, CSS Modules
- **Tests**: xUnit v3 + NSubstitute + AwesomeAssertions, Vitest + Testing Library, Playwright
- **CI/CD**: GitHub Actions, single multi-arch container image on GHCR
- Storage: SQLite, with vectors and full-text search inside it, behind interfaces; embeddings computed locally (ADR 0009). The collector agent is Rust (ADR 0005); the assistant runner reuses Portarr's design (ADR 0007).

## Non-negotiable rules

1. **English only** for code, comments, documentation, commits, issues and PRs. The chat with the maintainer is in French; nothing French is committed except clearly marked quotes in `docs/VISION.md`.
2. **Public repo, generic product.** Never commit real infrastructure values (domain names, IPs, host names, personal project names, file paths of a personal setup, tokens), nor real memory content, in code, tests, fixtures, docs or commit messages. Use examples (`homelab.example`, `server-1`, `10.0.0.10`).
3. ***arr patterns first.** A single container, API first (the UI consumes the REST API), settings as `[FieldDefinition]`-style providers, Sonarr-like screens. The UI will follow the same design rules as Portarr; read Portarr's `docs/DESIGN.md` before writing any UI code.
4. **The constellation** (below): Mnemarr remembers; it does not observe the fleet (Portarr) and does not act on it (Orchestrarr). Integrate through APIs and MCP, never by duplicating the other products.
5. **Privacy by default.** Memories can hold sensitive content. Nothing leaves the host unless the operator configures it; secrets are never stored in memories nor returned by the API; any external embeddings API is opt-in and documented.
6. **Sandbox first, PR after acceptance.** Develop each vertical in a local hot-reload sandbox (no commit, push, PR, CI or image). Give the maintainer the URL and what to try, iterate live, and open the PR only once the vertical is accepted. Then complete tests, lint, clean commits.
7. **One vertical = one PR**, with its tests and documentation.
8. **Ask, but build.** When gathering needs, ask open questions (never multiple-choice forms). Once the need is clear, code the vertical and ask only when it truly requires an answer.
9. **Never** commit secrets. Use `dotnet user-secrets` locally and GitHub Secrets in CI.
10. **Decisions** get an ADR in `docs/adr/NNNN-title.md` and a `D-xx` row in the spec.

## The constellation

Three products, each with one job, plugged into each other through their APIs and MCP servers:

| Product | Job | Relation to Mnemarr |
|---|---|---|
| **Portarr** | See and describe the fleet | Mnemarr registers in Portarr as a companion; Portarr's MCP server points assistants to Mnemarr |
| **Mnemarr** (this repo) | Remember | — |
| **Orchestrarr** | Act: orchestrate agents that fix, update and verify | Reads and writes memories (runbooks, outcomes of past actions) |

## Git workflow

Branches `main` (stable releases) and `develop` (integration) are protected by GitHub Rulesets: **no direct push, ever**. Everything goes through pull requests.

```text
1. git checkout develop && git checkout -b feature/<issue>-<short-description>
2. Commit with Conventional Commits
3. git push origin feature/<issue>-<short-description>
4. gh pr create --base develop
5. CI green + review -> squash merge into develop
6. When develop is stable -> release PR develop -> main
```

Prefixes: `feature/`, `fix/`, `chore/`, `docs/`. Conventional Commits: `feat(api): ...`, `fix(search): ...`, `docs(spec): ...`, `chore(ci): ...`.

The remote does not exist until the `repo/add-mnemarr-orchestratarr` pull request of `github-template` is merged and applied; see the README "Bootstrap" section.
