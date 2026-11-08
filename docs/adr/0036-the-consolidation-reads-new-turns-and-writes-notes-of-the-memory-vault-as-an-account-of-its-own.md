# 0036 — The consolidation reads the new turns of a conversation and writes notes of the memory vault, as an account of its own, through the runner

- **Status**: accepted
- **Date**: 2026-11-08
- **Spec**: D-56; builds D-14, D-17, D-22, F-CON-01 on the runner of [0007](0007-assistant-runner-headless-agent-cli.md) and [0035](0035-the-chat-is-a-panel-a-proxy-and-a-runner-with-adapters-for-any-model.md)

## Context

The conversations are collected, masked, written as notes and searched, but a conversation of hundreds of thousands of characters is not a memory: the decisions and the lessons are in it, between
tool output and detours. D-17 wants a job that turns them into short durable notes per application, linked to where they came from, that updates instead of piling up. D-14 gives the memory
vault to the AI as its only writer. D-22 gives the job to the same runner as the chat, under a stricter, unattended policy. Q-19 (the subscription, the cost, a light model) was answered by the
maintainer's rule: the subscription, a light model (Haiku), small uses, a cap.

## Decision

1. **A background job, off by default, bounded.** `Consolidation:Enabled` (environment) turns it on. Every few minutes it takes the conversations that grew since their last consolidation and have been left
   alone for 30 minutes, at most 3 per pass and 30 per day (UTC), the oldest first. The model is a setting (`haiku`). A conversation that failed 3 times is left alone until it has grown by 200 lines. A person who
   manages sources can run it now from the Sources page (`POST /api/v1/consolidation/run`), without waiting for the idle time. It needs the runner (`Agent:Url`) and a memory vault open for writing; without
   them the status says which is missing and nothing is sent anywhere.
2. **It reads the new turns only, and Mnemarr cuts them.** Mnemarr keeps for each conversation the lines it held and the turns it read at its last consolidation (table `consolidations`). A run takes the turns after
   those, up to 40 000 characters (a turn is cut at 4 000), and puts them in the prompt: the model is not given the whole conversation to page through (a run costs the same however long the conversation
   is), and a long one is read in several runs. The turns come from the raw lines, not from the note, whose heading changes when a title is written.
3. **One job conversation at the runner, then gone.** Mnemarr starts a conversation at the runner with `policy: job`, sends the prompt, waits for it to end (it looks every 3 seconds; 10 minutes at most), reads
   from its events how many notes the model wrote or changed and whether it failed, and deletes it. Only a count and a short reason are kept: nothing that was said.
4. **The runner's job policy: notes, and nothing else, with nobody to ask.** For a `job` conversation the runner answers each permission request itself: Mnemarr's reads and `create_note`,
   `update_note`, `append_to_note`, `rename_note` run; everything else (including `delete_note`) is refused; there is never a card.
5. **The model acts as an account of its own, and the server holds it to the memory vault.** The account `consolidation` (a service account with the role `Consolidator`: read notes and conversations, write notes, nothing
   else; no key, no password) is made when needed. Each run has a token of this account (the token of ADR 0035, with the id `job:<runner conversation>`; it lasts for the run and is revoked after), valid
   on the MCP server only. For such a caller the MCP server offers the reads and the four note tools, whatever the switches `WriteTools` and `SourceTools` say (turning the consolidation on is the consent), and
   **refuses a write outside the memory vault** ("the consolidation writes in the memory vault only"): the model reads what people and assistants said, which is text from outside (a conversation can contain
   instructions), so the worst it can do is bounded by this, not by its obedience. The log says `consolidation (via job:<id>)`.
6. **What the notes are.** One note per subject in the folder of the application (`<application>/<subject>.md`), with front matter (`application`, `status: active|superseded`, `updated`, `sources:` the
   conversation notes it came from), sections Decisions, Lessons, Facts, History. The model looks first at what the vault says (search, list, read), updates the note that exists with the version it read,
   corrects a contradicted note in place or marks it `superseded` with a line saying what replaces it, and never deletes. It writes in the language of the conversation, never a secret, never an address or a list of
   machines as they are today (D-15: that is Portarr's). When nothing deserves to be kept it writes nothing.
7. **It is seen.** The Sources page has a card (what waits, what is done, notes written, runs today of the day's cap, the last failure with its reason, the button). `GET /api/v1/consolidation` says the same; the readiness probe
   reports `consolidation-no-runner`, `consolidation-no-memory-vault`, `consolidation-memory-vault-read-only`, `consolidation-runner-unreachable` or `consolidation-failing` (degraded, never unhealthy: everything else works without it).

## Consequences

- **Conversations are sent to the model's provider**, already masked, when the consolidation is on. The operator accepts this by turning it on, as for the chat and the titles.
- **It spends the subscription**, a little for each conversation (a light model, a bounded excerpt, 30 runs a day at most by default). Whether an unattended use of a subscription is covered by the provider's terms stays
  Q-19: the maintainer chose it knowing that; an API key remains a possible adapter.
- A model can still write something wrong or banal in the memory vault. The notes carry their sources so that a person can check, the vault is a Git repository when the operator wants (a mistake is reverted), and a
  person who holds `notes.write` can correct a note (Q-18); the status shows the count so that a runaway is seen.
- The consolidation is as good as the prompt: it will be tuned on real conversations.
- Not now: a pause when the subscription's limit is near (the runner's error is shown and the conversation is tried again later), consolidation of the notes of a knowledge vault, a second pass that merges and prunes the
  memory vault, another reader than Claude Code's (vertical 4) feeding it.
