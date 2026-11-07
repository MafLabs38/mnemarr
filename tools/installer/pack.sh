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
vpk_version="1.3.39-g8d2844f"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/pack" "$output"
# Two programs, the same one: `mnemarr-collector.exe` talks to a terminal (the commands a person types), `mnemarr-collector-app.exe` is the application the
# installer starts, with the session or after an update: its subsystem is "windows", so that Windows opens no console for it (a console that a person closes
# would close the collector with it). The subsystem is one field of the header of the file (2 = windows, 3 = console).
cp "$program" "$work/pack/mnemarr-collector.exe"
python3 -I - "$program" "$work/pack/mnemarr-collector-app.exe" <<'PY'
import struct, sys
data = bytearray(open(sys.argv[1], "rb").read())
if data[:2] != b"MZ":
    sys.exit("not a Windows program")
pe = struct.unpack_from("<I", data, 0x3C)[0]
if data[pe:pe + 4] != b"PE\0\0":
    sys.exit("not a Windows program")
at = pe + 24 + 68  # the subsystem is at the same place in the 32-bit and the 64-bit header
if struct.unpack_from("<H", data, at)[0] != 3:
    sys.exit("the program is not a console program: nothing to change")
struct.pack_into("<H", data, at, 2)
open(sys.argv[2], "wb").write(data)
PY

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
  --mainExe mnemarr-collector-app.exe \
  --packTitle "Mnemarr collector" \
  --packAuthors "Mnemarr" \
  --runtime win-x64 \
  --shortcuts None \
  --noPortable \
  --outputDir "$output"

ls -l "$output"
