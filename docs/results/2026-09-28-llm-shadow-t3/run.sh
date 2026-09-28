#!/usr/bin/env bash
# Pre-registered full-vocabulary LLM shadow pass (docs/experiments/2026-09-27-llm-shadow-full-vocabulary-preregistration.md).
# One run; no prompt or threshold changes after it.
set -euo pipefail
cd "$(dirname "$0")/../../.."
OUT=docs/results/2026-09-28-llm-shadow-t3
nix shell nixpkgs#dotnet-sdk_8 -c dotnet build tools/Bindery.Ra2.Bot.Arena -c Release -v q > "$OUT/build.log" 2>&1
nix shell nixpkgs#dotnet-sdk_8 -c dotnet run --project tools/Bindery.Ra2.Bot.Arena -c Release --no-build -- run \
  --arms llm-shadow-t3 --maps training --opponents live-balanced,ai-rush:hard --seeds 4 --benchmark contested \
  --llm-endpoint http://127.0.0.1:8020/v1 --llm-model worker-fast --out "$OUT/run" > "$OUT/run.log" 2>&1
python3 docs/experiments/shadow_metrics.py "$OUT/run" llm-shadow-t3 > "$OUT/metrics.txt"
# The raw decision logs are committed compressed: run/decisions.tar.xz (decisions/ + dataset ndjson).
# To recompute the metrics: tar -xJf run/decisions.tar.xz -C run && python3 docs/experiments/shadow_metrics.py run llm-shadow-t3
