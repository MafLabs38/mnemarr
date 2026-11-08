#!/usr/bin/env bash
# Fetches the loadable sqlite-vec library for one system, at the pinned version, and verifies its SHA-256 before it is used (ADR 0012, ADR 0034).
#
#   fetch-vector-library.sh <linux-x86_64|linux-aarch64|windows-x86_64|macos-aarch64> <output folder>
#
# The file written is <output folder>/vec0.<so|dll|dylib>. A checksum that differs is an error and nothing is left behind.
set -euo pipefail

version="0.1.9"
system="${1:?the system is required (linux-x86_64, linux-aarch64, windows-x86_64 or macos-aarch64)}"
output="${2:?the output folder is required}"

case "$system" in
  linux-x86_64) sha256="b959baa1d8dc88861b1edb337b8587178cdcb12d60b4998f9d10b6a82052d5d7"; library="vec0.so" ;;
  linux-aarch64) sha256="ea03d39541e478fab5974253c461e1cb5d77742f69e40cf96e3fad5bc309a37c"; library="vec0.so" ;;
  windows-x86_64) sha256="51581189d52066b4dfc6631f6d7a3eab7dedc2260656ab09ca97ab3fb8165983"; library="vec0.dll" ;;
  macos-aarch64) sha256="8282126333399ddfe98bbbcc7a1936e7252625aac49df056a98be602e46bfd29"; library="vec0.dylib" ;;
  *) echo "unknown system: $system" >&2; exit 2 ;;
esac

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
archive="sqlite-vec-${version}-loadable-${system}.tar.gz"
curl --fail --silent --show-error --location --retry 6 --retry-delay 10 --retry-all-errors \
  "https://github.com/asg017/sqlite-vec/releases/download/v${version}/${archive}" --output "$work/$archive"
echo "${sha256}  $work/$archive" | sha256sum -c - >/dev/null || { echo "the checksum of $archive is not the pinned one" >&2; exit 1; }
tar -xzf "$work/$archive" -C "$work"
mkdir -p "$output"
cp "$work/$library" "$output/$library"
echo "$output/$library"
