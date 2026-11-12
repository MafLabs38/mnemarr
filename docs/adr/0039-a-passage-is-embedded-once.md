# 0039 — A passage is embedded once: the index keeps what did not change, and the vectors are kept by the hash of their text

- **Status**: accepted
- **Date**: 2026-11-12
- **Spec**: D-60; refines D-54 ([0034](0034-search-by-meaning-is-computed-on-the-host-and-merged-with-the-words-by-rank.md))

## Context

Mnemarr, in production and in the sandbox, sometimes used about two vCPU for a while, and about a fifth of a core on average while idle (13 h 39 of CPU in 2.7 days for the sandbox;
115 000 passages computed in 24 hours). A read of the code and of the logs found one chain:

1. A collector sends a batch for a conversation that grows; `ConversationIngestor` appends the turns to the note, then indexes the note again.
2. `SqliteNoteIndex.Upsert` deleted the note and inserted every chunk again, with new identifiers. The vector of a chunk hangs on its identifier, so every vector of the note was lost.
3. `EmbeddingIndexer` computed them all again, on two threads of ONNX Runtime, with no way to tell that a text had been read before.

The cost of a batch grew with the size of the conversation, so the cost of a conversation grew with its square: a conversation of 200 turns is about 400 passages, about 40 seconds of two
cores, more than the 30 seconds between two batches. The same loss happened for a note that moved or was renamed, for an index built again, and for a new version of the chunker.
On top of it, the idle host looked for work every 15 seconds (a query over every chunk) and every probe of health counted every chunk.

What runs the model is ONNX Runtime, native code; writing the surrounding code in another language would not have made the matrix products faster. The gain is in not computing.

## Decision

1. **The index keeps what did not change.** Indexing a note that is already known updates the note and *synchronises* its chunks: a chunk with the same heading and text as one the note had keeps its
   row (and so its identifier, its words in the full-text index and its vector), whatever its place (its order is updated); the chunks that are no longer there go, and the new ones are
   inserted. A new title is the one exception: the model reads the title with the passage, so the vectors of the chunks of the note are forgotten and computed again.
2. **A vector is kept by the hash of the text the model read.** A table `embedding_cache (model, text_hash, vector, used_at)` outlives the chunks. Before the model reads a batch, the store gives each
   chunk whose text (title, heading and text) is in the cache the vector it has; of several chunks with the same text, the model reads one. A vector is saved to the cache when it is computed.
   A text that comes back (a note moved or renamed, a note removed and restored, an index built again, a title changed back) is not computed again. A cached vector that was not used for
   `Embeddings:CacheDays` (30) is forgotten at the next tidy; so are the vectors of other models once nothing uses them.
3. **The indexer is woken, it does not poll.** The index tells the indexer when chunks are new (`EmbeddingWakeUp`); the safety timer (`Embeddings:IdleSeconds`) is five minutes. The vectors of chunks
   that are gone are tidied when the index says that chunks went, and now and then, not after every pass that computed.
4. **The probes are cheap.** The progress for a caller who may see everything is two counts that read an index each, not a join over every chunk. Indexing one note does not load every note of its source.
5. **Not now**: a debounce of an active conversation (measure first), the generated regular expressions of the secret masker, the SQLite pragmas, the cache of the consolidation status, the limit of 2 MB that
   drops a very long conversation from the index (a separate defect, noted), an embedding server on a GPU (the next vertical, ADR 0040).

## Consequences

- A conversation that grows costs the passages it adds; a rebuild, a rename or a chunker change costs nothing for the passages already read.
- The cache holds one more copy of each vector (about 1.5 KB for the default model) for the days it is kept: the database grows by the size of the vectors, and the backup with it.
- The vectors computed before this change have no cache entry; the chunks that keep their rows keep their vectors, and the others are computed once, which also fills the cache.
- `Missing` now gives vectors as well as listing the chunks to compute, and `IVectorStore.Save` takes the hash of the text: a store other than SQLite must do the same.
- **Retention.** A delete that a person asked for (a note, the conversations of a machine, a forgotten source) also empties the cache of computed vectors, at the next pass of the indexer, so that nothing computed from the deleted text is left; the next reindex recomputes what it needs (seconds on a GPU server, minutes on the CPU). A file that only moves, or that disappears from its folder, does not: that is the case the cache is for. Otherwise `CacheDays` is how long an unused vector is kept, backups included.
- **What the key does not hold.** The cache is keyed by the model and the text. Settings that change what the model reads from the same text (`MaxTokens`) are not in the key: after changing them, a passage that is cached keeps its vector until it expires or the cache is emptied. Changing the model starts a new cache, as before.
