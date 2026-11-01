# 0001 — Record architecture decisions

- **Status**: accepted
- **Date**: 2026-11-01
- **Spec**: —

## Context

Mnemarr is built over many interviews and iterations. Without a written trail, the reasons behind a choice are lost, and the same debate comes back.

## Decision

Every structural decision gets an Architecture Decision Record in `docs/adr/NNNN-title.md`, and a `D-xx` row in [the spec](../SPEC.md#2-decisions). An accepted ADR is not edited to change its decision: a new ADR supersedes it and the old one is marked superseded.

## Consequences

- Choices stay explainable to contributors and to the maintainer months later.
- A small writing cost for every structural change.
