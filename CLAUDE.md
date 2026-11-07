# Mnemarr

> Self-hosted long-term memory for people and AI assistants, in the spirit of the *arr suite.

**Project stage: foundation.** The architecture is decided: the decisions are in [docs/SPEC.md](docs/SPEC.md) and [docs/adr/](docs/adr/README.md) (see [docs/VISION.md](docs/VISION.md) for the original need). The foundation vertical (pipeline, quality chain and a walking skeleton, ADR 0008) comes first, then the first functional vertical. The two spikes that came before the search vertical are done (ADR 0012): `sqlite-vec` for the vectors, Granite 97M R2 for the embeddings.

This file is read automatically by Claude Code. It applies to every task in this repository.

## Read before any task

| Document | When |
|---|---|
| [docs/VISION.md](docs/VISION.md) | Always: why this exists, what it must do, open questions |
| [docs/SPEC.md](docs/SPEC.md) | Always: frozen decisions `D-xx`, features `F-xx`, open questions `Q-xx` (draft, interview in progress); decision records are in [docs/adr/](docs/adr/README.md) |

## Tech stack

Same as Portarr, so the three products share conventions:

- **Backend**: .NET 10 (LTS), C# 14, ASP.NET Core, FluentValidation
- **Frontend**: React, TypeScript (strict), Vite, TanStack Query, CSS Modules
- **Tests**: xUnit v3 + NSubstitute + AwesomeAssertions, Vitest + Testing Library, Playwright
- **CI/CD**: GitHub Actions, single multi-arch container image on GHCR
- Storage: SQLite, with vectors and full-text search inside it, behind interfaces; embeddings computed locally (ADR 0009). The collector agent is Rust (ADR 0005); the assistant runner reuses Portarr's design (ADR 0007).

## Non-negotiable rules

