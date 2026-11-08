#!/usr/bin/env bash
# Fetches the files of the embeddings model, at the pinned upstream revision, and verifies the SHA-256 of each before it is kept (ADR 0012, ADR 0034).
#
#   fetch-model.sh <output folder>
#
# The same revision and the same checksums are in src/Mnemarr.Core/Embeddings/KnownModels.cs, which verifies them again before the model is loaded: a file that
# differs is not loaded, wherever it came from.
set -euo pipefail

output="${1:?the output folder is required}"
model="ibm-granite/granite-embedding-97m-multilingual-r2"
revision="835ad14087e140460703cf0fae09f97d469d65c2"
base="https://huggingface.co/${model}/resolve/${revision}"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$output"

fetch() { # <path upstream> <name kept> <sha256>
  curl --fail --silent --show-error --location --retry 6 --retry-delay 10 --retry-all-errors "$base/$1" --output "$work/$2"
  echo "$3  $work/$2" | sha256sum -c - >/dev/null || { echo "the checksum of $2 is not the pinned one" >&2; exit 1; }
  mv "$work/$2" "$output/$2"
}

fetch onnx/model.onnx model.onnx 68e592b160673d30250824c1116bc6ab33f70efb22b97c9e1d7ce1e69c1c9d70
fetch tokenizer.json tokenizer.json 4f2842d568e2724370aec203652a42ac783c7937f8347a1a2cc7506d71f1582f
ls -l "$output"
