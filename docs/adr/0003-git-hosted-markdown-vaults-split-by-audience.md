# 0003 — Markdown vaults hosted in Git, split by audience

- **Status**: accepted
- **Date**: 2026-11-01
- **Spec**: D-03, D-04, D-05

## Context

A pure vector memory is not readable by a person: it only serves retrieval. The maintainer wants a memory that is **also for the operator**, as readable Markdown, browsable in Obsidian, on several devices.

The maintainer already keeps hand-written study notes in an Obsidian vault versioned in Git, used on several PCs and on a phone. Those notes must not be polluted by infrastructure or application material, and some devices (for example a work PC) must never download the infrastructure material.

Two facts constrain the layout:

- **Git has no per-path or per-branch read control.** A fetch brings every branch into `.git`, whether or not it is checked out; a token gives access to a whole repository. Worktrees, sparse checkouts and partial clones choose what is *displayed* or *checked out*, not what is *downloaded*.
- **The Obsidian Git plugin is more limited on mobile** than on desktop (to be verified against its documentation: HTTPS with a token only, no sparse checkout, no submodules, weaker conflict handling, trouble with large repositories). Selectivity must therefore come from the size and content of the repository that is cloned.

## Decision

- Markdown vaults live in **Git repositories**; Git is the central distribution. The Obsidian Git plugin is the synchronisation tool; the paid Obsidian Sync is not used.
- **One repository per audience, not per topic.** A vault is cloned only on the devices that should have it, with a token scoped to that single repository.
- Two vaults at first, plus application documentation read from each application's repository:
  - a **knowledge vault**: hand-written notes (courses, learning). Mnemarr reads it and never writes to it, even if the notes were drafted with an AI: it stays apart;
  - a **memory vault**: consolidated memory (decisions, lessons, observed behaviour, history, per application). **Created, structured and maintained entirely by Mnemarr's AI**; the operator reads it. It does not describe machines, network or deployment, which belong to Portarr;
  - **application documentation**: specifications and behaviour descriptions stay in the application's own repository and evolve with its implementation. Mnemarr indexes them in place, read-only.
- Mnemarr declares each repository as a **source** with a mode (read-write or read-only), clones it, indexes it, and pushes its own writes as commits.
- A third repository is created only when a new audience appears.

## Consequences

- Selectivity and access control are real: a work PC only ever holds the knowledge vault.
- "Everything at once" is Mnemarr's job (search and MCP across all sources, with a per-source filter), not Obsidian's.
- Obsidian cannot resolve `[[wikilinks]]` across vaults. Cross-vault references use a front matter convention that Mnemarr resolves (open question Q-09).
- Notes without front matter exist (the knowledge vault has none): Mnemarr must index them anyway, inferring the type from the source and the path.
- A few repositories to manage; the split stays coarse to keep that cost low.
- The memory vault is opened in Obsidian to be read, not edited. Devices should run the Git plugin in pull-only mode, so that a stray edit never reaches the remote and Mnemarr stays the only writer.
