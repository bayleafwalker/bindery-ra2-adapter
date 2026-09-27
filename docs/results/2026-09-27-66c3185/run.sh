#!/usr/bin/env bash
# Documented tournament, rerun at feat/strategic-bot HEAD with 8 seeds (side x faction balance).
set -euo pipefail
cd /projects/dev/_wt/bindery-ra2-bot
OUT=$(dirname "$0")
AI=""; for s in ai-rush ai-balanced ai-turtle ai-air ai-horde ai-armor; do for d in easy medium hard; do AI="$AI,$s:$d"; done; done
BASE="${AI#,},rush,turtle,tech,harass,balanced"
LIVE="live-balanced,live-rush,live-tech,live-turtle,live-harass"
ARMS=selector,bandit,llm-shadow,llm,llm+fast,distilled,selector-oracle,llm-oracle
run() { nix shell nixpkgs#dotnet-sdk_8 -c dotnet run --project tools/Bindery.Ra2.Bot.Arena -c Release --no-build -- run \
  --arms $ARMS --maps all --opponents "$2" --seeds 8 --llm-fake --benchmark $1 --no-decisions --out "$OUT/$1" > "$OUT/$1.log" 2>&1; }
git rev-parse --short HEAD > "$OUT/commit"
nix shell nixpkgs#dotnet-sdk_8 -c dotnet build tools/Bindery.Ra2.Bot.Arena -c Release -v q > "$OUT/build.log" 2>&1
run standard "$BASE" & run contested "$BASE,$LIVE" & wait
echo done
