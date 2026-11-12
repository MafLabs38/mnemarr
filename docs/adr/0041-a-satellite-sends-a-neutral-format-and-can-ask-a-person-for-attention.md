# 0041 — A satellite sends a neutral format, is not consolidated by default, and can ask a person for attention

- **Status**: accepted
- **Date**: 2026-11-12
- **Spec**: D-62; extends D-45 and D-47 ([0025](0025-the-collector-is-a-service-per-machine-enrolled-by-a-code-and-configured-from-the-server.md), [0027](0027-machines-join-with-a-code-hold-a-key-bound-to-their-name-and-say-they-are-alive.md))

## Context

A web assistant (the first one is Gemini) keeps its conversations in a product and not in a file. A satellite, a small program of its own in a container, reads them as a signed-in user and sends their turns to Mnemarr
**as a machine**, with an ingest-only key, exactly like a collector. Those conversations are an **idea repository**, not a build log: they are to be kept raw and found by words and by meaning, not turned into notes of
the memory. Mnemarr had three things in the way: a single format (Claude Code's files), a single switch for the consolidation with no filter by machine or format, and no way for a machine to say that it needs a person
(the session of a web assistant ends now and then). The contract was written by the repository of the satellite and handed over as instructions, which were checked against this code.

## Decision

1. **A neutral format, `chat-turns`.** One JSON object per line; line 0 is always a `meta` line (title, source, id at the assistant); line *n + 1* is turn *n* (`role`, `at`, `text`). The `meta` line is not a turn but it is a
   line, so positions stay aligned. Same tolerance as `claude-code`: a line that is not what is expected is skipped, never a failure; the turns are masked once decoded and cut. It is not tied to one assistant: a second
   web assistant, or the reader of a CLI whose history is a document, can use it. Documented in `docs/conversation-formats.md`.
2. **The sender's title.** A non-empty `title` of the `meta` line is the title of the conversation, with a new title source, `sender`: it is masked, on one line and at most 200 characters; **no model is asked for another**
   (the title service only picks conversations whose title is the first sentence), "rewrite all" does not touch it, and a person can still rewrite it. A rename after the first send is not propagated (the position model
   cannot update line 0): a known limit.
3. **Consolidation is a property of the machine.** A machine has an optional `consolidate` setting (the Manage button, `POST /api/v1/collectors/{machine}/consolidate`, audited); when it is not set, the format of the
   conversations decides (`ConsolidationDefaults`: `claude-code` is consolidated, every other format is not). The pending selection and the counts (the card, the readiness probe) skip a conversation of a machine that does
   not consolidate: it is never waiting, never sent to the runner. Turning it on makes its conversations eligible from their start. A conversation whose machine never joined takes the default of its format. The
   conversations that are not consolidated are still indexed for the search by meaning. There is no such setting in the configuration as code: machines are not declared there.
4. **The `attention` signal.** The heartbeat has an optional `attention` object: a `code` from a closed list in Mnemarr (`needs-login`, `reader-broken`) and a `url`. An unknown code is ignored and the heartbeat is kept.
   The address is untrusted: only `https` (or `http` to a loopback address) with a host, no credentials, no space or control character, at most 512 characters; anything else is dropped. A machine that asks is `failing`
   (an alert, when it is always-on) and the machines page shows **Mnemarr's own sentence for the code** and the link (`rel="noopener noreferrer nofollow"`, host visible); nothing else that the satellite wrote is shown. The
   next heartbeat without `attention` ends the episode; the episode belongs to the machine, not to the code (a heartbeat that goes from one code to another is the same episode, with the same mails). The code, the address, the beginning of the episode, and the number and date of the mails sent for it are stored, so that a restart does not send again.
5. **A satellite's conversations are not told to a project.** The start-of-session pack selects by application and `gemini` is not a project; the search finds them by the machine (the first folder of the note, `topic`),
   and `conversations.read` governs them like every conversation. Tests say so. Excluding them from a search is done by source or by asking another topic: a search takes one topic, it has no "not this topic".
6. **Not now**: the mail that tells a person (done in [0042](0042-mnemarr-writes-to-a-person-through-a-swappable-notification-sender.md)), a title set later by a sender's key (the maintainer asks if needed), a second kind of
   attention, a satellite for another assistant.

## Consequences

- A satellite needs no code of its own in Mnemarr: it is a machine that sends a format and a heartbeat. Mnemarr stays ignorant of Google and of any web assistant.
- The default of "not consolidated" for a new format is a safe one: the subscription is not spent on a repository of ideas by surprise. A person who wants the notes turns it on for that machine.
- The `machines` table gains six columns (the setting, the attention, its address, its beginning, the mail date and count); the `conversations` table is unchanged.
