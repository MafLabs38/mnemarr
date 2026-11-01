# 0006 — The API exposes every capability; the MCP server sits on top of it

- **Status**: accepted
- **Date**: 2026-11-01
- **Spec**: D-19, D-20

## Context

Mnemarr must be usable by any assistant, and Mnemarr's own chat (see [0007](0007-assistant-runner-headless-agent-cli.md)) must be able to operate Mnemarr itself ("index the data now", "change the pull schedule"). Two kinds of caller need two kinds of tools:

- an assistant working on an application wants to **use the memory**: search, read, add an observation, correct a wrong memory;
- the operator, through the chat or an external assistant, wants to **run the brain**: see its state, trigger a pull, an indexing or a consolidation, read and change schedules and settings.

## Decision

- **The REST API exposes every capability of Mnemarr**, with no capability reachable only through the UI or the MCP server. The UI, the chat and the agents all consume it.
- **The MCP server is a thin layer over the API**, not a second implementation. It offers two families of tools:
  - **memory tools**: search, read, add an observation, correct or retire a memory. For any assistant;
  - **administration tools**: status, trigger pull, indexing and consolidation, read settings. Tools that **change settings are off by default** and enabled separately, as in Portarr.
- Each tool needs a permission, and every call is audited.
- Portarr links to Mnemarr's MCP server (companion), so any assistant connected to Portarr discovers the memory.

## Consequences

- One source of behaviour: a fix in the API is a fix in the MCP server.
- "Intelligence inward" (the brain's own model) and "intelligence outward" (any agent via MCP) use the same door.
- The API needs OpenAPI documentation and a compatibility policy from the start, since agents and the MCP server depend on it.
- Correcting a wrong memory (Q-18) becomes an MCP tool: the operator tells an assistant, which calls it.
