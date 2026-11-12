#!/usr/bin/env bash
# Fetches the native libraries of ONNX Runtime that run on a CUDA GPU, at the pinned version, and verifies the SHA-256 of the package before anything is kept (ADR 0040).
#
#   fetch-onnx-gpu.sh <output folder>
#
# They replace the CPU-only library of the published embedding server: same version as the managed assembly of Directory.Packages.props, which must be moved with it.
# The package is for linux-x64 only. It needs the CUDA 13 runtime libraries (cublas, curand, cudart) next to it, which the image of the embedding server brings.
set -euo pipefail

output="${1:?the output folder is required}"
version="1.28.0"
sha256="6d82b065357558e8bea4f3d938ed95b9a2a0cb94c82749adf8e14401441c5346"
package="microsoft.ml.onnxruntime.gpu.linux"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
curl --fail --silent --show-error --location --retry 6 --retry-delay 10 --retry-all-errors \
  "https://api.nuget.org/v3-flatcontainer/${package}/${version}/${package}.${version}.nupkg" --output "$work/package.nupkg"
echo "${sha256}  $work/package.nupkg" | sha256sum -c - >/dev/null || { echo "the checksum of the ONNX Runtime GPU package is not the pinned one" >&2; exit 1; }
unzip -q "$work/package.nupkg" 'runtimes/linux-x64/native/*' -d "$work/files"
mkdir -p "$output"
cp "$work"/files/runtimes/linux-x64/native/*.so "$output/"
ls -l "$output"
