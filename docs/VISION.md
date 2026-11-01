# Mnemarr — Vision

> **Status**: inception. Gathered from the maintainer's notes on 2026-10-31; to be confirmed and refined in the first interview.
> This document feeds [SPEC.md](SPEC.md), which now carries the decisions taken in the interviews; where the two differ, the spec wins.

## 1. Pitch

**Mnemarr** (from *mnemonic*, plus *arr*) is a self-hosted **long-term memory for people and AI assistants**. It stores notes, decisions, runbooks and session summaries, and finds them again **by meaning** (semantic search) as well as by text. It exposes a **REST API** and an **MCP server**, so that any assistant (Claude Code and others) can read and write memories.

It is a standalone, API-first application in the spirit of the *arr suite: one container, pluggable providers, a familiar interface.

## 2. Why

### Maintainer's words

> « J'ai une mémoire bricolée : un wiki Markdown dans Obsidian, un magasin de vecteurs, une CouchDB… jamais un seul projet maintenu. »
>
> « Les assistants ne connaissent pas mon environnement. À chaque nouvelle session il faut tout leur réexpliquer. »
>
> « Je réécris les fichiers CLAUDE.md à chaque changement d'infra. »

*(Paraphrased from the maintainer's notes; the exact wording is to be confirmed.)*

### The problem, in English

1. **Ad-hoc memory, never maintained.** Today's setup is three unrelated pieces — a Markdown wiki edited in Obsidian, a vector store, a document database — glued together by scripts. It was never one maintained project, so it drifts, breaks and is not trusted.
2. **Assistants do not know the environment.** Each new assistant session starts from zero. Knowledge about machines, past decisions, how things were fixed, and why, lives in the maintainer's head or in scattered files.
3. **Instruction files are rewritten by hand.** Every infrastructure change means updating several `CLAUDE.md` files (global and per project). That knowledge belongs in one queryable place that assistants consult on demand, not in copies that go stale.

## 3. What it must do

Draft capabilities, to be prioritised in the interview:

- **Capture** notes, decisions, runbooks, session summaries and free text, with metadata (type, tags, source, project, date, author — human or assistant).
- **Search by meaning and by text**, combined; filter by type, tag, project, date.
- **Relate** memories to each other and to things (an application, a machine, a decision), without pretending to be a full knowledge graph.
- **REST API first**; the UI consumes it. OpenAPI document published.
- **MCP server**: tools to search, read, add and update memories, so any assistant can use it. Write tools enabled separately and audited.
- **Human UI** in *arr style: browse, search, edit, review what assistants wrote.
- **Lifecycle**: supersede or archive outdated memories rather than silently overwrite; keep history.
- **Import** from existing sources (a Markdown wiki or vault first).
- **Standalone first**: works with no companion at all; each integration is opt-in.

Out of scope (to confirm): hosting the assistants themselves, being a general document store or a file sync tool, replacing a metrics or log store.

## 4. How it plugs into the others

- **Portarr (See)**: Mnemarr is registered in Portarr as a **companion**. Portarr's MCP server points assistants to Mnemarr, so any assistant connected to Portarr also finds the operator's memory. Portarr does not store that memory; it links to it.
- **Orchestrarr (Act)**: workers read runbooks and past decisions before acting, and write back what they did and what happened.
- **Assistants (MCP)**: any MCP-capable assistant connects directly. A short bootstrap instruction file can point to Mnemarr instead of carrying the knowledge itself.

### Knowledge base vs memory (clarified 2026-10-31)

The maintainer separates two things:

- **Knowledge base**: Obsidian vaults kept in Git repositories (course notes for his training, work knowledge), used on several machines. They are written and organized by hand. Mnemarr does **not** replace them.
- **Memory**: what assistants and agents need to recall (facts about the environment, decisions, what happened in past sessions). That is Mnemarr's job, as a **RAG** (retrieval-augmented generation) over a vector store, in the spirit of the earlier attempt based on Qdrant.

Open point: whether Mnemarr should also *index* the Git-hosted Obsidian vaults read-only, so assistants can search the knowledge base without Mnemarr owning it.

### Idea to explore: a readable memory vault (noted 2026-10-31)

A pure vector memory has one flaw: it is not readable by a person, it only serves retrieval. The maintainer sees value in a **dedicated vault** (Markdown files, Obsidian-compatible, in Git) as the memory's **source of truth**: one place that gathers everything he has built so far (each application's specs and decisions, how it is deployed, what was done and why), readable and browsable by him, and by AI assistants. The vector index would then be **derived** from that vault (re-buildable at any time), not the primary store.

That makes Mnemarr a memory **for the operator as much as for the AI**. Questions for the interview: who writes into the vault (assistants, the operator, both, with review?); how notes are structured (one folder per application, decisions as dated notes, session summaries); how it relates to each repository's own docs (copy, link, or index in place); conflict handling when an assistant and the operator edit the same note.

> Maintainer's words: « Le défaut d'une mémoire vectorielle, c'est que par principe, elle n'est pas très consultable… elle sert juste à l'IA. Là, ça serait pas qu'une mémoire qui serait pour l'IA, ça serait aussi une mémoire pour moi. »

> Maintainer's words: « Le wiki Obsidian, je m'en sers pour ma formation… je vais m'en servir en tant que base de connaissances. Mais là, je vais pas m'en servir comme mémoire. Ça, comme mémoire, je vais plutôt utiliser un RAG, sur le modèle de ce qu'on avait mis en place, avec Qdrant à la base. »

## 5. Open questions for the first interview

Asked openly, one topic at a time.

1. **Storage engine.** One database for everything (e.g. SQLite or PostgreSQL with a vector extension), or text storage plus a dedicated vector index? What must survive a restore, and how are memories backed up?
2. **Embeddings model.** Local (runs on the host, no data leaves, heavier) or an external API (lighter, but memories leave the host)? Is a hybrid or a switchable provider wanted? What happens to existing vectors when the model changes?
3. **Import of the existing wiki.** The current Markdown wiki (an Obsidian vault) holds real knowledge. One-shot import, or continuous two-way sync with a folder of Markdown files kept as source of truth? How to handle front matter, links and attachments?
4. **Multi-user.** Single operator with several assistants, or several people? Authentication (API keys, OIDC through a reverse proxy)? Per-memory ownership and visibility?
5. **Privacy.** What may be stored (credentials must never be)? Redaction of secrets on write? Audit of what assistants read and wrote? Per-assistant scopes (read-only vs write)?
6. **Memory model.** Which types exist (note, decision, runbook, session summary, fact about a user…)? Free text with tags, or structured templates? How are duplicates and contradictions detected?
7. **Assistant behaviour.** Who decides what is worth remembering — the assistant, the human, a review queue? How is a session summary produced and filed?
8. **Scale and hosting.** Expected volume (hundreds or hundreds of thousands of memories)? Resource budget on a small homelab host?
9. **Name and scope check.** Is "Mnemarr" confirmed? Anything explicitly out of scope for v1?
