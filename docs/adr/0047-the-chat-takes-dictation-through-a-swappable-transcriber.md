# 0047 — The chat takes dictation through a swappable transcriber, with a catalog of models as configuration

- **Status**: accepted
- **Date**: 2026-11-26
- **Spec**: D-68; extends D-55 ([0035](0035-the-chat-is-a-panel-a-proxy-and-a-runner-with-adapters-for-any-model.md))

## Context

The chat is where a person tells Mnemarr things ("remember this", and soon "put this idea in the incubator", a later decision). On a phone, typing is
what stops it: the maintainer already dictates everything on the desktop with a small program of his own that records, sends the sound to a transcription model and pastes the text. The same thing, in the
browser, makes Mnemarr usable from a phone: open the page, press the microphone, speak.

A transcription model is either a paid API (OpenAI's `/v1/audio/transcriptions`, a fraction of a cent a minute) or a local server (Whisper, on a GPU). Model names change every few months, so the list of models must
not be code.

## Decision

1. **A microphone in the chat's composer.** A tap starts recording in the browser (`MediaRecorder`, the format the browser gives: webm/opus or mp4), a second tap stops, a cross cancels; the elapsed time shows. The
   text that comes back is **inserted in the field at the cursor and never sent by itself**: the person reads it, fixes it, sends it. The button is there only when transcription is on and the page is a secure
   context (a browser gives the microphone only to HTTPS or `localhost`). The chat panel becomes usable on a phone (full screen under 768 px) in the same vertical: dictation is for the phone.
2. **One endpoint, for a person.** `POST /api/v1/chat/transcriptions` takes the recording (multipart, at most `Transcription:MaxMegabytes`, 10 by default, a 413 above) and returns `{ text }` or a reason. Permission
   `chat.use` and a session of a person: keys of services cannot transcribe. The sound is held in memory, sent to the transcriber and dropped: **never stored, never logged, never indexed**. The text is
   returned to the person only; it becomes part of a conversation only if they send it.
3. **A swappable transcriber.** `ITranscriber` (sound, its content type, an optional language → text, or a short reason; it never throws for a transcription that fails), chosen by `Transcription:Provider` like
   the notification sender ([ADR 0042](0042-mnemarr-writes-to-a-person-through-a-swappable-notification-sender.md)). **One provider first, `OpenAiCompatible`**: `POST {BaseUrl}/audio/transcriptions` with the model of
   the catalog, `BaseUrl` `https://api.openai.com/v1` by default. Any server that speaks the same API (a local Whisper server, a gateway) is the same provider with another address: no code to add. The key is a
   **file** (`Transcription:OpenAiCompatible:KeyFile`), read at each call so it can be rotated, never an environment variable, never returned.
4. **The models are a catalog in a file of their own, the choice is a setting.** `Transcription:ModelsFile` names a YAML file of the deployment (a mounted ConfigMap, a file of a repository), read again
   whenever it changes, read strictly like the configuration document (one document, no anchors or tags, no unknown key, at most 50 models and 64 KB):
   ```yaml
   models:
     - { id: gpt-transcribe, label: "GPT Transcribe", pricePerMinute: 0.0045 }
     - { id: gpt-4o-mini-transcribe, label: "GPT-4o mini Transcribe", pricePerMinute: 0.003 }
     - { id: gpt-4o-transcribe, label: "GPT-4o Transcribe", pricePerMinute: 0.006 }
     - { id: whisper-1, label: "Whisper", pricePerMinute: 0.006 }
   ```
   Without the file, the catalog is the selected provider's own list (each provider brings its models: a second service does not start with OpenAI's names and prices); a
   file that cannot be used falls back to it, and the Settings page says why. A new model is one line of the file, not a release. It is not a section of the
   configuration document ([ADR 0020](0020-configuration-as-code.md)): that document is the wanted state of the sources, applied to the database with a plan, owners and removals; a list of models is reference
   data that nothing applies, and a section would have to carry those rules for nothing. Which model is used is chosen on the **Settings** page,
   among the catalog's, by an administrator (`settings.manage`), with three layers as for the identity ([ADR 0044](0044-the-menu-says-which-environment-it-is-with-a-name-a-logo-and-the-version.md)): the interface,
   then the deployment (`Transcription:Model`), then the first model of the catalog (`GET /api/v1/transcription`, `PUT /api/v1/transcription/model`, audited). A model removed from the catalog falls back to the next layer, and the page says so.
   The panel asks `GET /api/v1/chat/transcriptions/status` whether to show the microphone. The two roots are deliberate: `/api/v1/chat/transcriptions` is what a person
   uses (`chat.use`), `/api/v1/transcription` what an administrator sets (`settings.manage`); a later capability of the same kind (speaking the answers back) follows the same split. An optional `Transcription:Language`
   (an ISO code) is passed as a hint; without it the model detects the language.
5. **Off by default, and it says what it costs.** Nothing leaves the host until the operator sets a provider and a key: that is the opt-in of rule 5, and `docs/configuration.md` says that the sound goes to the
   provider. The Settings card shows the provider, the host it sends to, the model and its price per minute. A cap holds whatever happens: `Transcription:MaxPerPersonPerDay` (100 by default) recordings a person
   can send in 24 hours, then a 429 with a sentence and a `Retry-After` that says when a place frees. The place is taken **before** the service is called, in the same statement
   that counts, so that recordings sent at once cannot all pass; a call that is cancelled while the service works still counts (the service may bill it). Every attempt is audited (who, size, model, the duration the provider reports when it reports one, the result), never with the text.
6. **It is visible when it breaks.** The readiness probe `transcription` is degraded, never unhealthy, with `transcription-secret` (the key file cannot be read) or `transcription-failing` (the last call failed on the
   service's side: the key refused, the quota spent, the address wrong, no answer in time); a recording the service refused says nothing about the setup and does not count, and
   a later success clears it. Off, it is healthy. The API keeps the same line: a recording the service refused is a 422
   (`recording-refused`: speak again), a failure on its side a 502 (`transcription-failed`: the operator has something to fix). In the panel, a failure is Mnemarr's sentence, never
   the provider's text.

## Consequences

- From a phone, Mnemarr becomes something to talk to; the incubator, a later decision, will be fed this way.
- The paid key is behind the interface: behind Mnemarr's own session with `chat.use`, the cap per person per day and the audit, which Mnemarr holds itself, and behind the single
  sign-on that is recommended in front of the interface ([ADR 0046](0046-the-start-of-session-hook-is-published-like-ingestion-and-mcp.md)), which Mnemarr cannot see.
- One more outbound call, opt-in, on a path the operator names. The browser's own speech recognition was not used: on Chrome it also sends the sound to a third party, without a choice of model, and its quality in
  French is lower.
- A test environment needs HTTPS for the microphone: it is reached through its HTTPS address behind the proxy.

## Alternatives considered

- **Transcribing in the browser** (a model in WebAssembly): heavy on a phone, slow, and a second copy of a model per device.
- **The key in the browser**: anyone with the page could read it and spend it.
- **The list of models in code, or in an environment variable**: a release, or a restart, for each new model.
- **A section of the configuration document**: see point 4.
