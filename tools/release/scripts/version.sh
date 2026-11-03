#!/usr/bin/env bash
# The version an image is built with: X.Y.Z.<run> for a release (the application shows it), 0.0.0-<ref>.<run> for any other build.
# RELEASE is the version semantic-release chose ('' when nothing deserves a release), REF the branch, RUN the number of the CI run.
set -euo pipefail

if [ -n "${RELEASE:-}" ]; then
  echo "${RELEASE}.${RUN}"
else
  echo "0.0.0-${REF//[^A-Za-z0-9]/-}.${RUN}"
fi
