# 0045 — The embedding server gives the video memory back: after each batch, and entirely when nobody asks

- **Status**: accepted
- **Date**: 2026-11-21
- **Spec**: D-66; extends D-61 ([0040](0040-the-vectors-can-be-computed-by-a-server-on-a-gpu.md))

## Context

The embedding server of [0040](0040-the-vectors-can-be-computed-by-a-server-on-a-gpu.md) runs on a GPU that other things share (a media server, an image generator). Left alone for a day it held 2.8 GB of
a card of 8 GB, after 730 MB when it started: the engine keeps the memory it took for as long as its session lives, and the card ran short for the others. A restart gave everything back, so nothing was
stored there: the vectors are in Mnemarr's database, the server keeps no text and no vector.

Measured on a GeForce RTX 2070, with the image of 0.4.0 and then with this change:

- **The pool fills up and is never given back.** With the memory pool capped (`GpuMemoryMb`) and batches of texts of several lengths, the engine takes blocks of the sizes it is asked for, and a batch of another size
  cannot reuse them. The server went from 522 MB to its cap in about a hundred batches, then **every request failed** (`503`, Mnemarr computing on its own CPU) until it was restarted. The failure is silent for the
  person: the search goes on, slower.
- **Giving back what a batch does not use** (a setting of the engine, applied to each batch) keeps the server at 570 MB over 260 batches, without a failure, at the same speed (135 passages a second against
  127, measured with 32 passages of 512 tokens per request).
- **Letting go of the session** when nobody asks takes the process from 570 MB to 98 MB (what is left is the context of the driver). Building it again takes 0.7 s; the vectors are the same (cosine 1.000000
  against the first ones), on eight cycles, with no creep.

## Decision

1. **The engine gives back the memory a batch does not use** (`Embeddings:GpuShrinkAfterRun`, on by default): after each run the unused blocks of the pool go back to the driver. The pool no longer fills with blocks
   of the wrong sizes, and the card is not held between two batches. It only applies to a GPU.
2. **The server lets go of the model when nobody asks** (`Embedder:IdleUnloadMinutes`, 10 by default, 0 to keep it loaded). A check every minute at most; a run that is going on is never cut. The files of the model
   were verified when the server started and are not read again; the next request builds the session again (about a second) and counts as a use. This is one lock with the runs, so an unload and a request never meet.
3. **A refused card is not a failure.** If the card cannot give the memory when the session is built again, the server in `Auto` mode computes on its CPU and says so (`device: cpu`), as it already does at start; it
   tries the card again the next time it lets go of the model.
4. **No restart, no cron, no second program.** Both mechanisms are in the server, so that it holds for someone who runs it without a restart policy, and Mnemarr is unchanged: a first request after a long silence takes
   about a second longer, well under the delay Mnemarr waits for the server.

Not done: rewriting the server in another language (the memory is the engine's and the driver's, not the language's), and an exit of the process with a restart by the container runtime (it would give back the
last 98 MB at the price of a dependency on a restart policy).

## Consequences

- At rest the server holds about 100 MB of a card instead of up to 2.8 GB; in service it holds about 570 MB and does not grow with the days.
- The first search after a long silence is slower by about a second (75 ms warm, 1.35 s measured after a rest, same results, still on the GPU).
- `GpuMemoryMb` stays as a ceiling; with the unused blocks given back it is no longer reached by a long run.
- A person who wants the old behaviour sets `Embedder:IdleUnloadMinutes` to 0 and `Embeddings:GpuShrinkAfterRun` to false.
