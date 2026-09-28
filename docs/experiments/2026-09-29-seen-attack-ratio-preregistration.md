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
Not run yet.
