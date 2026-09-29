#!/usr/bin/env bash
# BaseSightingWeightCap 1.0 vs 0.5 (docs/experiments/2026-09-30-base-sighting-cap-preregistration.md).
set -euo pipefail
cd "$(dirname "$0")/../../.."
OUT=docs/results/2026-09-30-base-sighting-cap
HO="ai-horde:easy,ai-horde:medium,ai-horde:hard,ai-armor:easy,ai-armor:medium,ai-armor:hard"
SEEDS=$(seq -s, 41 80)
git rev-parse --short HEAD > "$OUT/commit"
nix shell nixpkgs#dotnet-sdk_8 -c dotnet build tools/Bindery.Ra2.Bot.Arena -c Release -v q > "$OUT/build.log" 2>&1
arena() { nix shell nixpkgs#dotnet-sdk_8 -c dotnet run --project tools/Bindery.Ra2.Bot.Arena -c Release --no-build -- run "$@"; }
# P: pilot, smoke check only; decisions kept. Stop here and read P-* before the rest.
if [ "${1:-}" = pilot ]; then
  for v in 1.0 0.5; do   # TODO(knob): confirm the --knob name
    arena --arms selector --maps heldout --opponents ai-horde:hard,ai-armor:hard --seeds 2 --benchmark contested \
      --knob "BaseSightingWeightCap=$v" --out "$OUT/P-$v" > "$OUT/P-$v.log" 2>&1
  done
  python3 docs/experiments/gate_launch_analysis.py "$OUT/P-1.0" "$OUT/P-0.5" > "$OUT/P-analysis.txt" || true
  exit 0
fi
for v in 1.0 0.5; do
  arena --arms selector,selector-oracle --maps training --opponents training --seeds 40 --benchmark contested \
    --interleave --no-decisions --knob "BaseSightingWeightCap=$v" --out "$OUT/T-$v" > "$OUT/T-$v.log" 2>&1 &
  arena --arms selector,selector-oracle --maps heldout --opponents "$HO" --seed-list "$SEEDS" --benchmark contested \
    --interleave --no-decisions --knob "BaseSightingWeightCap=$v" --out "$OUT/H-$v" > "$OUT/H-$v.log" 2>&1 &
done
wait
python3 docs/experiments/seen_ratio_analysis.py "$OUT/T-1.0" "$OUT/T-0.5" > "$OUT/T-analysis.txt"
python3 docs/experiments/seen_ratio_analysis.py "$OUT/H-1.0" "$OUT/H-0.5" --min-seed 41 > "$OUT/H-analysis.txt"
