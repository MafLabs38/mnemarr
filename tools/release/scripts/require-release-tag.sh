#!/usr/bin/env bash
# Without a tag to count from, the first release would be 1.0.0 and could not be undone (docs/releasing.md, "The seed tag").
set -euo pipefail

if [ -z "$(git tag --list 'v*')" ]; then
  echo "::error title=No release tag::No v* tag found: create v0.0.0 on the initial commit of main first (docs/releasing.md)."
  exit 1
fi
