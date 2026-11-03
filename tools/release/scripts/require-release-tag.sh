#!/usr/bin/env bash
# Without a tag to count from, the first release would be 1.0.0 and could not be undone (docs/releasing.md, "The seed tag").
set -euo pipefail

# Its own statement: a failure of git itself (not a repository, a safe-directory error) stops the script with its own message.
tags=$(git tag --list 'v*')
if [ -z "$tags" ]; then
  echo "::error title=No release tag::No v* tag found: create v0.0.0 on the initial commit of main first (docs/releasing.md)."
  exit 1
fi
