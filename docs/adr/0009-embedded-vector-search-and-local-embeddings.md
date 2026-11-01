# 0009 — Embedded vector search and local embeddings, both behind interfaces

- **Status**: accepted
- **Date**: 2026-11-01
- **Spec**: D-25, D-26

## Context

Mnemarr searches by meaning as well as by text. Two components are involved:

- an **embedding model** turns a passage of text into a vector (a list of numbers) so that texts with similar meaning are close to each other;
- a **vector store** keeps the vectors and returns the nearest ones to a question's vector.

The expected volume is small (up to a few hundred thousand passages), the deployment must stay a single container in the *arr spirit, and a previous attempt failed partly because a separate service went down unnoticed. The vectors are derived data: they can be rebuilt from the sources at any time.

## Decision

- **The vector store is embedded in Mnemarr's SQLite database**, behind an `IVectorStore` interface. A dedicated vector database (for example Qdrant) is a possible provider later, not a requirement. The implementation is chosen by a short, time-boxed spike with realistic data: either the `sqlite-vec` extension (still pre-1.0, no official .NET binding found, a native library per architecture), or vectors stored in SQLite and compared in-process with hardware-accelerated cosine similarity.
- **Search is hybrid**: full-text ranking (SQLite FTS5) and vector similarity, merged.
- **Embeddings are computed locally** with ONNX Runtime from .NET, behind an `IEmbeddingProvider` interface, with a small multilingual model (notes mix French and English). The model is chosen by a short comparison on the maintainer's own notes, among candidates such as `multilingual-e5-small` and `Granite Embedding 97M Multilingual R2`. An external embeddings API is possible only as an explicit opt-in.
- **The model identifier is stored with every vector.** Changing the model triggers an automatic re-embedding in the background.

## Consequences

- No extra service to run, monitor or back up for vectors.
- Choosing wrongly costs a recomputation, not data.
- Two spikes are needed before the first search vertical is built (store, model); their results are recorded in this ADR or in a superseding one.
- A first run over existing history can be slow on a CPU-only host; the pipeline must be incremental and resumable.
