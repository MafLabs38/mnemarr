#!/usr/bin/env bash
# The version an image is built with: X.Y.Z.<run> for a release (the application shows it), 0.0.0-<ref>.<run> for any other build.
# RELEASE is the version semantic-release chose ('' when nothing deserves a release), REF the branch, RUN the number of the CI run.
set -euo pipefail

: "${RUN:?the number of the CI run is required}"

if [ -n "${RELEASE:-}" ]; then
  echo "${RELEASE}.${RUN}"
else
  : "${REF:?the branch is required}"
  ref="${REF//[^A-Za-z0-9]/-}"
  # An identifier of digits only, with a leading zero, is not valid in a SemVer pre-release: give it a letter.
  case "$ref" in *[!0-9]*) ;; *) ref="r$ref" ;; esac
  echo "0.0.0-${ref}.${RUN}"
fi
