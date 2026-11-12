# Deployment examples

Generic examples only. A real deployment (host names, addresses, secrets, storage classes) belongs in your own private infrastructure repository, never in this one.

| File | For |
|---|---|
| [docker-compose.example.yml](docker-compose.example.yml) | A Docker host |
| [kubernetes/mnemarr.example.yaml](kubernetes/mnemarr.example.yaml) | A Kubernetes cluster |
| [docker-compose.embedder.example.yml](docker-compose.embedder.example.yml) | The embedding server, on a Docker host with an NVIDIA GPU ([guide](../docs/embedding-server.md)) |

Both run the image as a non-root user with a read-only root filesystem and no Linux capabilities, and keep the data folder on a **local** volume. Back up the data folder with your backup tool, from the snapshots Mnemarr produces (decision D-28 in [the spec](../docs/SPEC.md)).

When the ingest or MCP endpoint is published on its own host name (D-27), the reverse proxy in front of it should let through only the paths those endpoints need, not `/api/v1/auth/*` (the sign-in call): people sign in through the interface, behind the single sign-on.

Both are examples to adapt and were checked against the image built from this repository. Versions are published by the pipeline as `ghcr.io/<owner>/mnemarr:X.Y.Z` once the first release is cut; to follow them automatically, see [docs/releasing.md](../docs/releasing.md#following-the-releases).
