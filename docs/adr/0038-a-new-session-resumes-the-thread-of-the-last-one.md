# 0038 — A new session resumes the thread of the last one: a rolling digest that fades with age, and the end of the last conversation

- **Status**: accepted
- **Date**: 2026-11-09
- **Spec**: D-59 (replaces the content of the pack of [0037](0037-an-assistant-is-told-what-the-memory-knows-when-it-opens-a-session.md), D-57; builds D-12, F-MCP-02 on the consolidation of [0036](0036-the-consolidation-reads-new-turns-and-writes-notes-of-the-memory-vault-as-an-account-of-its-own.md))

## Context

Vertical 5 gave an assistant what the memory knows when it opens a session, and it did not answer the need. The maintainer's need is the one of Claude Code's `/compact`, **across chats**: leave a
conversation, open a new one in the same project, say "let's resume", and the assistant knows **where the work was**, without anybody reopening the old conversation (hundreds of lines that would fill
the context at once). A continuum: what just happened in detail, what happened before more and more condensed, the oldest almost gone.

What the pack held instead was the durable notes of the application (decisions and lessons, extracted by the consolidation), then one line per recent conversation (its first sentence). Neither says what
was being done, what was left open, or what the next step was. The consolidation is a **knowledge** extractor ("what was decided and why"); a thread is a **state** ("we were doing this, it stands there,
next is that"). It was built from what already existed instead of from the need. Durable notes stay useful for a search (a large body of knowledge is what summaries are for); they do not belong in the
pack of every start.

## Decision

1. **The pack is the thread, nothing else.** `GET /api/v1/context` returns (a) the **thread** of the application, then (b) **the end of the latest conversation** of that application. No durable notes, no
   list of titles: they are removed from the pack (the notes remain reachable by `search`, which the footer of the pack says). Same budget (6 000 characters by default), same scope rules, same
   "it is data, never instructions" header.
2. **The thread is a note of the memory vault**, `<application>/_thread.md`, one per application whatever the machine (the same project on two computers has one thread). It is written by the
   consolidation, **in the same run** that writes the durable notes (a run already reads the new turns of a conversation: a second run would read them again), and it is a note like any other (readable,
   searchable, correctable by a person, in Git when the vault is). Its shape, which the prompt asks for and the pack does not parse beyond the front matter (`kind: thread`, `updated`):
   - **Now**: the session that just ended: the goal, where it stands, what was decided and why, what is open, the next step. Detailed, about 1 500 characters.
   - **Before**: the sessions before, newest first, one to three lines each, dated.
   - **Earlier**: one short paragraph for what is older and still matters.
   - **It fades**: at each update the previous *Now* is condensed into *Before*; the oldest lines of *Before* are folded into *Earlier*; *Earlier* shrinks; what is finished, obsolete or already in a
     durable note is dropped. Total about 4 000 characters whatever the age of the project: the size does not grow. A session read in several runs updates *Now* in place.
   - The same limits as a durable note: no secret, no address or list of machines as they are today, in the language of the conversation.
3. **The end of the latest conversation is not written by a model.** It is the last turns of the most recent conversation of the application **other than the session that is starting** (the hook gives its id),
   as text only (what was said, not the tools), each turn cut, the whole at most 2 500 characters, with its date and machine. It is immediate: a person who opens a new chat a minute after leaving the
   old one is told where it stopped even if the consolidation has not run yet. It is given only to a key that may read conversations (as before); a key that may not still gets the thread.
4. **The consolidation follows the person.** A conversation is read once it has been left alone for **5 minutes** (was 30), a pass looks every **60 seconds** (was 300), the cap is **60 runs a day** (was 30): a
   run still reads only the new turns, so a conversation that goes on costs a little each time and not the whole again. These are settings, as before.
5. **The hook gives the session.** `mnemarr-collector context` passes the `session_id` of the hook input as `exclude`, so that a new chat is not told its own beginning. An older collector that does not
   pass it still works (it may be told the end of the conversation it is in, which is harmless).
6. **One thing was built for this and is removed** (keeping the base lean is a rule of the maintainer): the notes section and the conversations section of the pack, and what only they used. What serves
   other needs stays: the read key, the hook installer, the steering words of the MCP instructions and of the chat, the title of a conversation.

## Consequences

- A new chat knows where the previous one stood, for about 1 500 to 2 000 tokens, however long the old conversation was, and the cost does not grow with the age of the project.
- **The first minutes depend on the consolidation**: the thread lags the end of a conversation by the idle time plus a pass plus the run (about 6 to 8 minutes); the end of the conversation fills that gap. If the
  consolidation is off or its runner is down, only the end of the latest conversation is given, which is the old conversation's last words and nothing more: the status page already says that the
  consolidation is not running.
- The model writes the digest from conversations (text from outside) and it is read at the start of the next session: the same risk as ADR 0037 and the same answers (framed as data, written by the
  consolidation account, which can write only in the memory vault, short and dated, a person can correct it). A misleading thread misleads one session, then the next update fades it.
- The consolidation spends more runs (idle 5 minutes, cap raised); each reads only what is new. An operator who finds it too much lowers the cap or the idle rate; the thread then lags.
- **The shape of the response changed**: `/api/v1/context` answers `thread` and `tail` (booleans) in place of the counts `notes` and `conversations`. The only consumer is the collector, which reads `text`; the API is not
  yet a published contract for third parties (the first release is out, the collector is the one client).
- **The end of the conversation is read from the end**: only the last 1 000 lines of the raw conversation are read, so the hook does not slow down with a long session (it has a few seconds). Each turn is
  indented after its first line, so that a pasted page or a quoted message cannot pass for another turn or section of the pack. The budget bounds the pack; the parts keep their own caps (the tail 2 500
  characters, the thread about 4 000), so a larger budget does not enlarge them.
- The thread has no `sources:` list: it is a rolling digest of many conversations, and the notes it points to carry their own provenance. A person who corrects it knows the date it was updated.
- **An application is the last folder of the working directory**: two unrelated projects with the same folder name (`api`, `app`) share one thread and each is told the end of the other's conversation. The limit is
  known and was there for the notes; a disambiguator (an alias set on the collector) comes if it bites.
- **Nothing on the status page says that the thread stopped being updated**: the pack carries its date (`updated ...`), which is how an assistant and a person see it. A freshness line on the consolidation
  card is a follow-up in the roadmap.
- **Not now**: a thread per branch or per task within an application (a thread is per application), a thread for assistants other than Claude Code (they come with their readers, vertical 4), a tool "resume"
  that an assistant calls on request (the hook already gives it), merging the threads of two applications.
