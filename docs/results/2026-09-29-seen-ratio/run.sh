#!/usr/bin/env bash
# SeenAttackForceRatio 0.8 vs 1.0 (docs/experiments/2026-09-29-seen-attack-ratio-preregistration.md).
set -euo pipefail
cd "$(dirname "$0")/../../.."
OUT=docs/results/2026-09-29-seen-ratio
H2="ai-horde:easy,ai-horde:medium,ai-horde:hard,ai-armor:easy,ai-armor:medium,ai-armor:hard"
nix shell nixpkgs#dotnet-sdk_8 -c dotnet build tools/Bindery.Ra2.Bot.Arena -c Release -v q > "$OUT/build.log" 2>&1
run() { # cell maps opponents seeds value
  nix shell nixpkgs#dotnet-sdk_8 -c dotnet run --project tools/Bindery.Ra2.Bot.Arena -c Release --no-build -- run \
    --arms selector,selector-oracle --maps "$2" --opponents "$3" --seeds "$4" --benchmark contested --no-decisions \
    --knob "SeenAttackForceRatio=$5" --out "$OUT/$1-$5" > "$OUT/$1-$5.log" 2>&1
}
for v in 0.8 1.0; do run T training training 40 $v & run H1 heldout training 40 $v & run H2 heldout "$H2" 80 $v & done
wait
for c in T H1; do python3 docs/experiments/seen_ratio_analysis.py "$OUT/$c-0.8" "$OUT/$c-1.0" > "$OUT/$c-analysis.txt"; done
python3 docs/experiments/seen_ratio_analysis.py "$OUT/H2-0.8" "$OUT/H2-1.0" --min-seed 41 > "$OUT/H2-analysis.txt"