1. **English only** for code, comments, documentation, commits, issues and PRs. The chat with the maintainer is in French; nothing French is committed except clearly marked quotes in `docs/VISION.md`.
2. **Public repo, generic product.** Never commit real infrastructure values (domain names, IPs, host names, personal project names, file paths of a personal setup, tokens), nor real memory content, in code, tests, fixtures, docs or commit messages. Use examples (`homelab.example`, `server-1`, `10.0.0.10`).
3. ***arr patterns first.** A single container, API first (the UI consumes the REST API), settings as `[FieldDefinition]`-style providers, Sonarr-like screens. The UI follows the same design rules as Portarr; read [docs/DESIGN.md](docs/DESIGN.md) (it points to Portarr's) before writing any UI code.
4. **The constellation** (below): Mnemarr remembers; it does not observe the fleet (Portarr) and does not act on it (Orchestrarr). Integrate through APIs and MCP, never by duplicating the other products.
5. **Privacy by default.** Memories can hold sensitive content. Nothing leaves the host unless the operator configures it; secrets are never stored in memories nor returned by the API; any external embeddings API is opt-in and documented.
6. **Sandbox first, PR after acceptance.** Develop each vertical in a local hot-reload sandbox (no commit, push, PR, CI or image). Give the maintainer the URL and what to try, iterate live, and open the PR only once the vertical is accepted. Then complete tests, lint, clean commits. Technical pull requests are not verticals and skip this (rule 11).
7. **One vertical = one PR**, with its tests and documentation.
8. **Ask, but build.** When gathering needs, ask open questions (never multiple-choice forms). Once the need is clear, code the vertical and ask only when it truly requires an answer.
9. **Never** commit secrets. Use `dotnet user-secrets` locally and GitHub Secrets in CI.
10. **Decisions** get an ADR in `docs/adr/NNNN-title.md` and a `D-xx` row in the spec.
11. **Work autonomously; interrupt the maintainer only for a real advance** ([ADR 0014](docs/adr/0014-the-agent-merges-technical-pull-requests.md)). The agent prepares, has the review agents read, and merges technical pull requests (CI, dependencies, tool settings, documentation that records no decision, scaffolding, infrastructure as code, minus the guards and documents the ADR reserves to the maintainer); they are not submitted for validation. A vertical follows rule 6 and reaches the maintainer clean, with its demo, and they merge it. The ADR defines by role what only the maintainer merges (the trust anchor of the ADR: the rules and decisions, what decides whether a gate passes, what runs with a privilege or is built into the image, the settings that guard the project, the configuration of package managers, any new dependency; also the release pull request, and any doubt) and what the agent merges only after reading the whole diff. The agent never bypasses a guard: no admin or ruleset bypass, no merge with a required check failing or pending, no push to a protected branch.

## Quality gates

A vertical is not done, and its pull request is not opened, until all of this holds. Portarr learned it the hard way: code that was not clean from the start cost a day of corrections at the end.

- **Tests with the vertical**, at the levels of [docs/TESTING.md](docs/TESTING.md): unit, integration and, for a user journey, end to end. A bug found by a review gets a failing test first.
- **The SonarQube rules run locally, so a smell fails before the push.** The C# build carries the Sonar analyzer with the 324 rules of the server's profile (`.editorconfig`), warnings are errors; ESLint carries SonarJS, unicorn and jsx-a11y; the collector builds with clippy at `-D warnings`. **Zero warnings**, in every project but the tests.
- SonarQube: 0 bugs, 0 vulnerabilities, 0 critical code smells, coverage at least 70 % overall and at least 80 % on new code (the server job runs once `SONAR_HOST_URL` and `SONAR_TOKEN` exist).
- The four review agents (`review-vertical`), triaged: every finding checked in the code before it is accepted or declined. An architecture verdict of "no" blocks the pull request.
- A documented exception to a Sonar rule names the rule, the files and the reason, in `.editorconfig` or in the code; it is never silent.

SonarQube rules to watch: S2583/S2589 (always true or false conditions), S3168 (async void), S3776 (cognitive complexity at most 15), S1481 (unused variables), S1854 (dead stores), S1066 (mergeable ifs), S2486 (swallowed exceptions), S2325 (a method that could be static).

## Look at your own work before anyone else does

Defects that a person finds by hand are defects the agent could have found first. Getting feedback by yourself is a reflex, not a favour: every vertical is looked at, in a real browser, **by the agent**, before the demo is announced and again before the pull request.

- **Run every gate the CI runs, before pushing**: `dotnet format src/Mnemarr.sln --verify-no-changes`, the .NET tests, `yarn lint`, `yarn stylelint`, `yarn format:check`, `yarn typecheck`, `yarn test`. A gate you did not run locally is a red CI you will read later.
- **Run the end-to-end tests yourself**, in Chromium and in Firefox, against the stack built from the branch: `docker compose -f tests/e2e/stack/docker-compose.yml up -d --build`, then `yarn e2e --workers=1` (stop the stack afterwards with `down -v`), and once the way the CI runs it, in parallel and in both browsers (`yarn e2e --grep @smoke`): journeys that share the stack can race, and a serial run hides it. Do not leave that to the CI.
- **Open the sandbox in a real browser** (Playwright, or any browser you can drive) and use what was built as a person would: click through the journey, read the page, watch the console for errors and Content-Security-Policy violations, look at the screenshot (then delete it). A passing unit test says the code does what it was written to do, not that the screen is right.
- **Try it on real material, not on a toy.** An example written for the demo shows only what its author thought of; a real vault (Mermaid diagrams, relative wikilinks, foldable callouts, `<br>` in tables) finds what the example hides. Keep such material out of the repository: use it in the sandbox only (rule 2).
- A defect found this way is fixed with a test that fails without the fix, like any other.
- **A vertical is validated by feedback the agent got itself, on the real target, before the maintainer is asked to try it.** Automated tests against a fake are not enough when the journey touches a real machine (his PC, the installed collector, a second instance): run that exact journey end to end first (the Windows PC is reachable by SSH, see the global instructions), and hand over only what already worked. Never ask the maintainer to perform a step the agent can trigger itself. If the real run fails, that is a finding to fix, not a demo to give.

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
5. CI green + review -> squash merge into develop, by the agent for a technical pull request and by the maintainer for a vertical (rule 11; the PR title is a Conventional Commit: it decides the version)
6. When develop is stable -> release PR develop -> main, merged with a **merge commit, never a squash** (semantic-release must see the commits; `main` refuses the other methods). The pipeline then tags and publishes the image; deployments pull it: [docs/releasing.md](docs/releasing.md)
```

Prefixes: `feature/`, `fix/`, `chore/`, `docs/`. Conventional Commits: `feat(api): ...`, `fix(search): ...`, `docs(spec): ...`, `chore(ci): ...`.

The repository, its branches, its rules and its secrets are described as code in the organisation's `github-template` (`repos/mnemarr.yaml`): a change to them is a pull request there, not a setting changed by hand. The AI review that runs in CI on a pull request is advisory (ADR 0008); the SonarQube quality gate, CodeQL and the tests are what block.
