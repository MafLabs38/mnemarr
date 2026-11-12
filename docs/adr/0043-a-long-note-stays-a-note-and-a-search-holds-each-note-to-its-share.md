# 0043 — A long note stays a note, and a search holds each note to its share

- **Status**: accepted
- **Date**: 2026-11-12
- **Spec**: D-64; relaxes the 2 MB limit of [0019](0019-sources-are-declared-through-the-api-and-scanned-into-a-searchable-index.md) (D-39)

## Context

A conversation is one note, and it grows with every exchange. Two limits made a long one unreliable:

- **A note above 2 MB was not a note.** The scan did not list it, so the next pass found the note in the index but not in the folder and **removed it from the index**: a conversation vanished from the search
  exactly when it had become worth finding, and the reader refused to open it. Nothing said so. 2 MB is about 1 400 passages: a long working session reaches it.
- **A search gave one topic and no share.** One topic could be asked for, none could be left out, and the best passages were taken as they came: one long conversation with a hundred matching passages filled the
  whole answer and hid every other note.

## Decision

1. **The size limit is a setting, `Indexing:MaxNoteMegabytes`, 32 by default (1 to 512).** It bounds what a pass reads into memory at once, so that one file that is not a note (a log renamed `.md`) cannot fill the host.
   It is a guard, no longer the size of a conversation. 2 MB stays what a person writes in one go through the editor (ADR 0023) and the length of a page of the interface.
2. **A note above the limit is never forgotten.** It is not read, it **keeps what the index had of it**, and it is **counted and shown**: `tooLarge` in the report of a pass, `notesTooLarge` on the source, a line on the
   Sources page that names the setting. A limit that silently loses data is a defect; one that says what it left out is a setting.
3. **The interface shows a page, an assistant reads the whole.** A note longer than 2 MB comes to the interface as its first part, cut at a line end (`cut`), with the version of the whole note; the editor is off for it
   (reason `note-too-long`), because saving the first part would replace the note with it. The MCP `read_note` reads the whole in windows, the end included (`tail`): that is how an assistant reads a conversation.
4. **A search stays within some topics, leaves topics and sources out, and holds each note to its share.** `topic` may be repeated (any one of them), `notTopic` and `notSource` leave out, and `perNote` (3 by default,
   1 to 50) is how many passages one note may give. The share is applied inside the keyword query (a window over the best 600 passages) and again on the merged list, so that the other notes fill the answer. MCP `search`
   takes the same as `topic` and `exclude_topic` (comma separated) and `per_note`.

## Consequences

- A pass over a note of tens of megabytes reads it whole (bytes, text, passages): memory is bounded by the setting and by one note at a time (the lock of the source), not by the vault.
- A very long conversation is still one note, so an append re-reads it; the cost per pass grows with the conversation (the debounce noted in ADR 0039 remains the answer if it is measured to matter).
- The default share changes the answer of a search: a note gives at most three passages. `perNote=50` gives the old behaviour.
