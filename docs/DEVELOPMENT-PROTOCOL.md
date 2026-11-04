# Development protocol: build big, check it yourself, push once

> How a vertical is developed here. It changes the **cadence**, not the **gates**: everything the quality gates of `CLAUDE.md` demand before a merge still holds. What changes is where the feedback comes from.

## The principle

Feedback comes from **running the thing**, not from the CI and not from the review agents. A person who writes code by hand writes a lot, runs it, looks at what happens, fixes it, and pushes when they believe it is good. Only then do they wait for others. The CI and the review agents are the **last** check, run **once or twice**, never the inner loop.

The failure this protocol exists to prevent: push a small change, wait for CI and the agents, fix one finding, push again, wait again. Each cycle is slow and expensive, and every fix adds code that the next round criticises. A vertical then takes ten or more rounds and the result is not better.

## The phases

### 1. Slice (before any code)

- Write the vertical as **one demo sentence**: what the maintainer will be able to do or see. If it needs "and" three times, it is two verticals.
- List what is **in** and what is **not now**. Anything the demo does not need goes to the *not now* list (it becomes a follow-up, not scope). Adding a feature in response to a review comment is scope creep: it is a new item, not a fix.
- Prefer a **narrow** vertical (read-only before read-write, one provider before several, one screen before the family).

### 2. Build in large steps, locally

- Write whole layers or whole features at once (domain, API, UI and their tests), not one micro-change per cycle. Large coherent changes are cheaper to get right than many small ones that each need a check.
- Commit **locally** as often as is useful (checkpoints are free and safe). **Do not push** while building.
- The inner loop is fast and local: build and run the tests of the projects you touch (use filters), start the app in the sandbox, use it with real data, and look at it in a real browser. Find the bugs yourself.
- A bug found by looking gets a failing test first, then the fix. When you fix a bug, look for its **siblings** (the same mistake elsewhere) and fix the class, not the instance.
- Never edit many files with a script and commit without reading `git diff --stat` and the diff: a script that fails halfway must not be followed by a commit.

### 3. Self-review, then the local gates (once, before the first push)

1. Read your **own whole diff** once, as a reviewer would: layering, names, dead code, hard-coded values that should be settings, secrets, missing tests, documentation and ADR for each decision.
2. Run every gate the CI runs, **locally**, once: format, lint, type check, analyzers (the Sonar rules are in the build), the full unit and integration suites, the end-to-end journeys in each browser. For a surface that takes untrusted input, add the hostile inputs now, in tests.
3. **No push while a local gate fails.** A push is a claim that the work is finished.

### 4. Demo, and iterate live

- Show the maintainer the running result (sandbox address, what to try, test accounts). Iterate **here**: changes in a live demo cost minutes, not CI cycles.
- Open the pull request only once the vertical is accepted (rule 6 of `CLAUDE.md`).

### 5. Push once, review once

- Push, open the PR. The CI runs. Do not push "to see what the CI says": that is what step 3 is for.
- **Round 1**: run the review agents once on the head commit. Triage **every finding in the code** before accepting or declining it.
- **Batch the corrections.** Collect *all* the findings, decide each one, fix them **together** in one change, run the local gates, push **once**. One finding per push is forbidden.
- **Round 2 (only if round 1 had a blocker, critical or high finding)**: run the agents to confirm the fixes. Otherwise skip it.
- **At most two rounds.** After that the PR goes to the maintainer with what is still open written in its *Review* section. A finding that appears in round 2 and is not a regression of the fix is a follow-up, not a blocker.

### 6. Merge

Per the rules of the repository (`CLAUDE.md`, the ADR on who merges what). Then record the follow-ups as issues.

## How to triage a finding

Calibrate to the **threat model and the audience**, not to the reviewer's tone. A self-hosted tool whose notes are written by its operator is not exposed to the same risks as a public service; a finding that needs an attacker the product does not have is *declined with the reason written*.

| Finding | Action |
|---|---|
| Blocker, critical, high; a real bug; a data-loss or secret-leak path | Fix, with a failing test first. Always. |
| Medium that is a confirmed defect and cheap to fix | Fix in the batch. |
| Medium that is a hardening beyond the threat model, a refactor, or a preference | Decline with the reason, or record as a follow-up. |
| Minor, nitpick | Fix only if it is free in the batch; otherwise ignore. |
| "Add feature X", "also handle Y" | Not a finding: it goes to the *not now* list. |
| Architecture verdict "no" | Blocks. Resolve it, or decide and write why. |

The CI gates that **block** (tests, the SonarQube quality gate, CodeQL, dependency and secret scans) are not negotiable and are met in step 3 before the first push. The AI review is advisory.

## Budget

| Per vertical | Target |
|---|---|
| Pushes to the branch | 3 or fewer (open the PR, one batch of corrections, at most one more) |
| Review rounds | 2 at most |
| Local commits | as many as useful |
| CI waits | one per push, and only after the local gates were green |

If a vertical is heading past this budget, **stop and cut scope** (move the remainder to *not now*) instead of continuing to iterate.

## Anti-patterns

- Pushing to find out whether the CI is green.
- One commit and one push per finding; labelling for a new review at every push.
- Waiting on the CI for feedback on code that was never run.
- Growing the vertical while fixing it.
- Hardening against attackers the product does not have, at the price of code the next round will criticise.
- Re-running the whole suite after every small fix instead of the targeted tests, then the full suite once before pushing.
