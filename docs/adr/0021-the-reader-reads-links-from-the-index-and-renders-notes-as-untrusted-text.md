# 0021 — The reader reads links from the index and renders notes as untrusted text

- **Status**: accepted
- **Date**: 2026-11-04
- **Spec**: D-41 (implements D-37, [ADR 0017](0017-a-notes-reader-in-the-interface.md))

## Context

ADR 0017 decided that the interface has a read-only notes reader and what it must respect. Building it needs choices ADR 0017 left open: where wikilinks and backlinks come from, how a link is resolved across sources, what renders the Markdown, and how images are served.

## Decision

1. **Links and tags are read when a note is indexed, and stored.** The indexer reads each note's `[[wikilinks]]`, `#tags` and front-matter tags (code ignored) and stores them (schema step 3: `note_links`, `note_tags`, and the lower-case file name of each note). Backlinks are then a query, not a scan of the vaults, and the chunker version is raised so that notes already indexed are read again. The reading is done by hand in one pass, bounded in what a note can make the index hold.
2. **A link is resolved by name, the way Obsidian does, across the sources a caller may read.** `[[docker]]` is the file name `docker` anywhere; `[[devops/docker]]` is the end of a path. When several notes answer, the one in the same source wins, then the shortest path, then the source name: the answer is the same every time. A link to a note the caller may not read is **unresolved**, exactly as if the note did not exist, and the same holds for backlinks and tags: no count or name reveals a source out of scope.
3. **The server never renders.** `GET /api/v1/notes/{source}/{path}` returns the Markdown (front matter removed) with the tags, the resolved links, the backlinks and where the embedded images are; the interface renders it. A note is read from disk when it is asked for, but only if the index knows it (a path that was not indexed is not a file to read), only inside its source's folder, never through a link, never from a hidden folder.
4. **The interface renders with `react-markdown` and `remark-gfm`**, chosen because they build a React tree from the Markdown (no `innerHTML`), **drop raw HTML**, and have a small, widely used surface. Obsidian's wikilinks, embeds and callouts are added by two small pieces of our own (a transformation of `[[...]]` outside code into links with a private scheme, resolved against the server's answer; a plugin for callouts). A link keeps only the schemes `http`, `https`, `mailto` and a relative path; a web link opens with `noopener noreferrer nofollow`; an image is fetched from the note's own source or not shown (an address on another host is text), which the Content-Security-Policy would refuse anyway.
5. **Images are served by the authenticated API from their source** (`GET /api/v1/files/{source}/{path}`): a short list of formats decided by the extension, **never SVG** (it can carry a script), at most 10 MB, `nosniff`, and the same containment and scope rules as notes.
6. **Mermaid diagrams are drawn by the interface, as images.** The courses are full of them, and a reader without them is not a reader. A ```` ```mermaid ```` block is drawn by the `mermaid` library in its strictest mode (`securityLevel: strict`: labels are sanitised, no click handlers, no scripts) with the colors of the application, and what it returns is shown as an **image** (`<img src="data:image/svg+xml">`): an SVG shown as an image cannot run a script or load anything, whatever it holds, so the diagram never enters the page as markup. The library is large and is fetched only when a note holds a diagram. A diagram that cannot be drawn shows the reason and its own text; the text of a diagram that was drawn stays available; a diagram over 20,000 characters is not drawn.
7. **The Content-Security-Policy allows inline styles** (`style-src 'self' 'unsafe-inline'`), because Mermaid measures and draws with them and has no mode without. Scripts, fonts, connections, frames and objects still come from the application only (no `unsafe-eval`, no inline script); a style cannot run anything, and a note cannot put one in the page (no raw HTML). This is a deliberate, recorded weakening of the policy, in exchange for diagrams.
8. **What Obsidian notes use that Markdown does not have is read, nothing more.** A callout (`> [!tip] Title`, foldable with `-` or `+`) is a box or a `<details>`; `<br>` is a line break (the one piece of HTML notes use, in table cells; no other HTML is read); a wikilink written from the folder of its note (`[[../01 - Java/Index]]`) is stored as a path from the root of the source, and one that leaves the source points at nothing.
9. **The reader needs `notes.read`**, which `Reader`, `Assistant`, `Operator` and `Admin` hold; a key limited to some sources sees only those.

## Consequences

- A source out of scope is as absent as one that does not exist, in the tree, the tags, the links, the backlinks and the images.
- New dependencies in the interface (`react-markdown`, `remark-gfm`, `mermaid` and what they bring: Mermaid is the heavy one, loaded on demand); they parse untrusted text, and the tests include hostile notes.
- The tree lists at most 10,000 notes; a vault of that size is searched, not browsed.
- A heading anchor (`[[note#Heading]]`) leads to the note, not to the heading; a note embed (`![[note]]`) is a link, not a transclusion.

## Alternatives considered

- **Render on the server and send HTML.** One place to sanitise, but the server would need an HTML sanitiser and the interface would use `innerHTML`; a React tree has no such step to get wrong.
- **Resolve links in the browser from the whole tree.** The interface would need every note of every source, and would be the place that decides what a person may see.
- **Compute backlinks by scanning the notes at request time.** Simple, and proportional to the size of the vaults at every click.
