# Releasing

Mnemarr's versions are decided by [semantic-release](https://semantic-release.org) (its configuration and tooling are in [`tools/release`](../tools/release)) from the Conventional Commits of the history ([ADR 0013](adr/0013-releases-with-semantic-release-consumed-by-pulling.md), D-33). Nobody types a version.

## How a version is decided

On every push to `main`, after the whole pipeline is green, the `release` job reads the commits since the last `v*` tag:

| Commit | Version |
|---|---|
| `feat(…): …` | minor: `0.1.0` → `0.2.0` |
| `fix(…)`, `perf(…)`, `revert(…)` | patch: `0.1.0` → `0.1.1` |
| a breaking change (`!` after the type, or a `BREAKING CHANGE:` footer), **of any type** | minor while below 1.0, major from 1.0: a breaking `refactor!:` or `docs!:` must not ride in a patch |
| `chore(deps)` (a non-development dependency, as Dependabot titles it) | patch: a fixed vulnerability must reach a version. A NuGet bump counts even when only the tests use the package (Dependabot cannot tell); the collector's crates (`build(deps)`) do not |
| `chore`, `docs`, `test`, `refactor`, `style`, `build`, `ci`, `chore(deps-dev)` | no release (unless marked breaking, above) |

Two consequences of this to know. The title is what counts: git's default `Revert "feat: x"` is not a Conventional Commit (the title check fails it), retitle the pull request `revert: …`. And a bump of a build tool such as `vite` or `typescript` is a development dependency (`chore(deps-dev)`) and releases nothing, although it changes the bundle in the image; a grouped Dependabot pull request that contains a production dependency is titled `chore(deps)` and releases a patch even when most of the group is test-only.

The rules are in [`tools/release/.releaserc.json`](../tools/release/.releaserc.json) (the rules are applied in order: the highest release type among the matching ones wins, but a rule with `release: false` clears what matched before it. So the specific `chore(deps)` rule and the breaking rule come after the general `chore` one: moving them up silently stops those commits from releasing) and are tested by `release-tooling` in the CI.

It then creates the tag `vX.Y.Z` and a GitHub release whose notes are the changelog. Because the title of a squash-merged pull request becomes the commit on `develop`, **the title of the pull request decides the version**: write `fix:` for a fix and `feat:` for something new. The `PR title` check fails a title that is not a Conventional Commit, so a fix cannot silently drop out of a release.

## The build number

The image is built with the version `X.Y.Z.<n>`, where `<n>` is the number of the CI run (`0.1.0.123`). That is what the application shows in **System > Status**. It answers "which build is this"; the tags `X.Y.Z` and `latest` are what a deployment follows.

## Cutting a release

