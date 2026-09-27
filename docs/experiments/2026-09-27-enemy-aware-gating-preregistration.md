# Enemy-aware attack gating: pre-registration (2026-09-27, before any gated run)
Prior source: training maps x training opponents, selector-oracle, contested, 4 seeds (168 traces).
p75 running-peak opponent army value: 0 until ~75 s, 1300 at 180 s -> 12 credits/s after 75 s.
Constants (fixed; no tuning after held-out results): PriorValuePerSecond 12, PriorStartSeconds 75,
EvidenceSeconds 60, UncertaintyMargin 0.5, MinAttackForceRatio 1.2.
Gate: fresh attack launch additionally requires own / enemyUpper >= 1.2 (all playbooks).
w = confidence * max(0, 1 - newestAge/60); expected = w*seen + (1-w)*max(seen, prior(t));
upper = expected * (1 + 0.5*(1-w)).
Pass: OOS selector wins >= 40/72 AND median first attack later than baseline;
training (training maps x training opponents, 4 seeds): selector wins >= baseline - 2.

## Amendment (before any gated held-out run)
FullMatchTests vs ai-turtle (training opponent) failed to win in 900 s: the uncapped linear prior reached ~9900 (x1.5).
The same training data plateaus: p75 running peak 1600 from 210 s. Added EnemyPriorMaxValue 1600. No held-out data used.

## Outcome (contested, same commands as the baseline)
| Cell | Arm | Baseline (2e49971) | Gated | Paired flips |
|---|---|---|---|---|
| OOS heldout x ai-horde/ai-armor, 6 seeds | selector | 40/72 | 56/72 | +19 / -3 (ai-armor +14) |
| OOS | selector-oracle | 52/72 | 46/72 | +3 / -9 (ai-horde -9) |
| training x training opponents, 4 seeds | selector | 145/168 | 140/168 | +4 / -9 (live-tech -4, live-rush -3) |
| training | selector-oracle | 147/168 | 153/168 | +8 / -2 |
Median first attack (selector): OOS 127.5 s -> 127.5 s; training 124 s -> 124 s.
Pre-registered verdict: FAIL on two of three criteria (first attack not later; training -5 < -2). OOS wins criterion passes.
Confound: live-* training opponents run the live stack, so they are gated too.
Tests: dotnet test tests/Bindery.Ra2.Bot.Tests -> Failed: 1, Passed: 786, Skipped: 1, Total: 788
(FullMatchTests.The_bot_builds_an_economy_and_an_army_and_attacks: no winner by 900 s vs ai-turtle on twin-valley).

# v2 pre-registration (written before the v2 held-out run; operator approved continuing)
Changes from v1, all driven by training data or the failing training-opponent test, none by held-out results:
- Required ratio slides with evidence weight w: SeenAttackForceRatio (w = 1) to MinAttackForceRatio (w = 0).
  Training sweep (4 seeds, arm-only `--knob`, live-* opponents at defaults): Min in {1.0, 1.2, 1.5} indistinguishable
  (selector 138-140/168); Seen 1.2 cost the oracle arm 3 wins vs Seen 0/0.8 (153 vs 156). Chosen: Min 1.2, Seen 0.8.
- Evidence weight = max(army confidence x freshness, freshness of the last look at a known enemy base region):
  FullMatchTests vs ai-turtle failed because an empty, freshly scouted base left w = 0 and the prior dominated.
- Arena `--knob Name=value` applies tuning knobs to arms only; gate settings are tuning knobs.
Training at v2 (same opponents): selector 142/168 gate-on vs 145/168 gate-off; oracle 153 vs 153.
Tests at v2: Failed 0, Passed 796, Skipped 1, Total 797.
v2 criteria (single held-out run, no retuning after it):
1. OOS selector >= 40/72 (baseline).
2. OOS selector-oracle >= 50/72 (baseline 52 minus 2).
3. Median first attack later than baseline: EXPECTED TO FAIL by construction (the ~124 s attack passes the gate on
   the training prior); kept so the report says so, not withdrawn.
v2 outcome (held-out, contested, 6 seeds; same command as baseline):
| Arm | Baseline | v2 | Paired flips | Sign p |
|---|---|---|---|---|
| selector | 40/72 | 55/72 | +19 / -4 | 0.0026 |
| selector-oracle | 52/72 | 49/72 | +1 / -4 | 0.375 |
Median first attack: selector 127.5 s -> 127.5 s; oracle 213 s -> 213 s.
Verdict: criterion 1 PASS; criterion 2 FAIL by one match (49 < 50; not significant); criterion 3 FAIL as predicted.
Held-out opponents were not used to choose any constant; no retuning follows this run.
