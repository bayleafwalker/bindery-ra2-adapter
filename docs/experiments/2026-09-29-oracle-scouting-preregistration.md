# Does the oracle's missing scout objective cost it Allied games? (pre-registration, 2026-09-29, before the run)
`2026-09-29-oracle-allied-divergence-preregistration.md` closed with no primary cause, but reported that the first
divergence in all 46 Allied discordant pairs is the selector's first scout order (second 106) against none from the
oracle, and that the oracle never scouts in 25 of those 46 games (19 of 31 selector-only, 6 of 15 oracle-only). The
oracle's scouting coverage is always 1.0, so `IntentComposer` never gives it a Scout objective. This asks, by
intervention rather than from those traces, whether that matters. It explains; it changes no default and adopts
nothing (`ScoutingCoverageCap` is a diagnostic knob the tuner never reads, PR #26).

Cell: as in the divergence study (held-out maps x ai-horde/ai-armor easy/medium/hard, contested,
`SeenAttackForceRatio=1.0`), but on fresh seeds: runs use `--seeds 120` and only seeds 81-120 are analysed (480
selector-oracle matches per run, 240 Allied), seeds no earlier analysis has looked at.
Runs (one each, at the commit in `docs/results/2026-09-29-oracle-scouting/commit`; `run.sh`):
- R0 baseline: `--arms selector,selector-oracle` (the selector only as a reference).
- R1 capped: `--arms selector-oracle --knob ScoutingCoverageCap=0.39`, below `IntentComposer`'s 0.4 threshold, so the
  oracle carries a Scout objective throughout (a persistent scout, stronger than the selector's opening-only one).
Analysis: `docs/experiments/scouting_cap_analysis.py R0 R1 --min-seed 81`, pairing selector-oracle matches by
opponent, map and seed.

Criterion S1: capped better than baseline in Allied pairs (more capped-only wins than baseline-only wins), exact
two-sided sign test p < 0.05. S1 passes -> the missing scout objective is recorded as a cause of part of the oracle's
Allied deficit (with R1's Allied wins against R0's selector reported). S1 fails -> recorded as no measurable effect,
and the line closes. Reported only: Soviet and overall paired results. One run each; no other cap values, seeds or
cells after the run.

## Outcome
Run once at 01870c2 (`docs/results/2026-09-29-oracle-scouting/commit`); `analysis.txt`, per-run `report.md` and
`results.json.xz` (the analysis reproduces from the compressed files).

Decision: **S1 fails**, and the line closes. The capped oracle is not better as Allied; it is much worse:

| seeds 81-120 | Allied | Soviet |
|---|---|---|
| R0 selector-oracle | 147/240 | 218/240 |
| R1 selector-oracle, ScoutingCoverageCap 0.39 | 102/240 | 222/240 |
| R0 selector (reference) | 151/240 | 186/240 |

Paired selector-oracle, capped against baseline: Allied better 1, worse 46 (sign p 6.8e-13); Soviet better 9, worse 5
(p 0.42); all better 10, worse 51.

Deviation in wording: the failure branch above says "no measurable effect"; the measured effect is large but in the
opposite direction to S1, so it is recorded as it is. What it shows: a Scout objective held all match costs the oracle
many Allied games. What it does not show: whether the selector's opening-only scout matters, because this
intervention is far stronger than that (as the design above said). The missing scout objective is therefore not
supported as a cause of the oracle's Allied deficit, and on these fresh seeds that deficit is small at baseline (147
against the selector's 151). No other cap values or seeds follow, as pre-registered.
