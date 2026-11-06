# 0007 — An assistant runner: a headless agent CLI behind an adapter, in an optional container

- **Status**: accepted for the runner and the chat (D-21); the use of the same runner for the consolidation (D-22) is provisional, to confirm on the first consolidation vertical
- **Date**: 2026-11-01
- **Spec**: D-21, D-22

## Context

Mnemarr needs a language model in two places:

1. **Inward**: the brain's consolidation (reading conversations and deciding what is worth keeping, see D-17).
2. **Outward to the operator**: a chat in the UI, like Portarr's, that can act on Mnemarr itself through its MCP server.

Portarr already solved the second one (its ADR 0008): the application does not call a model. A separate optional container runs an agent CLI in headless, structured mode (Claude Code first) behind an adapter; the CLI's only tool is Portarr's MCP server; writes wait for the operator. The application image holds neither the CLI nor model credentials.

The operator wants to use his existing Claude subscription rather than a separate pay-per-use key.

## Decision

Reuse Portarr's design for Mnemarr, in a separate optional container (the **assistant runner**):

- The runner drives an agent CLI in headless mode, one process per active conversation or job, behind an **adapter** (Claude Code first, others later). Mnemarr's code does not know which CLI runs.
- **The only tool is Mnemarr's MCP server** (see [0006](0006-api-first-with-mcp-on-top.md)); no built-in tool, no shell, no file access.
- The runner, not the CLI, decides what a tool may do, with **two policies**:
  - **chat**: reads run; changes to settings ask the operator;
  - **job** (consolidation): a fixed, scoped set of memory tools runs without confirmation: read conversations, create, update and supersede memory notes. Nothing else.
- The consolidation job **runs through the same runner** as the chat (one mechanism, two uses). Provisional: to confirm on the first consolidation vertical.
- The model credentials (the subscription login) exist **only in the runner container**. The main image never holds them.
- Without the runner, Mnemarr still ingests, indexes and serves search and MCP; it only lacks consolidation and the chat.

## Consequences

- Mnemarr is two images, not one: the main container (the *arr rule) and the optional runner. This mirrors Portarr.
- **Conversations are sent to Anthropic** when consolidated: secrets must be masked before (D-10), and the operator accepts this by choosing Claude.
- **A subscription has usage limits shared with the operator's interactive coding.** Consolidating many conversations can consume them. The consolidation cadence, a cap and the model used for batch work must be settings (Q-19). Whether automated use of a subscription this way is covered by the provider's terms must be verified before relying on it (Q-19); an API key stays a possible adapter setting.
- The runner depends on the CLI's structured protocol; its version is pinned in the image and fixtures pin what the parser expects.
- Each active conversation costs one CLI process; the runner caps and stops idle ones.
