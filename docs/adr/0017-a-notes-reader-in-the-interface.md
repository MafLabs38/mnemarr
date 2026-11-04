# 0017 — A notes reader in the interface

- **Status**: accepted
- **Date**: 2026-11-03
- **Spec**: D-37 (amends D-18)

## Context

D-18 said that reading the memory is done in Obsidian and not in Mnemarr. Obsidian has no official web version: reading from a browser or a phone without cloning a vault means running another product in another container. The maintainer wants a reader inside Mnemarr, "a home-made web Obsidian", which also respects the rights of the people who sign in: a user who may read the notes only sees the notes.

## Decision

1. **The interface has a read-only notes reader.** It shows the sources a person may read as a tree of folders, renders a note's Markdown, resolves `[[wikilinks]]` (inside a source and across sources, by note name, so that three repositories read as one vault), lists the backlinks and the tags of a note, and searches.
2. **It is Obsidian-compatible, not Obsidian.** The files stay what Obsidian opens: standard Markdown, wikilinks, tags, callouts, with the folder structure of the vault. Obsidian, and its Git plugin on a phone, keep working on the same repositories. The reader does not edit: notes are written by assistants through the MCP server (D-35) or in Obsidian.
3. **It obeys the permissions of the person and, for an API key, its scope** (ADR 0016): the notes of a source that a person may not read are neither listed, nor rendered, nor found by a search, nor counted.
4. **A note is untrusted content.** A memory note can quote a conversation, a web page or a tool's output. Markdown is rendered without raw HTML, a link is checked for its scheme, an image is served by the authenticated API from the source it belongs to (never from another path), and the content-security policy of the interface stays as strict as it is.

## Consequences

- D-18 stands for what it was meant to protect (Mnemarr is not a note editor and its interface configures the brain) and is amended on the sentence that sent the reading to Obsidian.
- The renderer is a dependency that parses untrusted text: it is chosen for its safety and its Obsidian-flavoured Markdown support, and tested against hostile notes.
- Backlinks and wikilink resolution need the links of each note in the index.

## Alternatives considered

- **A second product that serves the vaults (Obsidian in a container, a static site generator).** It would not know the permissions of Mnemarr, and it is one more thing to run and to secure.
- **No reader: Obsidian only.** Keeps the interface minimal, but leaves a browser or a phone without a way to read what the assistants wrote.
