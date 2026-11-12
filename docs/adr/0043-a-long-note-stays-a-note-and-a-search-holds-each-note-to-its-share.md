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

1. **The size limit is a setting, `Indexing:MaxNoteMegabytes`, 32 by default (2 to 64).** It bounds what a pass reads into memory at once, so that one file that is not a note (a log renamed `.md`) cannot fill the host.
   It is a guard, no longer the size of a conversation. 2 MB stays what a person writes in one go through the editor (ADR 0023) and the length of a page of the interface.
2. **A note above the limit is never forgotten.** It is not read, it **keeps what the index had of it**, and it is **counted and shown**: `tooLarge` in the report of a pass, `notesTooLarge` on the source, a line on the
   Sources page that names the setting. A limit that silently loses data is a defect; one that says what it left out is a setting. A note above the limit is not opened either (a sentence that names the
   setting, not "no such note"), and **a reconstruction (`rebuild=true`) drops it**: that is how what the index kept of it is purged, a secret that was removed from the file included. The setting is at least
   2 MB, the size that can be written, so that Mnemarr never writes a note it then refuses to read.
3. **The interface shows pages, an assistant reads the whole.** A note longer than a page (512 KB, drawn in about a second) comes to the interface in pages cut at line ends (`page` of `pages`, `?page=`),
   with the version of the whole note. A page is never a note: the editor is off for it (reason `note-too-long`, unless the source is read-only, which is the first reason), and **the server refuses to
   replace such a note whole** (409), because a client that sends one page back with the version of the whole would cut the rest off; appending stays possible up to the 2 MB that the editor writes (above it, a note is read and searched but not changed from here, except that it can be removed: its file is the way to change or move it). The MCP `read_note` reads the whole in windows,
   the end included (`tail`), and draws nothing for it: that is how an assistant reads a conversation.
4. **A search stays within some topics, leaves topics and sources out, and holds each note to its share.** `topic` may be repeated (any one of them), `notTopic` and `notSource` leave out, and `perNote` (3 by default,
   1 to 50) is how many passages one note may give. The share is applied inside the keyword query (every matching passage is ranked and held to its share before the best are completed) and again on the merged list, so that the other notes fill the answer. MCP `search`
   takes the same as `topic` and `exclude_topic` (comma separated) and `per_note`.

## Consequences

- A pass over a note of tens of megabytes reads it whole (bytes, text, passages): memory is bounded by the setting and by one note at a time (the lock of the source), not by the vault. Measured on a 28 MB note:
  7 seconds, 665 MB of peak memory in the container, about 20 times the size of the note: that is why the ceiling is 64 MB, and why at most two reads of a note above 2 MB run at once (a third waits one second, which holds a thread of the host that long at most, and is
  told to try again, with `Retry-After`). A page of a note is read from the whole file each time (no cache yet).
- A very long conversation is still one note, so an append re-reads it; the cost per pass grows with the conversation (the debounce noted in ADR 0039 remains the answer if it is measured to matter).
- The default share changes the answer of a search: a note gives at most three passages. `perNote=50` gives the old behaviour.
