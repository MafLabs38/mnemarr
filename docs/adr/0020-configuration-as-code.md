# 0020 — Configuration as code: a YAML document, applied through the API or as a file

- **Status**: accepted
- **Date**: 2026-11-04
- **Spec**: D-40 (builds on D-39 and D-19; answers part of Q-29)

## Context

The maintainer runs everything as infrastructure as code: what a page of the interface configures must also be describable in a file, kept in a repository, and sent programmatically. The same operations must be available to an assistant (the MCP server and the chat panel are built on the API, D-19 to D-21), so nothing the interface does may exist only in the interface. The first thing to configure is the sources, which have rules of their own (ADR 0019): a base that is required and unique, and kinds that grow.

## Decision

1. **A configuration document is YAML in the shape of the rules.** The memory vault is an object (there is exactly one); knowledge vaults and application documentation are lists:

   ```yaml
   memory:
     path: /vaults/memory
   knowledge:
     - name: courses
       path: /vaults/courses
   applicationDocs:
     - name: portarr
       path: /vaults/portarr
       mode: ReadOnly
   ```

   The name of the memory vault is `memory` unless given; the mode is the default of the kind unless given.
2. **A section that is absent is not managed; one that is present is, even when it is empty.** So `knowledge: []` says "no knowledge vault is declared by configuration", and a document without `applicationDocs` leaves the documentation sources as they are. The memory vault is required: a document never removes it.
3. **What a document owns is marked as managed**, and only that is ever removed by it. A source made by hand with the same name (the memory vault: the one that exists) is **taken over** and becomes managed; a source made by hand and not listed is left alone. A managed source cannot be changed or forgotten by hand (409): the document is the truth, and a hand edit would be undone at the next application.
4. **Everything is checked before anything is changed, and applying is idempotent.** Everything is checked first (paths inside the allowed roots, names, modes, duplicate names); a document with an error changes nothing and the answer lists every error (400). Applying the same document again changes nothing. A **preview** (`dryRun`) says what would be created, changed, taken over or removed.
5. **Three ways in, the same document**: `GET /api/v1/config` exports the current state as a document (all three sections, so that applying it back changes nothing); `PUT /api/v1/config` previews or applies one (`application/yaml`); and a **file**, `Config:File`, is applied at start and again whenever it changes (a mounted ConfigMap, a pull) or when a previous attempt failed (a folder mounted late). **When a file is given, it is the one document**: `PUT /api/v1/config` then refuses to apply (409) and only previews, so that the file never silently stops describing the system. A file that cannot be applied does not stop the application: readiness is degraded with a reason (`config-invalid`, `config-pending`) that gives no path.
6. **A configuration is global**: a key limited to some sources can neither export nor apply a document (an export with only some of the sources would remove the others when applied back). The right to apply is the right to manage what the document describes (sources today; each later section asks for its own permission).
7. **The document is input from outside, so it is read strictly**: at most 256 KB, one document, at most 8 levels deep, no anchors or aliases (how a small document becomes a huge one) and no tags, and an unknown key is an error and not something ignored. The reader is YamlDotNet; the document is never turned into anything but the plain types of the model.

8. **A source stays managed until the document removes it.** The memory vault is never removed, so once a document has taken it over it stays managed, and its folder is changed in the document. Handing it back to manual control is not offered; if it is ever needed, it will be a decision (for example `memory: null`).
9. **The steps of an application run one after the other under the lock of the catalog**, after the whole document has been checked. If the database fails halfway, the document is not fully applied and nothing says it was; applying it again finishes the job, as the file service does on its next pass.

## Consequences

- The memory vault and the base are configurable by an operator, by a pipeline and by an assistant, with the same rules and the same audit (`config.applied`).
- Every vertical that adds something to configure adds a section to the document, its rules, and its permission; the interface shows what is managed.
- A new dependency, YamlDotNet, parses untrusted text: it is pinned, used behind one class, and tested against hostile documents.
- Secrets never go in a document (a document is meant to be committed): they stay files named by settings.

## Alternatives considered

- **JSON**: no dependency, but unpleasant to write and to review as infrastructure.
- **Environment variables only**: fine for bootstrap (ADR 0019), not for lists of objects.
- **The file as the only way**: simple, but an assistant and the interface could not act on the system, and Mnemarr would have two truths.
- **A hand edit wins over the file**: the file would silently stop describing the infrastructure.
