# 0015 — Sources are read-write capable: Mnemarr writes in the document repositories it owns and proposes changes elsewhere

- **Status**: accepted
- **Date**: 2026-11-03
- **Spec**: D-35 (supersedes the write rules of D-05, D-11 and D-14; partly supersedes [0003](0003-git-hosted-markdown-vaults-split-by-audience.md))

## Context

[0003](0003-git-hosted-markdown-vaults-split-by-audience.md) made the knowledge vault, the hand-written course notes, read-only for Mnemarr, and application documentation read-only too: only the memory vault was written. The maintainer's purpose is wider. Mnemarr centralises knowledge **and lets it evolve**: when an assistant is asked to complete a course on a subject, it must read the notes through Mnemarr, make sure the repository is up to date, update the notes, and push the result so that another device can fetch it. A memory reads and writes by nature.

## Decision

1. **A source has a kind and a mode.** The kinds are the knowledge vault, the memory vault and application documentation; the mode, read-only or read-write, is a setting of each source (it was already in the data model). **The mode is the write switch of point 5, and it is off by default for every kind**: a source is read-only until an operator turns writing on for it, source by source.
2. **A document repository is one whose job is documents** (the knowledge vault, the memory vault). Mnemarr owns their upkeep: it writes there **directly**, with a commit pushed on the configured branch, so that other devices pull it.
3. **Any other repository is not Mnemarr's responsibility.** An application's repository holds its code and its documentation: when Mnemarr finds that documentation wrong or out of date, it **opens a pull request** and never pushes. Its read-write mode means "may propose".
4. **Raw conversations are the one input that is only read**, and they are not in Git (D-06): an archive, not versioned, that consolidation reads. It may sit on a network share (D-07), unlike the database and the vector index.
5. **Writing is done with care**, not in any way:
   - only through the write tools of the API and the MCP server, which are **off by default, enabled per source and audited** (D-20);
   - **bring the working copy up to date before any write**, and read the existing notes first: complete the note of the subject rather than create a duplicate;
   - put the note where the structure of the vault says (**the location characterises the note**: no front matter is required, and one that exists is kept);
   - never touch what belongs to the tools: `.obsidian/`, templates, attachments;
   - **mask secrets before the commit** (D-10): what is pushed to a remote must hold none;
   - **one commit per write**, authored by Mnemarr, saying what changed and for which assistant; **never a force-push**; on a non-fast-forward or a conflict, abort and report it to the caller.

## Consequences

- The devices of the knowledge vault keep pushing as before, so conflicts can happen: point 5 is the policy. The memory vault stays written by Mnemarr only (D-14, Q-12 unchanged).
- Writing to a remote needs a Git engine that works in the chiseled image, and a credential per source with the narrowest scope (push for a document repository, pull request for another).
- The audit trail of a write is what makes an evolving knowledge base trustworthy: who asked, which note, which commit.
- Documentation in an application's repository still evolves with its implementation (D-11): Mnemarr proposes, the owner of the repository decides.

## Alternatives considered

- **Read-only knowledge vault (0003).** Safe, but the knowledge could only be changed by hand, and an assistant asked to complete a course could not leave its work anywhere another device can fetch.
- **Direct push to application repositories.** They have their own review rules and are not documents repositories: Mnemarr is not responsible for them.
