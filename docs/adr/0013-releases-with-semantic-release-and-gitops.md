# 0013 — Releases are decided by semantic-release from main, and deployed by following the registry

- **Status**: accepted
- **Date**: 2026-11-03
- **Spec**: D-33 (applies the release line of ADR 0008, same design as Portarr's ADR 0014)

## Context

The pipeline builds and tests everything, but nothing is versioned or published: there is no tag, no release notes and no image to deploy. The maintainer wants every release to be versioned by the pipeline, not by hand, and a new version to reach the operator's cluster without anyone typing a version or giving the CI a way into that cluster.

Constraints:

- `main` and `develop` are protected: no direct push, not even for the pipeline. Whatever releases cannot commit a changelog or a version file back.
- Feature branches are **squash-merged into `develop`**, so each commit on `develop` carries one Conventional Commit message (`feat(…)`, `fix(…)`, `chore(…)`).
- The project is below 1.0: the first release is **0.1.0**, and an incompatible change must not jump to 1.0.0 by accident.
- The cluster that runs a deployment is usually behind a home router. A CI that pushes into it would need an inbound path that should not exist.

## Decision

1. **semantic-release runs on `main` only**, as a job of the CI workflow, after every test, scan and the end-to-end smoke test have passed. It reads the Conventional Commits since the last `v*` tag, picks the next version, and creates the tag and the **GitHub release**, whose notes are the changelog. Nothing is committed back: the version lives in the tag.
2. **Release rules** (`.releaserc.json`): `feat` is a minor; `fix`, `perf` and `revert` a patch; a breaking change is a **minor** while the project is below 1.0 (the rule goes away at 1.0). `chore`, `docs`, `test`, `refactor`, `style` and `ci` do not release.
3. **The first release is 0.1.0.** A seed tag `v0.0.0` sits on the initial commit of `main`; semantic-release counts from it. Without it the first release would be 1.0.0.
4. **`main` takes merge commits only, never a squash.** A release pull request `develop → main` is merged with a merge commit, so that `main` contains the commits themselves and the analyzer sees each `feat` and `fix`. A squash would turn the whole history into one message and release nothing. Every other pull request, into `develop`, stays squash-merged. This is enforced in the organization's infrastructure as code (`allowed_merge_methods` of the `main` ruleset, `allow_merge_commit` of the repository), not by a convention.
5. **The version is `X.Y.Z` plus a build number.** The image is built with `VERSION=X.Y.Z.<run number>` (the application shows `0.1.0.123`) and tagged `X.Y.Z` and `latest`. semantic-release only knows three numbers; the fourth is the CI run that built the image.
6. **One image, for `linux/amd64` and `linux/arm64`.** The build stages of the Dockerfile run on the builder's architecture and the final stage only copies files, so the arm64 image is built without emulation. Every push to `main` or `develop` publishes the branch and commit tags; only a release adds the version and `latest`. Pull requests never publish.
7. **A run on `main` is never cancelled** by the next push (the other branches still are): a tag created without its image is the one inconsistent state to avoid.
8. **Deployment follows the registry, it is never pushed.** The operator's cluster runs a GitOps tool that watches the image repository, picks the newest version `>= 0.1.0` (tags such as `develop`, `latest` and `sha-…` are not versions and are ignored), and records it in the operator's own infrastructure repository. A deployment is a commit there, a rollback is a revert. The CI has no access to the cluster. That repository and its manifests are the operator's, they are not part of this one (rule 2 of `CLAUDE.md`); `deploy/` holds generic examples.

## Consequences

- A release is a pull request `develop → main` that a person reviews and merges; everything after the merge is automatic.
- The Conventional Commit **title of every pull request** decides the version, because the title becomes the squash commit: a `feat` that is really a `fix` over-bumps. The pull request template says so.
- `main` is a little noisier than a squash would leave it. It is the price of letting the analyzer see each change.
- If the release succeeds and the image build fails, the tag exists without an image. Re-running the failed `image` job in the same run fixes it (the version output is kept).
- The collector (ADR 0005) is not part of this release yet: it is released as binaries for Windows and Linux in its own vertical.

## Alternatives considered

- **Pre-releases `-beta.N` on `develop`.** They need `main` merged back into `develop` after every release, for tags nobody deploys.
- **A `CHANGELOG.md` and a version bump committed by the pipeline** (`@semantic-release/git`). Impossible with the no-direct-push rule, and a bypass for a bot is a hole worth not digging.
- **Squash merge of the release pull request.** Hides the commits from the analyzer.
- **Pushing the new version into the cluster from the CI** (`kubectl`, SSH). Needs an inbound path to a cluster that has none, and a credential in the CI that can change production.
- **Release-please** (a release pull request kept by a bot). A fine tool, but a second pull request flow on top of ours.
