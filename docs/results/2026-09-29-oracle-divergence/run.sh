#!/usr/bin/env bash
# Pre-registered traced divergence run (docs/experiments/2026-09-29-oracle-allied-divergence-preregistration.md).
set -euo pipefail
cd "$(dirname "$0")/../../.."
OUT=docs/results/2026-09-29-oracle-divergence
git rev-parse --short HEAD > "$OUT/commit"
nix shell nixpkgs#dotnet-sdk_8 -c dotnet build tools/Bindery.Ra2.Bot.Arena -c Release -v q > "$OUT/build.log" 2>&1
nix shell nixpkgs#dotnet-sdk_8 -c dotnet run --project tools/Bindery.Ra2.Bot.Arena -c Release --no-build -- run \
  --arms selector,selector-oracle --maps heldout \
  --opponents ai-horde:easy,ai-horde:medium,ai-horde:hard,ai-armor:easy,ai-armor:medium,ai-armor:hard \
  --seeds 40 --benchmark contested --no-decisions --knob SeenAttackForceRatio=1.0 \
  --trace "$OUT/trace" --out "$OUT/run" > "$OUT/run.log" 2>&1
python3 docs/experiments/oracle_divergence.py "$OUT/run" "$OUT/trace" > "$OUT/analysis.txt"
