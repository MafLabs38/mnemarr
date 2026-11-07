# 0034 — Search by meaning is computed on the host and merged with the words by rank

- **Status**: accepted
- **Date**: 2026-11-07
- **Spec**: D-54; realises D-25, D-26, D-30 and D-31 ([0009](0009-embedded-vector-search-and-local-embeddings.md), [0012](0012-vector-store-and-embeddings-spikes.md))

## Context

ADR 0009 and 0012 chose the pieces: `sqlite-vec` for the vectors, Granite 97M multilingual R2 for the embeddings, hybrid search. This is the vertical that builds
them. What was left open: how the model is run in .NET, how the vectors follow the notes, how the two searches are merged, and when a passage is "near enough" to be shown.

## Decision

1. **The model runs in the process, with ONNX Runtime.** The tokenizer is Mnemarr's own byte-level BPE (about 200 lines) reading the `tokenizer.json` of the model,
   because no .NET tokenizer library reads that format with the same result; it is compared with the reference tokenizer (`tokenizers`, Python) on a set of texts
   (`reference.json`: same ids, vectors that point the same way, cosine above 0.9999), in the tests, with the real model. The pooling is the model's (first token) and
   vectors are normalised. A special token written in a text is read as plain text: a passage cannot steer the model with the markers of its format.
2. **One chunk, one vector, computed in the background.** `EmbeddingIndexer` takes the chunks that have no vector of the active model, a batch at a time, so that
   the work is incremental, resumable after a restart, and never on a request. A row `chunk_embeddings (chunk_id, model)` says which model computed which chunk and
   goes with the chunk (`ON DELETE CASCADE`): a note that changes has new chunks, which are computed at the next pass; the vectors of chunks that are gone are
   forgotten. A chunk is embedded with its note's title and its heading in front of it. A change of model computes everything again in the background and drops the
   old table when nothing uses it (one `vec0` table per model and dimension, named by a hash). While it goes on, the search by meaning covers the chunks that the new
   model has computed (the progress says how many). The text that is embedded is decided by the store; a version of that recipe, to recompute when it changes, is not
   recorded yet (not now).
3. **The two searches are merged by reciprocal rank** (k = 60), not by comparing scores: a chunk is as good as its places in the two lists. Each hit says why it is
   there (`keywords`, `meaning`, `both`). The words of a question that are in every note (*le*, *de*, *the*, *is*…) are left out of the keyword query, or a natural question
   would match every note by them; a question made of nothing else is searched as it is. The list is French and English, fixed in the code, and applies whether or not
   the search by meaning is on: a limit of this version, for the other languages the model reads. **A question of four meaningful words or more weighs its keyword list half
   as much** in the merge (`LongQuestion`): by any one of its words it matches notes that are not about it, and the meaning, which reads the whole question, is the better
   judge (seen on the first real run: noise at the second and third place); a short query is exact and keeps its full weight.
4. **A passage is shown only if it is near enough** (cosine distance at most the distance measured for the model, 0.2 for the default one, `KnownModel.MaxDistance`; an
   operator may override it with `Embeddings:MaxDistance`). The distances of this model are close together: measured on real
   notes (the documentation of the project, 470 passages), the best passage of a question the notes answer is at 0.10 to 0.16 and the best passage of a question that
   nothing answers is at 0.23 to 0.26. Another model needs its own measure. The search by meaning widens its depth (four times the answer) so that what the caller may not
   see, or the filters, do not empty the list; the scope of the caller applies to it as to the words.
5. **It can be off, and it can fail, and search goes on.** Without `Embeddings:ModelFolder` the feature is off and nothing is computed. A model whose files do not match their
   pinned SHA-256 is not loaded; a library that cannot be loaded, or a pass that fails, makes Mnemarr *degraded* with the reason (health check `embeddings`), and the answer
   of the search is by words. **The model is loaded by the indexer, never on a request or a probe**: until it is, the state is `starting` and the answer is by words. A failure
   is tried again after five minutes (a volume mounted late, a file put back). The reason that is shown to the people who ask is a fixed sentence; the detail (paths, the loader's
   words) is in the log only. `GET /api/v1/search/status` says where it is (`off`, `starting`, `ready` with the passages computed of those that exist, `failed` with why) and the notes page shows it.
   The work of a question is bounded: it is read up to 1000 characters, and the tokenizer cuts a text to what the model can read and a piece at 256 characters, so that a long
   query cannot hold a core.
6. **The library and the model are in the image**, fetched at pinned versions and verified (`tools/native`): `sqlite-vec` 0.1.9 per architecture (amd64, arm64), and
   the model at its upstream revision (390 MB). The library is checked by its SHA-256 when the image is built, and its path is trusted operator configuration (an
   operator who can swap the file can run code anyway); it is loaded by Mnemarr's code from that path, and loading is switched off on the connection straight after. The model's
   files are checked again, by SHA-256, before they are loaded, wherever they come from.
7. **Privacy.** Nothing leaves the host: the model is on disk and runs here (D-26). An external embeddings API stays an opt-in for later.

## Consequences

- The image grows by about 450 MB (the model, the runtime, the library). The first run over a long history is slow (about ten passages a second on two threads): its progress
  is on the notes page and in the health check.
- The search by meaning is tried in CI with the real model on the documentation of the project, and the end-to-end journey asks a question in other words and finds the note.
- Not now: a second model (`multilingual-e5-small` stays the documented alternative), re-ranking, an external embeddings API, vectors for the titles of notes alone.
