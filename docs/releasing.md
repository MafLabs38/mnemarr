# Releasing

Mnemarr's versions are decided by [semantic-release](https://semantic-release.org) from the Conventional Commits of the history ([ADR 0013](adr/0013-releases-with-semantic-release-and-gitops.md), D-33). Nobody types a version.

## How a version is decided

On every push to `main`, after the whole pipeline is green, the `release` job reads the commits since the last `v*` tag:

| Commit | Version |
|---|---|
| `feat(…): …` | minor: `0.1.0` → `0.2.0` |
| `fix(…)`, `perf(…)`, `revert(…)` | patch: `0.1.0` → `0.1.1` |
| a breaking change (`feat(…)!:` or a `BREAKING CHANGE:` footer) | minor while below 1.0, major from 1.0 |
| `chore`, `docs`, `test`, `refactor`, `style`, `ci` | no release |

It then creates the tag `vX.Y.Z` and a GitHub release whose notes are the changelog. Because the title of a squash-merged pull request becomes the commit on `develop`, **the title of the pull request decides the version**: write `fix:` for a fix and `feat:` for something new.

## The build number

The image is built with the version `X.Y.Z.<n>`, where `<n>` is the number of the CI run (`0.1.0.123`). That is what the application shows in **System > Status**. It answers "which build is this"; the tags `X.Y.Z` and `latest` are what a deployment follows.

## Cutting a release

1. `develop` is green and holds what should ship.
2. Open a pull request **`develop` → `main`**. Its title does not matter for the version (the commits do).
3. A maintainer reviews it and merges it with a **merge commit**. `main` accepts nothing else: a squash would hide every `feat` and `fix` from the analyzer and the release would come out empty. This is a rule of the repository (the `main` ruleset and the merge settings are described as code in the organization's `github-template`, `repos/mnemarr.yaml`), not a habit. Every other pull request, into `develop`, is squash-merged.
4. The pipeline runs on `main`: tests, scans, end-to-end smoke test, then `release` (tag and notes) and `image` (the image under the new version).
5. A deployment that follows the version tags picks the new version up on its own (below).

A run on `main` that finds nothing worth releasing (only `chore`, `docs`…) publishes no version: the `main` and commit images are built, and nothing is deployed.

## What is published

`ghcr.io/<owner>/mnemarr`, for `linux/amd64` and `linux/arm64`. Tags: `X.Y.Z`, `latest`, the branch (`develop`, `main`) and `sha-<commit>`. Pull requests publish nothing.

## Following the releases

Mnemarr does not deploy itself and the CI has no access to your hosts. Run a GitOps tool that watches the registry and records the newest version in your own infrastructure repository. With [Flux](https://fluxcd.io) image automation, for example:

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
      range: ">=0.1.0" # develop, latest and sha-… are not versions: they are ignored
```

and a marker on the `image:` line of the Deployment (`# {"$imagepolicy": "<namespace>:mnemarr"}`). A deployment is then a commit in your repository and a rollback is a `git revert`. See [deploy/](../deploy/README.md) for the manifests to start from.

## The seed tag

semantic-release counts from the last tag. The repository starts from a tag `v0.0.0` on the initial commit of `main`, so that the first release is `0.1.0` and not `1.0.0`. Do not delete it.

## Before the first release

- The tag `v0.0.0` exists on the initial commit of `main`.
- The `main` ruleset allows merge commits only (check **Settings > Rules**).
- After the first image is published, set the package visibility to public if the registry does not inherit it from the repository, so that a deployment can pull it without a credential.

## Checking a release without publishing

```bash
GITHUB_TOKEN=<a token> npx semantic-release --dry-run --no-ci --branches develop
```

prints the version it would choose and the notes, and publishes nothing.
