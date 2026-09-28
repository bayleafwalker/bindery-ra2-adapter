#!/usr/bin/env bash
# Pre-registered oracle scouting-cap run (docs/experiments/2026-09-29-oracle-scouting-preregistration.md).
set -euo pipefail
cd "$(dirname "$0")/../../.."
OUT=docs/results/2026-09-29-oracle-scouting
OPP="ai-horde:easy,ai-horde:medium,ai-horde:hard,ai-armor:easy,ai-armor:medium,ai-armor:hard"
git rev-parse --short HEAD > "$OUT/commit"
nix shell nixpkgs#dotnet-sdk_8 -c dotnet build tools/Bindery.Ra2.Bot.Arena -c Release -v q > "$OUT/build.log" 2>&1
arena() { nix shell nixpkgs#dotnet-sdk_8 -c dotnet run --project tools/Bindery.Ra2.Bot.Arena -c Release --no-build -- run "$@"; }
arena --arms selector,selector-oracle --maps heldout --opponents "$OPP" --seeds 120 --benchmark contested --no-decisions \
  --knob SeenAttackForceRatio=1.0 --out "$OUT/R0" > "$OUT/R0.log" 2>&1
arena --arms selector-oracle --maps heldout --opponents "$OPP" --seeds 120 --benchmark contested --no-decisions \
  --knob SeenAttackForceRatio=1.0 --knob ScoutingCoverageCap=0.39 --out "$OUT/R1" > "$OUT/R1.log" 2>&1
python3 docs/experiments/scouting_cap_analysis.py "$OUT/R0" "$OUT/R1" --min-seed 81 > "$OUT/analysis.txt"
