# 0002 — *arr-like stack, and external helpers in any language

- **Status**: accepted
- **Date**: 2026-11-01
- **Spec**: D-01, D-02

## Context

Mnemarr belongs to a family of products (Portarr, Orchestrarr) that share one philosophy: the *arr suite. For the maintainer, that philosophy is a **monolithic application in one container that does one job well and talks to the rest through its API**. It does not prescribe the technology of anything that lives outside that container.

Mnemarr also needs small programs outside the container: something has to run on every machine where an assistant runs, to read its conversations and send them to the server (see [0005](0005-collector-agent.md)).

## Decision

- **The Mnemarr container** is a single image: a .NET (C#) API on ASP.NET Core and a React (TypeScript) frontend, API first, the UI consuming the REST API. This is the stack of the whole family and is recorded in `CLAUDE.md`.
- **External helpers** (agents, collectors, importers) are not part of the application. They serve the container through its API and may be written in any language. Another helper doing the same job could replace one without changing Mnemarr. A helper never defines the application.

## Consequences

- The container keeps the family's conventions (tests, CI, design rules).
- Helpers can use the best tool for a small, always-on, low-footprint program, at the cost of a second toolchain in CI.
- The REST API is the only contract between Mnemarr and its helpers, so it must be versioned and documented from the start.
