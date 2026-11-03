# 0011 — Default port 8383

- **Status**: accepted
- **Date**: 2026-11-01
- **Spec**: D-29

## Context

Mnemarr's audience runs the *arr suite, and many people will install it next to those applications. The first draft listened on 8686, which is Lidarr's default port. The ports in use by the family and its neighbours include Sonarr 8989, Radarr 7878, Lidarr 8686, Readarr 8787, Prowlarr 9696, Whisparr 6969, Bazarr 6767 and Portarr 8484.

## Decision

Mnemarr listens on **8383** by default. It is set in one place, the `Urls` setting of `appsettings.json` (overridable with `MNEMARR__URLS`); the Dockerfile, the deployment examples, the end-to-end stack and the development proxy follow it.

## Consequences

- No collision with another *arr application on a default install.
- The value appears in a few files; a change is a search and replace, and the checks (end-to-end tests, deployment examples) fail if one is missed.
