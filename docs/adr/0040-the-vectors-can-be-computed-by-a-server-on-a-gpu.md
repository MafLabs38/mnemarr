# 0040 — The vectors can be computed by a server on a GPU, and Mnemarr falls back to its own CPU

- **Status**: accepted
- **Date**: 2026-11-12
- **Spec**: D-61; extends D-26 and D-54 ([0009](0009-embedded-vector-search-and-local-embeddings.md), [0034](0034-search-by-meaning-is-computed-on-the-host-and-merged-with-the-words-by-rank.md))

## Context

The search by meaning reads every passage with a neural model (97 million parameters). On two CPU threads that is about ten passages a second. The first run over a long history, a change of model, and a
host with little CPU to spare are slow, and a GPU does the same work about thirty times faster. The maintainer has an NVIDIA GPU on the hypervisor that runs the homelab; Mnemarr runs on a Kubernetes cluster,
which cannot use that GPU: the cluster nodes are virtual machines, and passing the card through to one takes it away from the host and from the other containers.

Two shapes were considered: move Mnemarr to the host with the GPU (one container, but it leaves the deployment pipeline, the sign-in in front of it and the backups of the cluster, and a stopped GPU host
stops the memory), or keep Mnemarr where it is and add a small server that computes vectors next to the GPU.

## Decision

1. **A separate embedding server, for the people who have a GPU.** `Mnemarr.Embedder` is a small web program: `POST /v1/embeddings` (texts in, vectors out) and `GET /health`. It holds the model, nothing else: no
   database, no text kept, no log of a text. It is in this repository and built from the same code (`OnnxEmbeddingProvider`), so there is one implementation of the tokenizer, the pooling and the checks, and the
   model files are verified by the same hashes. It ships as its own image (`Dockerfile.embedder`, linux/amd64): the GPU build of the engine is a few hundred megabytes and the CUDA libraries a gigabyte more, which
   the image of Mnemarr, built for two architectures and for everybody, does not carry. *Mnemarr keeps its logic and its data; the server only turns texts into vectors.*
2. **The model can run on a CUDA GPU** (`Embeddings:Device`: `Cpu`, `Cuda`, `Auto`). The memory it may take is capped (`Embeddings:GpuMemoryMb`), because the pool of the engine otherwise grows without a limit and
   the GPU may be shared with a media server. `Auto` uses the GPU when the engine can and the CPU when it cannot; the server image defaults to `Auto`, the image of Mnemarr to `Cpu`.
3. **Mnemarr can use a server** (`Embeddings:Remote:Url`, `KeyFile`, and bounds). The provider is a failover: the server when it answers, **the CPU of the host when it does not**, and the server again after
   `RetryAfterSeconds`. The indexing never waits for the GPU and never stops: a deployment without a GPU, or whose server is down or misconfigured, works as before. The model of the host is read only if it is
   needed. The server's answer is checked before it is used: the model, the number and the size of the vectors, and that every value is a number.
4. **The key is a bearer token in a file**, never a value in the configuration; the server compares it in constant time and keeps only its hash in memory; it does not start without it. Mnemarr requires
   `https` (or `http` to the same host, or an explicit `AllowInsecureHttp` for a network the operator trusts), refuses credentials in the address, and never follows a redirect, so the key cannot be sent elsewhere.
5. **Same model, interchangeable vectors.** Both sides use the same pinned files and the same code; the cache of computed vectors ([0039](0039-a-passage-is-embedded-once.md)) does not depend on where a vector was
   computed. A client that expects another model is refused (`409`), not given vectors of another space.
6. **Visible.** The status says where the passages are read (`device`: `cpu`, `cuda` or `unknown`, never text from the server) and, when a server is set, whether the last passages came from it and why it was left alone when it was (`server.inUse`, `server.problem`, a sentence of Mnemarr's), on the notes page and in `GET /api/v1/search/status`.

## Consequences

- **It relaxes D-01 (a single container) knowingly.** The embedding server is an optional second container from the same repository; the single container still works alone, and Mnemarr reaches the server only through its API (D-02). Do not cite it as a precedent for other helpers inside the product.
- The request carries `maxTokens` and the server refuses (409) a client that reads another length of text: the same model with another truncation gives other vectors, and the cache of Mnemarr relies on them being the same. A question is given at most five seconds, so that a hung server does not make a search wait.
- With a GPU, a long history is read in minutes and a question is embedded in about ten milliseconds; the CPU of the host is left alone. Measured on a GeForce RTX 2070: 250 to 330 passages a second.
- One more image and one more container to run for the people who want it, with its own secret. The image pins the CUDA 13 libraries of NVIDIA's runtime image by digest and the engine's GPU package by SHA-256;
  moving the engine version means moving `tools/native/fetch-onnx-gpu.sh` and `Directory.Packages.props` together.
- The texts to embed travel to the server: the network between the two is inside the boundary the operator trusts. It is opt-in; nothing leaves the host by default (D-26).
- Not now: a pool of servers, the vectors of queries cached, TensorRT, a model other than the one pinned, an arm64 build of the server, an API key per client.
