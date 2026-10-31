# Mnemarr

> Remember. A self-hosted long-term memory for people and AI assistants, in the spirit of the *arr suite.

**Status: inception.** No code yet; the need is being gathered. See [docs/VISION.md](docs/VISION.md).

Mnemarr stores notes, decisions, runbooks and session summaries, and finds them again by meaning (semantic search) and by text. It exposes a REST API and an MCP server so any assistant can read and write memories. It is a companion of [Portarr](https://github.com/MafLabs38/portarr) (see and describe your fleet) and Orchestratarr (act on it).

## Documentation

| Document | Content |
|---|---|
| [docs/VISION.md](docs/VISION.md) | Why, what it must do, how it plugs into Portarr and assistants, open questions |
| [CLAUDE.md](CLAUDE.md) | Rules for AI assistants working in this repository |

## Bootstrap

The GitHub repository is created by Terraform from `repos/mnemarr.yaml` in `MafLabs38/github-template` (pull request `repo/add-mnemarr-orchestratarr`). Once that pull request is merged and the apply has run, publish this local repository through a scaffolding pull request:

```bash
cd path/to/mnemarr
git remote add origin https://github.com/MafLabs38/mnemarr.git
env -u GH_TOKEN git fetch -q origin develop
git checkout -q -B chore/initial-scaffolding
git reset -q origin/develop            # keeps the local files, rebases on the remote history
git add -A && git commit -m "chore: initial project scaffolding"
env -u GH_TOKEN git push -u origin chore/initial-scaffolding
env -u GH_TOKEN gh pr create -R MafLabs38/mnemarr --base develop --title "chore: initial scaffolding" --body "Vision, project rules, common templates."
```

Before the first push, check that no real infrastructure value slipped in (`gitleaks dir .`). CI workflows (`ci.yml`, `codeql.yml`, `mutation.yml`) are added with the first vertical, when there is code for them to build; the options are declared in `repos/mnemarr.yaml` (`codeql` and `mutation` on, `sonarqube` and `ai_review` off until their tokens exist).

## License

GPL-3.0, like the *arr apps (to be confirmed in the spec).
