#!/usr/bin/env bash
# Build the lab payload from this checkout into $RA2_LAB_HOME/payload and
# rewrite its SHA256SUMS (lab-run.sh pushes exactly what the sums list).
#
#   build-payload.sh [--fork-dir <dir>]
#
# --fork-dir: a ra2yrcpp fork build's bin/ directory (libra2yrcpp.dll and
# zlib1.dll) built with upstream's docker MinGW toolchain; see README.md.
# spawnmap-brutal.ini is game content derived from the appliance: it is kept
# in the payload directory and summed, never built here.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
PAYLOAD="${RA2_LAB_HOME:?set RA2_LAB_HOME to the lab state directory}/payload"
fork=""
while [ $# -gt 0 ]; do
  case "$1" in
    --fork-dir) fork=$2; shift ;;
    *) echo "usage: $0 [--fork-dir <dir>]" >&2; exit 2 ;;
  esac; shift
done
mkdir -p "$PAYLOAD/fork"
out=$(mktemp -d); trap 'rm -rf "$out"' EXIT
for t in LaunchAgent LiveAcceptance Channel; do
  nix shell nixpkgs#dotnet-sdk_8 -c dotnet publish "$REPO/tools/Bindery.Ra2.Adapter.$t/Bindery.Ra2.Adapter.$t.csproj" \
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$out/$t" -v q
  cp "$out/$t/Bindery.Ra2.Adapter.$t.exe" "$PAYLOAD/"
done
if [ -n "$fork" ]; then
  for f in libra2yrcpp.dll zlib1.dll; do cp "$fork/$f" "$PAYLOAD/fork/"; done
fi
rm -f "$PAYLOAD/fork/libmcfgthread-2.dll"
cd "$PAYLOAD"
[ -f SHA256SUMS ] && cp SHA256SUMS SHA256SUMS.prev
files=(Bindery.Ra2.Adapter.Channel.exe Bindery.Ra2.Adapter.LaunchAgent.exe Bindery.Ra2.Adapter.LiveAcceptance.exe)
[ -f spawnmap-brutal.ini ] && files+=(spawnmap-brutal.ini) || echo "warning: $PAYLOAD/spawnmap-brutal.ini missing" >&2
for f in fork/libra2yrcpp.dll fork/zlib1.dll; do [ -f "$f" ] && files+=("$f"); done
sha256sum "${files[@]}" > SHA256SUMS
echo "payload at $(git -C "$REPO" rev-parse --short HEAD):"; sed 's/^/  /' SHA256SUMS
