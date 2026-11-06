---
name: review-vertical
description: Run the four review agents (tests, quality, OWASP security, architecture) in parallel on the current branch before opening or updating a vertical PR, then triage and apply their findings.
---

# Review a vertical before its PR

1. Base: `develop` (or the given base). Make sure the branch is committed and the full test suite is green.
2. Launch **in parallel**, each with the base and the branch name, asking for its Markdown report:
   - `test-reviewer`: relevance, redundancy, missing cases, speed;
   - `quality-reviewer`: spec compliance, design, smells, frontend and Rust conventions;
   - `security-reviewer`: OWASP and privacy review;
   - `architecture-reviewer`: technical lead, *arr patterns, layers, hard-coded vs configurable, decisions.
   If the agents are not registered in the session, spawn general-purpose agents with the content of `.claude/agents/<name>.md` as instructions.
3. **Triage** the four reports: fix every blocker, major, critical and high finding (the scale of each reviewer) and every "remove" or "fix" test finding that is right; for the rest, decide and say why. Do not accept a finding without checking it in the code. An **architecture verdict of "no" blocks the PR** until it is addressed or decided (written in the PR's Review section).
4. Re-run the tests, note the durations before and after.
5. Add a **Review** section to the PR description: per agent, what was fixed, what was declined and why, test count and duration before and after.
