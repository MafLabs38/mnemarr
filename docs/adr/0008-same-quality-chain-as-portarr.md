# 0008 — The same quality chain and delivery as Portarr

- **Status**: accepted
- **Date**: 2026-11-01
- **Spec**: D-23, D-24

## Context

Mnemarr is free and public, and the maintainer wants it to be adoptable by other people. That calls for the same quality bar as Portarr, which already has a heavy but proven chain: lint and format checks, unit, integration and end-to-end tests with coverage thresholds, SonarQube quality gate, CodeQL, weekly mutation testing, four review agents on every pull request, hardened images and infrastructure as code. The development cost is higher; it is accepted.

The organisation's repository template already describes Mnemarr as code (repository settings, branch rules, quality options) and its quality options for Portarr come from the same template.

## Decision

- Mnemarr uses **the same pipeline, thresholds and review agents as Portarr**, derived from the same template and adapted to its components. Coverage thresholds follow Portarr's testing strategy: at least 70 % overall on the .NET core and at least 80 % on new code, frontend components at least 70 %, all unit and integration tests green, an end-to-end smoke run on every pull request. Mutation testing is reported, not enforced.
- **Four review agents** (tests, quality, security, architecture) run on every pull request, advisory, and the review skill is run locally on each vertical before its pull request. SonarQube, CodeQL and the tests are what block a merge.
- **Components get their own jobs**: the .NET core and the React frontend as in Portarr; the collector agent (Rust) with format, lint-as-error, tests and a smoke test of the real binary; the assistant runner with its own tests and recorded fixtures of the agent CLI's protocol.
- **Infrastructure as code**: repository settings and protections stay in the organisation template's code; the product ships generic deployment examples (container and Kubernetes manifests) under `deploy/`; any personal deployment lives in the maintainer's own private infrastructure repository, never here.
- **Order**: a foundation vertical comes first. It brings the pipeline and a minimal walking skeleton that makes every job pass, and only then are the checks made required in the template. No feature vertical starts on a repository without its quality chain.
- The runner and the collector start from Portarr's existing runner and reader code, to avoid reinventing them.

## Consequences

- Each vertical is heavier to deliver, in exchange for a repository others can trust and contribute to.
- Some steps need the maintainer: the SonarQube token and the model login used by the review agents are repository secrets that only he creates.
- Making checks required is a change to the template repository, which is a separate, deliberate step once the pipeline passes.
- Divergence from Portarr's pipeline should be recorded: a fix to the shared chain is applied in both.
- **The review workflow is hardened against hostile content** *(amended 2026-11-03, D-32)* (found by the whole-repository review). The model reads text that the author of a pull request wrote, so the job that runs it has no write permission, isolates its subprocesses, has no shell and no write tool, and can only read the repository (its report is its final message, which a step of the workflow writes to a file); a second job with no model scans the report for secrets (a defence in depth, not a guarantee) before posting it, and only edits comments written by the Actions bot. **The boundary is stated plainly:** a pull request runs the workflow of its own branch, so an author of a branch of this repository can change the workflow and read the secrets; same-repository branch authors are trusted with them, pull requests from forks never get them. The SonarQube token is an argument of the scanner's `begin` and `end`, and that job runs the pull request's own code, on the same terms.
- **The review agents in CI.** The workflow that runs the four agents on every pull request (`ai-review.yml`) started as Portarr's and **has diverged on purpose**: it raises a visible warning without the `CLAUDE_CODE_OAUTH_TOKEN` secret, and it is split in two jobs as described above. Portarr's copy has not received that hardening: this is a recorded divergence, and the same change should be applied there. Until the secret exists (and `quality.ai_review` is on in the repository's definition in the organisation template, which also creates the `ai-review` label), the `review-vertical` skill runs the agents locally before each pull request. SonarQube is configured. The base images of the Dockerfile are pinned by tag; pinning them by digest is part of the release vertical, which publishes the image.