1. `develop` is green and holds what should ship.
2. Open a pull request **`develop` → `main`**. Its title does not matter for the version (the commits do).
3. A maintainer reviews it and merges it with a **merge commit**. `main` accepts nothing else: a squash would hide every `feat` and `fix` from the analyzer and the release would come out empty. This is a rule of the repository (the `main` ruleset and the merge settings are described as code in the organization's `github-template`, `repos/mnemarr.yaml`), not a habit. Every other pull request, into `develop`, is squash-merged.
4. The pipeline runs on `main`: tests, scans, end-to-end smoke test, then `release` (tag and notes) and `image` (the image under the new version). A release needs everything to have run **and passed**, Sonar included: a skipped Sonar analysis (server not configured) does not release. If `release` succeeded and `image` failed, use **Re-run failed jobs**, not "Re-run all jobs" (a full re-run would find nothing new to release and publish no version tag).
5. A deployment that follows the version tags picks the new version up on its own (below).

A run on `main` that finds nothing worth releasing (only `chore`, `docs`…) publishes no version: the `main` and commit images are built, and nothing is deployed.

What the jobs do, by the result of the gates (`release` and `image` are skipped on a pull request):

| Push to | Gates | `release` | `image` |
|---|---|---|---|
| `develop` | all passed (Sonar passed or not configured) | skipped | `develop` and `sha-` tags |
| `develop` | one failed or was cancelled | skipped | skipped |
| `main` | all passed, Sonar included | runs, tags if a commit deserves it | `main` and `sha-` tags, plus `X.Y.Z` and `latest` after a release |
| `main` | one failed or was cancelled | skipped | skipped |
| `main` | Sonar not configured | skipped | skipped: **`main` publishes nothing without Sonar** |

CodeQL runs in its own workflow and gates the pull request (once it is a required check), not the release.

## What is published

`ghcr.io/<owner>/mnemarr`, for `linux/amd64` and `linux/arm64`. Tags: `X.Y.Z`, `latest`, the branch (`develop`, `main`) and `sha-<commit>`. Pull requests publish nothing, and neither does a push whose tests, scans or Sonar analysis failed. The base images of the Dockerfile are pinned by digest, and a release is built without the layer cache.

The end-to-end test runs the amd64 image (a precondition for the vector search vertical is tracked in issue #24). The arm64 image is built (without emulation) and **not started in CI**: that is acceptable while the application is portable, framework-dependent code, and it must change with the first native dependency per architecture (ADR 0013).

## Following the releases

Mnemarr never deploys itself, and the CI has no access to your hosts: a version is consumed by pulling it.

**Docker Compose.** Pin the version in your Compose file and bump it when you want to update (see [deploy/docker-compose.example.yml](../deploy/docker-compose.example.yml)):

```yaml
image: ghcr.io/<owner>/mnemarr:0.1.0
```

**A tool that follows the registry.** For example [Flux](https://fluxcd.io) image automation, which records the newest version in your own infrastructure repository (a deployment is then a commit there, and a rollback a `git revert`). Below 1.0 a breaking change is a minor, and Mnemarr keeps a database and vectors: the example stays on one minor, and moving to the next one is a deliberate step until 1.0.

```yaml
apiVersion: image.toolkit.fluxcd.io/v1
kind: ImagePolicy
metadata:
  name: mnemarr
spec:
  imageRepositoryRef:
    name: mnemarr
  policy:
    semver:
      range: ">=0.1.0 <0.2.0" # develop, latest and sha-… are not versions: they are ignored
```

Images are not signed yet (Q-39): following the tags automatically trusts everyone who can push to the repository (D-32 states that boundary), so pin by digest if you do not. With a marker on the `image:` line of the Deployment (`# {"$imagepolicy": "<namespace>:mnemarr"}`). See [deploy/](../deploy/README.md) for the manifests to start from.

## The seed tag

semantic-release counts from the last tag. The repository starts from a tag `v0.0.0` on the initial commit of `main`, so that the first release is `0.1.0` and not `1.0.0`. Do not delete it.

## Going to 1.0

The `breaking → minor` rule of `tools/release/.releaserc.json` keeps the project below 1.0, so no commit can produce 1.0.0 by itself. 1.0 is a decision of the maintainer: a pull request that removes that rule and carries a breaking commit.

## A fix in a hurry

There is no separate hotfix path. A fix goes through `develop` like any other change, and the release pull request `develop` → `main` is cheap. (A branch cut from `main` would need `main` merged back into `develop`, or the next release pull request conflicts.)

## Before the first release

- The tag `v0.0.0` exists on the initial commit of `main` (the `release` job refuses to run without any `v*` tag).
- The `main` ruleset allows merge commits only (check **Settings > Rules**), and the squash commit of the repository is the pull request title (**Settings > General > Pull Requests**): both are set by `repos/mnemarr.yaml` of the organization's `github-template`.
- Sonar is configured (`SONAR_HOST_URL` and `SONAR_TOKEN`): without it a merge into `main` releases and publishes nothing, and the run says so with a warning.
- The `v*` tags are protected against deletion and moves (a ruleset of the organization's `github-template`): deleting `v0.0.0` would make the next release `1.0.0`.
- A Dependabot pull request that touches `tools/release/yarn.lock` is reviewed as privileged code: that package runs in the one job that can write to the repository. Npm bumps wait seven days (`cooldown`).
- Decide whether `conventional-title` (and the other checks) are **required** checks of the repository. The `PR title` workflow reports a failure, but only a required check blocks the merge.
- After the first image is published, set the package visibility to public if the registry does not inherit it from the repository, so that a deployment can pull it without a credential.

## Checking a release without publishing

The tests of the release tooling (`yarn test` in `tools/release`: the rules, the version of an image, the guard on the seed tag) use `bash` and POSIX paths: Linux, macOS or WSL.

The release tooling (hundreds of packages) runs with that token in its environment: never use your everyday `gh` credential for it.

```bash
cd tools/release
yarn install --frozen-lockfile --ignore-scripts
export GITHUB_TOKEN=<a fine-grained token of this repository only, Contents: read-only (a dry run publishes nothing)>
yarn semantic-release --dry-run --no-ci --branches develop
```

prints the version it would choose and the notes, and publishes nothing.

semantic-release only counts the tags that are reachable from the branch it runs on. Release tags sit on the merge commits of `main`, which are never merged back into `develop`: once a first release exists, a dry run on `develop` counts from `v0.0.0` again and over-reports. To preview the next release, run it on a throwaway branch cut from `origin/main` with `origin/develop` merged in.
