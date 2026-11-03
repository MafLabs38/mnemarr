# Contributing to Mnemarr

Thanks for your interest! Mnemarr is at an early stage; the best way to help right now is to read the [specification](docs/SPEC.md) and open an issue with feedback.

## Ground rules

- **English** for code, comments, docs, commits, issues and pull requests.
- **No real infrastructure data** in anything you submit: no domain names, IP addresses, host names, personal paths, tokens, nor real memory content. Use `homelab.example`, `server-1`, `10.0.0.10`. CI rejects commits containing secrets.
- Be kind. See the [Contributor Covenant](https://www.contributor-covenant.org/version/2/1/code_of_conduct/).

## Workflow

1. Open (or pick) an issue.
2. Branch from `develop`: `feature/<issue>-<short-description>` (also `fix/`, `chore/`, `docs/`).
3. Commit with [Conventional Commits](https://www.conventionalcommits.org/): `feat(api): add the system status endpoint`.
4. Open a pull request against `develop` and complete the PR template. Its title is a Conventional Commit too: it becomes the squash commit, and it decides the next version ([releasing](docs/releasing.md)).
5. CI must be green: lint, unit, integration, E2E smoke, the collector on Linux and Windows, CodeQL, gitleaks, Trivy, and SonarQube once configured.

Direct pushes to `main` and `develop` are blocked for everyone. One vertical (a feature end to end) is one pull request, with its tests and documentation. A structural decision gets an ADR in `docs/adr/` and a `D-xx` row in the spec.

## What a good PR contains

- **Zero warnings.** The SonarQube rules run in the build and in ESLint, so `dotnet build` and `yarn lint` already say what the quality gate will say. Fix a smell rather than silence it; a justified exception is documented where it is made.
- Tests at the right level of the pyramid ([TESTING.md](docs/TESTING.md)).
- Documentation updates (spec, ADR when a decision is made).
- A self-review with the four review agents of `.claude/agents/` (tests, quality, security, architecture): run the `review-vertical` skill before opening the PR.

## Development setup

Needs the .NET 10 SDK, Node 22 with Yarn 1, Rust (stable) and Docker.

```bash
yarn install --frozen-lockfile
dotnet build src/Mnemarr.sln

# Backend, then frontend with hot reload (the frontend proxies /api to the backend)
# The data folder must exist and be writable (it defaults to /config, the image's volume)
mkdir -p .data && echo "a password of at least 12 characters" > .data/admin-password
MNEMARR__DATAFOLDER="$PWD/.data" MNEMARR__AUTH__ADMINUSER=admin MNEMARR__AUTH__ADMINPASSWORDFILE="$PWD/.data/admin-password" \
  MNEMARR__AUTH__COOKIESECURITY=SameAsRequest dotnet run --project src/Mnemarr.Host   # http://localhost:8383, sign in as admin
yarn start                                   # http://localhost:5173

# Checks, as CI runs them
dotnet format src/Mnemarr.sln --verify-no-changes
yarn lint && yarn stylelint && yarn format:check && yarn typecheck
dotnet test --solution src/Mnemarr.sln
yarn test --coverage
(cd collector && cargo fmt --check && cargo clippy --all-targets --locked -- -D warnings && cargo test --locked)
```

`.data/` is ignored by Git. On a small machine, run the heavy steps in a container with a CPU and memory cap, so a build cannot starve the host:

```bash
docker run --rm --cpus 2 --memory 4g -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test --solution src/Mnemarr.sln
```

End-to-end tests run against an instance of the image: `docker compose -f tests/e2e/stack/docker-compose.yml up -d --build`, then `yarn e2e --grep @smoke`.
