#!/usr/bin/env bash
# Pre-registered A/B (docs/experiments/2026-09-29-defer-service-start-ab-preregistration.md): 60 stage-1 runs, ABBA x15.
# Resumable: runs already in results.tsv are skipped. Needs RA2_LAB_HOME and RA2_LAB_TUNNEL.
set -uo pipefail
cd "$(dirname "$0")/../../.."
OUT=docs/results/2026-09-29-defer-service-ab
: "${RA2_LAB_HOME:?}" "${RA2_LAB_TUNNEL:?}"
git rev-parse --short HEAD > "$OUT/commit"
sha256sum "$RA2_LAB_HOME/payload/fork/libra2yrcpp.dll" > "$OUT/payload.txt"
touch "$OUT/results.tsv"
infra=0
for i in $(seq 1 60); do
  grep -q "^$i	" "$OUT/results.tsv" && continue
  case $(( (i - 1) % 4 )) in 0|3) arm=A defer=0 ;; *) arm=B defer=1 ;; esac
  before=$(ls -d "$RA2_LAB_HOME"/runs/*-s1 2>/dev/null | sort | tail -1)
  LAB_DEFER_SERVICE_START=$defer PREPARE_STAGE=2 timeout 3000 deploy/ra2-lab/lab-run.sh --stage 1 > "$OUT/run-$i.log" 2>&1
  rc=$?
  run=$(ls -d "$RA2_LAB_HOME"/runs/*-s1 | sort | tail -1)
  if [ "$run" = "$before" ] || [ ! -d "$run/evidence" ]; then
    echo "$(date +%T) run $i ($arm): no evidence (rc $rc)" | tee -a "$OUT/infra.log"
    infra=$((infra + 1)); [ $infra -ge 3 ] && { echo "three infrastructure failures in a row: paused" | tee -a "$OUT/infra.log"; exit 4; }
    continue
  fi
  infra=0
  python3 "$OUT/classify.py" "$run" "$arm" "$i" >> "$OUT/results.tsv"
  tail -2 "$OUT/results.tsv"
done
