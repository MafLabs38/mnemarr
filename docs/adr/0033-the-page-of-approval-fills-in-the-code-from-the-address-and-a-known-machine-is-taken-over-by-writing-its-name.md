# 0033 — The page of approval fills in the code from the address, and a known machine is taken over by writing its name

- **Status**: accepted
- **Date**: 2026-11-07
- **Spec**: D-53; refines D-50 ([0030](0030-a-collector-sends-to-several-mnemarr-and-links-to-one-by-asking-and-being-approved.md)) and D-51 ([0031](0031-a-machine-is-a-computer-not-a-name-it-is-deleted-in-two-ways-and-installed-once-for-everyone.md) §3)

## Context

A computer that asks to be linked shows a short code and opens, in the browser, the page of Mnemarr where a person approves it. ADRs 0030 and 0031 made the
person **type** that code from the screen of the computer, as the proof that the request in front of them is the one they started. In use, the address the
computer opens already carries the code, and typing it again is a copy and paste.

## Decision

The maintainer asked, on 2026-11-07, that the page **fills in the code from the address**. From the list of requests there is no address, so the person still types
the code the computer shows.

What is lost is the proof that the person looked at the computer: **an address sent by a stranger shows a request that the person did not make**, with its code
filled in, and one click approves it; the stranger then holds a key that writes conversations. What remains is the name the computer gives itself, which the person
reads, and the expiry of a request after ten minutes. One check is added, **in the API and not only in the page**: a computer that says it is a machine that
already joined (`knownAs`) takes that machine over (its key stops, the new one takes its conversations) only if the approval carries `takeOver`, the name of that
machine, which an address cannot supply. Without it the approval is refused (400).

## Consequences

- The ease the maintainer asked for, at the cost of a weaker proof. A stricter option stays open (Q-41): a second code that only the computer shows.
- Every client of the API, not only the page, meets the takeover guard.
