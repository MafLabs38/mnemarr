---
name: develop-vertical
description: Develop a vertical with the fast protocol (slice, build big locally, self-check and run every gate locally, demo, push once, one batched review round). Use when starting or continuing a vertical, before pushing, or when the CI/review loop is dragging.
---

# Develop a vertical (docs/DEVELOPMENT-PROTOCOL.md)

Read [docs/DEVELOPMENT-PROTOCOL.md](../../../docs/DEVELOPMENT-PROTOCOL.md) if it is not in context. The short form:

1. **Slice**: one demo sentence; write the *in* and *not now* lists. Nothing the demo does not need.
2. **Build big, locally**: whole layers at a time, local commits as checkpoints, **no push**. Run the app, use real data, look at it in a browser, find the bugs yourself (failing test first, then fix the class of bug).
3. **Before the first push**: read your whole diff once; run every CI gate locally (format, lint, types, analyzers, full unit and integration suites, e2e in each browser). No push while a gate fails.
4. **Demo** to the maintainer and iterate live. Open the PR only after acceptance.
5. **Push once. Review once**: the pipeline's agents (`ai-review.yml`, comments on the PR) on the head commit. **Read every one of their reports, in full** (your own local agents do not replace them), triage every finding in the code and answer each in the PR's *Review* section: fixed, declined with the reason, or **if there are too many or a fix is a big, token-costly change, stop and discuss it with the maintainer**: never ignore a report, never decide silently. fix **all** of them in **one** batch, run the local gates, push once. A second round only if there was a blocker, critical or high finding. **Two rounds at most**; then present the PR with what is still open.
6. **Triage by threat model**: fix real defects and high-severity items; decline or defer hardening beyond the product's threat model, with the reason written; "also do X" goes to *not now*.

Budget: 3 pushes or fewer, 2 review rounds at most. If it is heading past that, stop and cut scope.
