#!/usr/bin/env bash
# The version an image is built with: X.Y.Z.<run> for a release (the application shows it), 0.0.0-<ref>.<run> for any other build.
# RELEASE is the version semantic-release chose ('' when nothing deserves a release), REF the branch, RUN the number of the CI run.
set -euo pipefail

: "${RUN:?the number of the CI run is required}"

if [ -n "${RELEASE:-}" ]; then
  echo "${RELEASE}.${RUN}"
else
  : "${REF:?the branch is required}"
  # (A ref made of digits only, such as 007, would give an invalid pre-release identifier: only main and develop publish.)
  echo "0.0.0-${REF//[^A-Za-z0-9]/-}.${RUN}"
fi
