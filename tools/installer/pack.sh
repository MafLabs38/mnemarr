#!/usr/bin/env bash
# Packages the Windows program of the collector with Velopack (ADR 0032): the installer (Setup.exe, no secret in it), the packages that a collector
# downloads to update itself, and the feed (releases.win.json). Works on Linux (it packages for Windows from there) and on Windows.
#
#   pack.sh <program.exe> <version> <output folder>
#
# The version is a semantic version (1.2.3, or 0.0.1-develop.42 for a build that is not a release; Velopack refuses anything below 0.0.1): the collector
# compares them to know what is newer.
# vpk is pinned: it is a tool of the build, not something a machine runs.
set -euo pipefail

program="${1:?the Windows program of the collector is required}"
version="${2:?the version is required}"
output="${3:?the output folder is required}"
vpk_version="1.2.161"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/pack" "$output"
cp "$program" "$work/pack/mnemarr-collector.exe"

# The tool is installed beside the work, so that a run leaves nothing behind.
dotnet tool install --tool-path "$work/vpk" vpk --version "$vpk_version" >/dev/null
vpk="$work/vpk/vpk"
[ -x "$vpk" ] || vpk="$work/vpk/vpk.exe"

# On Linux and macOS the target system is a directive of the command; on Windows it is the system itself.
case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*) directive=() ;;
  *) directive=('[win]') ;;
esac

"$vpk" ${directive[@]+"${directive[@]}"} pack \
  --packId MnemarrCollector \
  --packVersion "$version" \
  --packDir "$work/pack" \
  --mainExe mnemarr-collector.exe \
  --packTitle "Mnemarr collector" \
  --packAuthors "Mnemarr" \
  --runtime win-x64 \
  --shortcuts None \
  --noPortable \
  --outputDir "$output"

ls -l "$output"
