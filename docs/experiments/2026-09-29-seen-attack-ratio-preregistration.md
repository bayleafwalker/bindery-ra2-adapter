# Attack gate with exact evidence: SeenAttackForceRatio 0.8 vs 1.0 (pre-registration, 2026-09-28, before any run)
Question: should the enemy-aware gate's required ratio at full evidence weight (`SeenAttackForceRatio`,
`src/Bindery.Ra2.Bot/Operations/OperationalOptions.cs`) be 1.0 instead of 0.8? The gate formula is unchanged (no
special case for exact evidence): `RequiredForceRatio` already slides with evidence weight w
(`EnemyArmyBound.cs`), and the belief arm reaches w near 1 too.

Why 1.0, and why it may be tested: the v2 training sweep (`2026-09-27-enemy-aware-gating-preregistration.md`) tried
Seen 0, 0.8 and 1.2, never 1.0, so 1.0 is untested on training data. The held-out observation that the oracle arm wins
366/480 at 1.0 against 337/480 at 0.8 (`docs/architecture/strategic-bot-results.md`, 2026-09-28 section, measured at
fe3244a) motivates the question but may not decide it: held-out data only confirms, on seeds never examined. 1.2 is
excluded (lost on training and held-out). Code has changed since fe3244a (one-item-per-queue spending, blocked queues,
spawn fallback), so every number below is measured at the commit recorded in `docs/results/2026-09-29-seen-ratio/commit`.

Arms `selector,selector-oracle`; contested benchmark; `--no-decisions`; one run per value with `--knob
SeenAttackForceRatio=0.8` and `=1.0` (arm-only; `live-*` opponents keep defaults). Commands:
`docs/results/2026-09-29-seen-ratio/run.sh`. Analysis: `docs/experiments/seen_ratio_analysis.py` (committed with this
file, before any run).

Cells:
- T (decision): `--maps training --opponents training --seeds 40` (3 maps x 22 opponents x 40 seeds = 2640 matches per
  arm per value; the report's opponent count is checked against 22).
- H1 (reported only): `--maps heldout --opponents training --seeds 40`.
- H2 (confirmation, fully held-out): `--maps heldout --opponents ai-horde:easy,ai-horde:medium,ai-horde:hard,
  ai-armor:easy,ai-armor:medium,ai-armor:hard --seeds 80`; only seeds 41-80 are analysed (480 matches per arm per value,
  balanced by faction and start side). Seeds 1-40 of this cell were examined on 2026-09-28 and are excluded.

Criteria (paired by opponent, map and seed; exact two-sided sign test on discordant pairs; reported by faction):
- C1 (T): selector-oracle, 1.0 vs 0.8: better-minus-worse > 0 with sign p < 0.05.
- C2 (T): selector wins at 1.0 >= selector wins at 0.8 - 26 (1% of 2640).
- C3: with the default set to 1.0, `dotnet test` (Adapter and Bot, Release) passes, including FullMatchTests vs
  ai-turtle.
- C4 (H2, seeds 41-80): selector wins at 1.0 >= selector wins at 0.8 - 10; selector-oracle Allied wins at 1.0 >=
  selector-oracle Allied wins at 0.8.
Decision: C1-C4 all pass -> default 0.8 -> 1.0 in OperationalOptions (parameter doc updated) and a results section.
Otherwise 0.8 stays and the numbers are recorded. No other values and no further ratio runs either way.

## Outcome
Run once at 18f0aa5 (`docs/results/2026-09-29-seen-ratio/commit`) with the commands above; analyses
`{T,H1,H2}-analysis.txt`, per-cell `report.md` and `results.json.xz` (the analyses reproduce from the compressed files).

Decision: **C1 fails, so 0.8 stays.** No default change, no C3 run, no further ratio runs.

| Criterion | Measured | Result |
|---|---|---|
| C1 (T) | selector-oracle 1.0 vs 0.8: better 1, worse 0, sign p 1 | fail |
| C2 (T) | selector 1436 at 1.0 vs 1434 at 0.8 (threshold 1434 - 26) | pass |
| C3 | not run: the default changes only if C1, C2 and C4 pass | - |
| C4 (H2, seeds 41-80) | selector 348 vs 344 (threshold 344 - 10); selector-oracle Allied 140 vs 125 | pass |

Deviation from the text above, found when reading the results: the training cell has 14 opponents, not 22
(`OpponentSets.Training` at this commit: 4 `ai-*`, 5 pinned and 5 `live-*` styles), so T is 3 x 14 x 40 = 1680
matches per arm per value, not 2640, and H1 is 2 x 14 x 40 = 1120. The run used the pre-registered `--opponents
training` exactly; "22" was a miscount when writing this file, and the promised opponent-count check was never
added to `seen_ratio_analysis.py`. It does not change the decision: C1 needs p < 0.05 and has one discordant pair
(C2's 1% threshold would be 17 instead of 26; the measured difference is +2).

Reported only, not decided on: H2 selector-oracle 1.0 vs 0.8 is better 21, worse 4 (sign p 0.0009; Allied 17 vs
2), and H1 selector-oracle Allied is better 32, worse 7 (p 7e-05) while Soviet is better 8, worse 19 (p 0.052). On
the T cell the oracle arm is indifferent to the ratio (1 discordant pair of 1680); the held-out gain is the
same Allied pattern as the oracle-gap study and is not a reason to change the default from held-out data.
