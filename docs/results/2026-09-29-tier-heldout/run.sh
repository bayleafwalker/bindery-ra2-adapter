#!/usr/bin/env bash
# Pre-registered played held-out tier comparison (docs/experiments/2026-09-29-tier-heldout-preregistration.md).
set -euo pipefail
cd "$(dirname "$0")/../../.."
OUT=docs/results/2026-09-29-tier-heldout
curl -sf http://127.0.0.1:8020/v1/models | grep -q '"worker-fast"' || { echo "worker-fast not listed; wait" >&2; exit 3; }
git rev-parse --short HEAD > "$OUT/commit"
nix shell nixpkgs#dotnet-sdk_8 -c dotnet build tools/Bindery.Ra2.Bot.Arena -c Release -v q > "$OUT/build.log" 2>&1
nix shell nixpkgs#dotnet-sdk_8 -c dotnet run --project tools/Bindery.Ra2.Bot.Arena -c Release --no-build -- run \
  --arms llm-t1,llm-t2,llm-t3 --maps heldout \
  --opponents ai-horde:easy,ai-horde:medium,ai-horde:hard,ai-armor:easy,ai-armor:medium,ai-armor:hard \
  --seeds 8 --benchmark contested \
  --llm-endpoint http://127.0.0.1:8020/v1 --llm-model worker-fast \
  --write-adoption "$OUT/vocabulary-adoption.json" --out "$OUT/run" > "$OUT/run.log" 2>&1
