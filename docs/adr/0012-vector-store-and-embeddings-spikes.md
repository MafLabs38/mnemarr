# 0012 — Vector store and embeddings model: results of the two spikes

- **Status**: accepted
- **Date**: 2026-11-03
- **Spec**: D-30, D-31; settles the two open choices of [0009](0009-embedded-vector-search-and-local-embeddings.md)

## Context

[0009](0009-embedded-vector-search-and-local-embeddings.md) decided that vectors live inside SQLite, behind an `IVectorStore` interface, and that embeddings are computed locally behind an `IEmbeddingProvider`. It left two choices to short spikes with realistic data: the implementation of the vector store, and the embeddings model. Both were run on a 4-core virtual machine with little spare memory, each capped to 2 CPU cores.

## Spike 1: the vector store

**Method.** Random unit vectors of 384 dimensions (the size of the candidate models), 10 nearest neighbours, 40 queries, three volumes. A brute-force search costs the same whatever the data, so random vectors time it fairly. Two implementations, filled from the same data in one transaction:

- **In-process**: vectors as BLOBs in a plain table, loaded into one array in memory, compared with SIMD (`System.Numerics.Tensors`).
- **`sqlite-vec` 0.1.9**: a `vec0` virtual table, searched inside SQLite, loaded as an extension through `Microsoft.Data.Sqlite`.

**Results** (median query latency; resident memory of the process):

| Vectors | In-process | `sqlite-vec` | Memory, in-process | Memory, `sqlite-vec` |
|---|---|---|---|---|
| 20,000 | 2.9 ms | 13.8 ms | 64 MB | 72 MB |
| 100,000 | 19.8 ms | 66.4 ms | 219 MB | 75 MB |
| 300,000 | 40.6 ms | 181.7 ms | 528 MB | 84 MB |

Both are fast enough for an interactive search at the expected scale. The in-process search is about four times faster but keeps every vector in memory, so its memory grows with the volume (about 1.5 KB per vector); `sqlite-vec` stays near 80 MB at any volume, on disk. `sqlite-vec` publishes loadable binaries for Linux x86-64 and aarch64 (and Windows, macOS), each with a checksum.

Not measured, and left to the tests of the search vertical: filtering by metadata (source, application) inside `vec0`, and concurrent writes while searching.

## Spike 2: the embeddings model

**Method.** A Markdown vault of 69 French course notes, cut into 657 passages by section (median 608 characters), and 38 natural questions written without reusing the notes' titles, each with the note or notes that answer it. A note is found when it ranks among the best by its best passage. Two small multilingual models with official ONNX files, run with ONNX Runtime on 2 threads, against a lexical baseline (BM25) and against the two combined by reciprocal rank fusion. EmbeddingGemma was left out: its license (Gemma terms) does not suit the likely GPL-3.0 license of the project (still open, Q-20, and to reconsider if it lands elsewhere).

| Method | Right note first | In the first 3 | In the first 5 | MRR | Passages per second | Query |
|---|---|---|---|---|---|---|
| BM25 (words only) | 50 % | 76 % | 87 % | 0.67 | - | - |
| `multilingual-e5-small` (MIT) | 68 % | 89 % | 92 % | 0.79 | 11.3 | 9 ms |
| `granite-embedding-97m-multilingual-r2` (Apache-2.0) | 68 % | 89 % | 92 % | 0.79 | 10.1 | 10 ms |
| e5-small + BM25 | 68 % | 84 % | 97 % | 0.80 | - | - |
| **Granite + BM25** | 68 % | 92 % | 97 % | 0.81 | - | - |
| Granite, quantized to uint8 | 55 % | 79 % | 87 % | 0.69 | 10.5 | 8 ms |

**What it shows, and what it does not.**

- **Meaning beats words**: 18 points more on the first place, and an MRR of 0.79 against 0.67.
- **The two models are tied on their own.** Granite is a little better combined with a lexical search, but one question is worth 2.6 points: the gap is **within the noise** of 38 questions, and the choice does not rest on it.
- **Quantizing loses quality** (MRR 0.69, under the full-precision e5) without a measurable gain in speed here.
- The expected notes are the author's judgement; some misses, shared by both models, are arguably another valid note.
- At about 10 passages per second on 2 threads, embedding 10,000 passages takes 15 minutes and 100,000 takes close to three hours: the first run over an existing history is slow, and the pipeline must be incremental, resumable and in the background.

## Decision

- **Vector store**: `sqlite-vec`, **pinned** (0.1.9), behind `IVectorStore`. In the image, the binary is fetched by version for amd64 and arm64 and **its checksum is verified**. Memory stays flat, filtering by source and application belongs to the same SQL, and the latency at 300,000 vectors on two cores is acceptable. The in-process search stays a documented alternative implementation of the same interface if `sqlite-vec` turns out to be a problem (it is not 1.0 yet).
- **The store is keyed by (model identifier, dimension).** A `vec0` table has a fixed dimension, and a change of model, a smaller Matryoshka vector or an external API gives another one. So there is one `vec0` table per model, created on demand: search reads the active model's table while a re-embedding fills the next, then the old table is dropped. This is part of the contract tests of `IVectorStore`, which any implementation, the in-process one included, must pass.
- **Embeddings model**: **`granite-embedding-97m-multilingual-r2`**, full-precision ONNX, 384 dimensions, behind `IEmbeddingProvider`. On quality it ties with `multilingual-e5-small`, so the choice rests on the other points: a context of up to 32,768 tokens (conversation passages are long; e5 stops at 512), Matryoshka dimensions (a smaller vector is possible later), Apache-2.0, and an official ONNX export. `multilingual-e5-small` (MIT, the most used) is the supported alternative. **No quantized model.**
- **`IEmbeddingProvider` tells a query from a passage** (two operations, or an input kind). e5 needs the prefixes `query:` and `passage:` and Granite needs none: each provider applies its own, so adding one never changes a caller.
- **The model is configured, not hard-coded**: the model identifier, where its files come from and the vector dimension are settings (environment or the settings API) with documented defaults. Only the listed models are supported. The model identifier stays stored with every vector (0009), so a change of model re-embeds in the background.
- **The model is pinned like `sqlite-vec`**: a fixed upstream revision (a commit, not a branch) and a SHA-256 per file, verified before the model is loaded, wherever it comes from (baked into the image or downloaded). On a mismatch Mnemarr refuses to embed and says why.
- **The extension is loaded by Mnemarr's code, from the pinned path in the image, and extension loading is then switched off on the connection.** No path comes from configuration or from input, and every filter of a `vec0` search is a bound parameter, so that a later injection cannot become `load_extension`.

## Consequences

- The search vertical can start: the store and the model are chosen, and what is left to verify is named (metadata filtering, concurrent writes).
- A native extension joins the image: its version, its checksum and its architectures (amd64 and arm64) are part of the build, and of the supply-chain review.
- The model files (about 390 MB) are **baked into the image by default**, so a first start needs no network and an air-gapped host works. If they are downloaded instead, the host contacted is documented, its address can point to a local mirror, and the health page says plainly when it cannot be reached.
- The first embedding run is long (hours, for a large history): its progress is shown on the health page (F-HLT-01), since the previous attempt at this product failed silently.
- The evaluation is small and French. Its method is described above so that it can be repeated on another vault, with a list of questions and their expected notes; the data and the scripts are not committed, because the notes are the maintainer's private ones. It should be run again on a bigger set, and on conversations, once the first real data exists.
