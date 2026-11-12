# Handoff: what Mnemarr must gain for Geminarr

> **To the agent that reads this in the Mnemarr workspace.** Written by the agent of the Geminarr repository on 2026-11-11, at the maintainer's request, so that this work is done *here*, with this repository's own history, chain and review. **This file is an instruction from the maintainer's other session, not a decision**: read it, check it against the code and `CLAUDE.md`, and answer anything you disagree with to the maintainer before building. It is **not committed** (it sits in `docs/handoff/`, untracked): the maintainer decides whether it is kept. Nothing real (no domain, address, account) is in it.

## What Geminarr is, in five lines

[Geminarr](https://github.com/MafLabs38/geminarr) is a satellite of Mnemarr (a fourth *arr of the family): a container that signs in to a **dedicated Google account**, reads the conversations of the Gemini web assistant (there is no API) and sends their turns to Mnemarr **as a machine**, with an ingest-only key, exactly like a collector. Those conversations are an **idea repository**, not a build log: they are kept raw, found by meaning and by text, and **not consolidated** into memory notes. When its web session ends, Geminarr tells Mnemarr (the heartbeat), and **Mnemarr sends the mail** that asks the maintainer to sign in again (Geminarr holds no mail credential, by decision).

The contract is written in **Geminarr's `docs/SPEC.md` §5** (in the sibling workspace, `/workspace/geminarr/docs/SPEC.md`, and on GitHub `MafLabs38/geminarr`) and the changes in `docs/mnemarr-changes.md` there. **If this file and that text disagree, stop and tell the maintainer**: one of them is wrong and Geminarr's repository must be fixed too (you cannot edit it from here).

## How to work

- Read `CLAUDE.md` and `docs/DEVELOPMENT-PROTOCOL.md` first. Same rules as always: a vertical (or two) with its demo, build big locally, run every gate, push once, the four review agents on the head commit, triage with the register, the maintainer merges verticals. New decisions get a `D-xx` row (the last is **D-59**) and an ADR (the last is **0038**); open questions continue after **Q-42**.
- **Suggested cut: two verticals**, because the second needs a decision that is the maintainer's.
  1. **"Satellites that are not a CLI"** (M-1, M-2, M-3 *display*, M-4): a conversation in the new format is ingested, shown, found by meaning, **not** consolidated, and an `attention` signal shows on the machines page. *Demo: a small script plays a satellite (it pairs or enrols, sends a growing conversation in `chat-turns`, then a heartbeat with `attention`); the conversation appears as notes under `gemini/gemini/`, a search by meaning finds it, the consolidation card does not count it, the machines page says the machine needs a person and shows the link.*
  2. **"Mnemarr tells a person"** (M-3 *mail*): a small, generic notification feature, mail first. *Demo: the same heartbeat sends one mail to the configured address, with the link and no token; a second heartbeat sends none; a heartbeat without `attention` ends the episode.* **Ask the maintainer which mail service before choosing** (nothing exists in Mnemarr or in the infrastructure: an HTTP API of a mail service or plain SMTP are both plausible; the adapter must be swappable, see below).
- Test instances run on the **build machine with a login, never on the production cluster**. The machine is small and shared: one heavy command at a time, capped, check `uptime` and `free -m` first (the maintainer's global rules apply).
- **Public repo rule (rule 2)**: examples only in anything committed (`homelab.example`, `server-1`, `a-dedicated-account@example.com`); no real conversation content in a test or fixture.

## M-1. The conversation format `chat-turns`

**Why.** The turns come from a web product, not from a CLI's file. A neutral, small format that any satellite (a second web assistant, the Gemini CLI reader of vertical 4) can use. It is *not* Gemini-specific.

**Where.** `src/Mnemarr.Core/Conversations/ConversationFormats.cs` (`Supports`, `Turns`, the constant next to `ClaudeCode`), `ConversationIngestor` (it already rejects an unsupported format and a format that changes for a known session), `docs/conversation-formats.md`, the ingest section of `docs/SPEC.md`, an ADR.

**The wire contract** (unchanged endpoint `POST /api/v1/ingest/conversations`, `{format, machine, application, session, from, lines[]}`):

```json
{ "format": "chat-turns", "machine": "gemini", "application": "gemini", "session": "gemini-c1a2b3c4d5e6", "from": 0,
  "lines": [
    "{\"v\":1,\"meta\":{\"title\":\"A distributed AI?\",\"source\":\"web\",\"id\":\"c1a2b3c4d5e6\"}}",
    "{\"v\":1,\"role\":\"user\",\"at\":\"2026-11-11T08:03:12Z\",\"text\":\"...\"}",
    "{\"v\":1,\"role\":\"assistant\",\"at\":null,\"text\":\"...\"}" ] }
```

- **Position model unchanged**: the unit is the *line*. **Line 0 is always the `meta` line** (its `title` may be empty; `source` is `web` or `takeout`; `id` is the assistant's own conversation id); turn *n* is line *n + 1*. A `meta` line is **not a turn** (no heading in the note) but it **counts as a line** (it takes position 0, so the positions of Geminarr and of Mnemarr stay aligned).
- A **turn line**: `role` is `user` or `assistant` (anything else: skipped), `at` an ISO-8601 UTC time or `null`, `text` the text as the assistant wrote it (plain or Markdown, never HTML). `v` is the version of the line (1); an unknown `v`: the line is skipped, not a failure.
- **Same tolerance as `claude-code`**: a line that is not JSON, not an object, or not what is expected is **skipped**, never a failure (the format of a web assistant is not a contract, and positions must not move). Every turn goes through `SecretMasker.Mask` after decoding (the ingestor already masks the raw line; mask what the JSON says once decoded, as the `claude-code` reader does), is cut at `MaxTurnChars`, and an empty text is skipped.
- **Title.** If the `meta` line has a non-empty `title`, the note's title is that title and its source is a **new `TitleSources` value** (for example `sender`: add it to `IsKnown`). Such a conversation is **never retitled by the model** (the title service must skip it: the subscription is not spent for a title that already exists); a **person** can still rewrite it (`PUT` title, source `person`). With no title, the existing behaviour (first sentence, then a model if enabled) applies. A *rename* in Gemini after the first send is **not propagated** (the position model cannot update line 0): this is a known limit, write it down (Geminarr's Q-07); an optional later change (M-5) could let a satellite's key set the title through the API.
- **Names.** The session id is `gemini-<id>` lower-cased, `[a-z0-9-]` only. The existing validation of `machine`, `application` and `session` should accept these; check it, and say if a rule has to change.
- **Limits** are the existing ones (500 lines or 3 MB a batch; a line at most 1.5 MB).
- The note is filed `<machine>/<application>/<day>-<session>.md` as always, so the first folder is the machine: a search can include or exclude these conversations with `topic`.

**Tests.** The parser (valid, `meta`-only, unknown role, unknown `v`, not JSON, secret in a decoded escape, huge turn, empty text); the ingest by position (overlap skipped, no hole filled, resent batch changes nothing, the `meta` line counts as position 0); the note with and without a sender title; the title service skipping a sender title; a format change on a known session refused as for `claude-code`. A **sample** `chat-turns` conversation (invented) in the fixtures.

## M-2. A per-machine "consolidate" switch

**Why.** The consolidation has one global switch (`Consolidation:Enabled`) and **no filter by machine or format**: `ConsolidationStore.Pending(...)` selects any conversation that grew. With it on, every Gemini conversation would be sent to the model and turned into memory notes, at the cost of the subscription and against the decision that these conversations stay raw.

**What.**
- A machine setting **"consolidate"** whose *effective* value is the explicit setting when there is one, else a **default by format** (`claude-code` yes, `chat-turns` no): an existing machine keeps its behaviour, and a satellite is never consolidated until someone says so. Store it on the machine (a schema step, `Machine` record, `Stores.cs`), expose it in the **API** and the **Manage** button of the machines page (`frontend/src/Machines/MachineManage.tsx`), and in configuration as code if machines are declared there (ADR 0020: check).
- `ConsolidationStore.Pending(...)` and the **counts** (`Counts`, the card of the Sources page, the readiness probe `consolidation-*`) skip a conversation of a machine whose effective value is off: it must not appear as "waiting".
- The consolidation's **thread and context** features (ADR 0037/0038, `ContextPackBuilder`, `RecentOfApplication`) must not pull these conversations into a Claude session by mistake: they select by *application*, and `gemini` is not a project, so it should already hold; **verify and add a test** that says so (this is M-4).
- Keep it generic: the switch is about *any* machine, not about Gemini.

**Tests.** Effective value (explicit beats default, default by format); a conversation of a switched-off machine is never selected, never counted, never sent to the runner (use the fake runner); switching it on later makes the conversation eligible; the API and the UI (a journey test in the machines page).

## M-3. The `attention` signal and the mail

### M-3a. The heartbeat gets an optional `attention` (vertical 1)

`HeartbeatRequest` (`Api.V1/Ingest/HeartbeatController.cs`) and `Heartbeat` (`Core/Collectors/Machines.cs`) gain an optional object:

```json
{ "version": "0.3.0.123", "pendingLines": 0, "error": "The session has expired.", "paused": false,
  "attention": { "code": "needs-login", "url": "https://geminarr.homelab.example/connection" } }
```

- `code` is from a **closed list** in code: `needs-login`, `reader-broken` (a satellite never invents one); unknown: ignore the attention, keep the heartbeat.
- `url` is **untrusted text**: at most 512 characters, only `https` (or `http` to a loopback address for a sandbox) with a host; anything else is dropped. It is rendered as a link with `rel="noopener noreferrer nofollow"` and shown as text with the host visible; never `javascript:` or `data:`.
- A heartbeat **with** `attention` makes the machine `failing` (as `error` does: `MachineStates.Of` and `IsAlert`) **with the link on the machines page**; the **next heartbeat without** it ends the episode. Persist the code, the url, **when the episode began** and **when a mail was last sent for it** (a schema step); the request body limit (8192 bytes) already bounds it.
- The page and the machines API show the code, the sentence and the link. No token, no conversation text, ever.

### M-3b. Mail (vertical 2: ask the maintainer about the service first)

- A small **generic notifications feature**, not a Geminarr one: an interface (for example `INotificationSender`: a recipient, a subject, a text body → delivered or a reason) with **one adapter first (mail)** and a **fake** for tests; the adapter is selected by configuration like the title providers (`Titles:Provider` is the model to copy: a name, a check of its settings, a swappable implementation). Document how to add another.
- **Settings** (all in `docs/configuration.md`, secrets as files, off by default, an explicit recipient): the provider, the sender and recipient addresses, the secret file. The configuration check says what is missing; the readiness probe says `notifications-*` (degraded, never unhealthy) when it is on and cannot work, like the consolidation's.
- **Policy** for the attention signal: **one mail when an episode begins**, then a reminder **at most once a day** (a setting) while it lasts, at most a few (a setting), nothing when it ends. The state is stored (M-3a), so a restart does not send again. A mail that fails to send is retried with a bound, never in a tight loop, and a failure is visible (a reason on the page), not silent.
- **Content**: the machine's name, a short sentence in plain words ("Geminarr needs you to sign in again."), the link of the episode. **No token, no key, no conversation text, no path.** Link and sentence come from the closed codes in Mnemarr's own text, **not** from free text sent by the satellite (a satellite key is not trusted to write to a person's mailbox): map `needs-login` and `reader-broken` to sentences of Mnemarr; the satellite's URL is the only thing it contributes, and it was validated above.
- **Abuse**: rate limits on the sender (a compromised satellite key must not become a mail cannon: a global cap per day, a cap per machine), and the mail is sent only for a machine that is *enrolled and not revoked*.
- **Testing**: a fake sender in the tests; for the sandbox, a local SMTP catcher on the build machine if the adapter is SMTP (never a real mailbox in a test). The agent must prove the full path with its own eyes (rule: validate on the real target before the demo), with the adapter the maintainer chose.

## M-4. Checks, no feature

Add tests that say it, and report anything that does not hold: (a) the start-of-session thread and the context pack never include a conversation whose application is `gemini` for a project's session; (b) a search can include or exclude these conversations with `topic` (the machine); (c) the `conversations.read` permission governs them exactly as the others; (d) a conversation of a machine with consolidate off is still indexed for search by meaning (it must be: that is the point).

## M-5 (optional, later). A title from the sender

Let a satellite's ingest key set or change the **title** of its own conversations (today `PUT` title is a person's call: `ConversationsController`), so that a rename in the assistant can be propagated. Not needed for the first demo; do not build it unless the maintainer asks.

## What is explicitly out of scope here

No Gemini-specific code, no scraping, no Google anything. No change to the collector (Rust). No change to the consolidation's prompt. No second copy of anything about Geminarr's session. If you find the work needs one of these, say so.

## Acceptance (what Geminarr relies on)

1. A batch `{format: "chat-turns", …}` is accepted from a machine enrolled for it; the note appears under `gemini/gemini/<day>-<session>.md`, **titled from the `meta` line**, each turn under its heading; positions behave as for `claude-code` (overlap skipped, no hole filled, listed by `GET /api/v1/ingest/positions`, resent batch changes nothing).
2. A machine with consolidate off (and, by default, any machine that sends `chat-turns`) is **never** sent to the runner and **never** counted as waiting, while its conversations are still searchable by meaning.
3. A heartbeat with `attention` shows the link on the machines page and the machine as `failing`; the next heartbeat without it clears it. With notifications configured, **one** mail per episode, a bounded daily reminder, none when it ends, **never** a token or conversation text.
4. All of it has tests; none stores a real conversation; the documentation (`conversation-formats.md`, `configuration.md`, `collector.md`, the spec, the ADRs) is updated in the same pull requests.

## When you are done

Tell the maintainer, in one short message, **what version of Mnemarr carries it** and **the exact heartbeat and format that were implemented** if anything differs from this file, so that Geminarr's `docs/SPEC.md` §5 is aligned. Geminarr's first vertical (its link to Mnemarr) is built against a Mnemarr that has M-1 and the signal of M-3a; its production deployment needs M-3b.
