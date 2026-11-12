# The embedding server (GPU)

Mnemarr computes the vectors of the search by meaning itself, on the CPU of its own container ([ADR 0034](adr/0034-search-by-meaning-is-computed-on-the-host-and-merged-with-the-words-by-rank.md)). That is enough for a small memory. For a large one, or to leave the CPU of the host to something else, the same model can run on a **GPU**, in a small separate container, the **embedding server**. Mnemarr keeps its logic, its database and its notes; the server only turns texts into vectors ([ADR 0040](adr/0040-the-vectors-can-be-computed-by-a-server-on-a-gpu.md)).

```text
Mnemarr (any host, a cluster)  ──  texts  ──▶  embedding server (a host with an NVIDIA GPU)
        ▲                          ◀── vectors ──┘
        └─ if the server does not answer: computes on its own CPU, and asks again later
```

The vectors are the same model, the same files, checked by hash on both sides: what the server computed and what the CPU computed are interchangeable, and the cache of computed vectors ([ADR 0039](adr/0039-a-passage-is-embedded-once.md)) is valid for both.

On a GPU, about 250 to 330 passages a second were measured on an RTX 2070, against about 10 on two CPU threads. A question is answered in about 10 ms over a local network.

## What it needs

- A host with an **NVIDIA GPU** (Turing, the RTX 20 series, or newer), a driver that supports **CUDA 13** (580 or newer), and the **NVIDIA container runtime**. On Unraid: the *Nvidia Driver* plugin, and run the container with `--runtime=nvidia`.
- The GPU must not be reserved for a virtual machine (VFIO passthrough takes it away from Docker).
- About 520 MB of video memory for the model, plus a work area you cap with `Embeddings:GpuMemoryMb`. The GPU can be shared: a media server that transcodes uses a separate circuit of the card (NVENC), not the compute units.
- Mnemarr must reach the server: an address, and a key.

## Run the server

An example for a Docker host is in [deploy/docker-compose.embedder.example.yml](../deploy/docker-compose.embedder.example.yml). The image is `mnemarr-embedder` (linux/amd64 only: the GPU build of the engine is published for that). To build it from this repository: `docker build -f Dockerfile.embedder -t mnemarr-embedder .`.

1. Make a key, a file with a random string of at least 16 characters, one line, readable by the user of the container (uid 1654). It is the only secret.
2. Start the container with the NVIDIA runtime and the key file (`MNEMARR__EMBEDDER__KEYFILE`).
3. Check it: `GET /health` answers `{"status":"ready"}` once the model is loaded, and the log says `loaded, on the cuda`.

| Variable | Default | Meaning |
|---|---|---|
| `MNEMARR__EMBEDDER__KEYFILE` | none, required | The file with the key a client must present. The server does not start without it. |
| `MNEMARR__EMBEDDER__MAXTEXTS` | `64` | The most texts in one request. |
| `MNEMARR__EMBEDDER__MAXTEXTCHARS` | `8000` | The longest text, in characters. |
| `MNEMARR__EMBEDDER__MAXBODYBYTES` | `2097152` | The largest request body. |
| `MNEMARR__EMBEDDINGS__DEVICE` | `Auto` in the image | `Cuda` (the GPU or nothing), `Auto` (the GPU when the engine can use it, else the CPU), `Cpu`. |
| `MNEMARR__EMBEDDINGS__GPUMEMORYMB` | `1024` | The most video memory the model may take (256 to 262144). |
| `MNEMARR__EMBEDDINGS__GPUDEVICEID` | `0` | Which GPU. |
| `MNEMARR__EMBEDDINGS__BATCHSIZE` | `8` | Passages computed together. A GPU likes more (32). |

The server keeps nothing: no text, no vector, no log of either. It answers `401` to a client that does not hold the key, `409` to one that expects another model, `400` to a request that is empty or too big, and `503` when the model fails (without saying on what).

## Point Mnemarr at it

On the Mnemarr that should use it:

| Variable | Default | Meaning |
|---|---|---|
| `MNEMARR__EMBEDDINGS__REMOTE__URL` | none | The address of the server. Empty: the vectors are computed here. |
| `MNEMARR__EMBEDDINGS__REMOTE__KEYFILE` | none, required with a URL | The file with the key of the server. |
| `MNEMARR__EMBEDDINGS__REMOTE__TIMEOUTSECONDS` | `30` | The longest Mnemarr waits for an answer before it counts the server as down (1 to 300). |
| `MNEMARR__EMBEDDINGS__REMOTE__RETRYAFTERSECONDS` | `60` | How long Mnemarr computes on its own CPU after a failure before it asks the server again (5 to 3600). |
| `MNEMARR__EMBEDDINGS__REMOTE__ALLOWINSECUREHTTP` | `false` | Allows `http` to an address that is not this host. The key is then sent in the clear: only on a network you trust. |

The address must be `https`, or `http` to this host, or `http` with `AllowInsecureHttp`. No credentials in the address; a redirect is never followed, so the key cannot be sent elsewhere.

The model files must still be in the Mnemarr image (`Embeddings:ModelFolder`): they are what Mnemarr computes with when the server is down.

## What happens when the server is not there

The indexing never waits for the GPU. When the server does not answer (down, unreachable, refusing the key, another model), Mnemarr computes on its own CPU, as it did before, and asks the server again after `RetryAfterSeconds`; the next batch goes back to the server when it is up. The notes page says where the passages are read (`cpu`, `cuda`, or `remote (cuda)`), and `GET /api/v1/search/status` gives it as `device`. Without a GPU at all, set nothing: it is the default.

An `Auto` server on a machine whose GPU cannot be used falls back to its own CPU and says so in its log; Mnemarr sees `cpu` as the place and the server as in use.

## Security

- The key is a bearer token; keep it in a file with the right permissions and use HTTPS where the network is not yours. It is compared in constant time, and only its hash is kept in memory.
- The server never writes what it reads. A text sent to it is on the wire and in the memory of the server for the time of the request; the network between Mnemarr and the server is therefore inside the boundary of what you trust, like the database.
- It runs as a non-root user and needs no capability; the example drops them all.
