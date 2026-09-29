# Attack gate: cap a base sighting's evidence weight at 0.5 (pre-registration, 2026-09-30; closed before the run)
**Status: Closed before the run (2026-09-29 ~22:45): refuted by a free check; no registered run was made.** The design below
is kept as drafted and was never executed; see Outcome.

Question: should `BaseSightingWeightCap` default to 0.5 instead of 1.0? The knob is being added by a parallel worker
and is not merged. TODO(knob): confirm the name, its range, where it is read (expected `EnemyArmyBound.EvidenceWeight`)
and that 1.0 reproduces current behaviour exactly, before this file is finalised.

Mechanism under test. `EnemyArmyBound.EvidenceWeight` is `max(army confidence x freshness, base-seen freshness)`,
and "a base seen recently with no army in it" counts as full evidence (`EnemyArmyBound.cs`). That weight w slides
the launch requirement from 1.2 (`MinAttackForceRatio`, w = 0) down to 0.8 (`SeenAttackForceRatio`, w = 1) and also
shrinks the enemy army bound (`Upper`: less prior, less uncertainty margin). The hypothesis: a scout's first sight
of an enemy base, with no army in view, drops the requirement to 0.8 and lifts the ratio in one tick, so the
selector launches on a sighting rather than on a force advantage, and those attacks lose. A cap of 0.5 on the
base-sighting term would leave the requirement at 1.0 and keep more of the prior in the bound at a fresh sighting.
Army sightings are untouched (TODO(knob): confirm the cap applies to the base term only).

## Evidence before the run
Written from existing archives, before any run of this experiment. This is what motivated the test; it is not a
result of it, and it does not decide anything. Script: `docs/experiments/gate_launch_analysis.py` (stdlib; extract the
`decisions.tar.xz` archives to a scratch directory first, nothing extracted is committed).

Data limits, stated first:
- `docs/results/2026-09-29-claude-transfer-pilot/twenty-cell-luna/` (the 20-cell test in the brief) is not on main
  (main 8f8b515), so the "every hard fortress-choke cell staged at 0.78/1.20 then launched ~1,800 at frame ~3075"
  observation could not be re-derived from its logs. The one archived match with exactly that pattern is
  `selector-same-cells/selector_ai-horde-medium_fortress-choke_2` (0.78/1.20 at frame 3060, attacking with 1,800 at
  3075). That match was won by the arm (`winner 0`), so even the exemplar is not a loss; its Allied twin
  `fortress-choke_1` (launch at frame 3330 from 0.92/1.20) was lost.
- Selector decision logs are rare: `--no-decisions` was used for every large run (seen-ratio, oracle-gap,
  oracle-scouting, oracle-divergence). Logs with staging ratios exist for 28 distinct selector-played matches:
  4 in `claude-transfer-pilot/selector-same-cells` and 24 `llm-shadow-t3` matches (Primary strategist = selector, the
  LLM only shadows) in `2026-09-28-llm-shadow-t3` and `2026-09-29-llm-shadow-short-rationale` (the same cells appear
  in both, counted once). The oracle-divergence traces (`discordant-traces.tar.xz`) are per-tick state dumps with no
  gate notes, so they carry no ratios.
- The launch note ("attacking rN with army value V") does not contain the ratio or the requirement. Whether the gate
  opened by a requirement drop or a ratio rise at launch cannot be seen; only the tick before is. The parallel
  worker's launch-note weight (TODO(note): format) will close this; `gate_launch_analysis.py` needs its regex
  updated then.

Classification: a launch is an `attacking` tick after a non-attacking tick. "jump" = the previous tick staged with
ratio below the requirement (so the gate opened between two ticks); "steady" = the previous tick already met the
requirement (the launch waited on something else, e.g. army size). Times are frame/15.

Table A, selector-played matches with decision logs (28 matches, 42 launches):

| Faction | Split | Matches | Launches | Jump launches | Jump share | First launch jump: wins/n | First launch steady: wins/n | Median launch (s) |
|---|---|---|---|---|---|---|---|---|
| Allied | training | 12 | 17 | 13 | 76% | 5/12 | 0/0 | 220 |
| Allied | held-out | 2 | 6 | 5 | 83% | 1/2 | 0/0 | 416 |
| Soviet | training | 12 | 17 | 12 | 71% | 12/12 | 0/0 | 223 |
| Soviet | held-out | 2 | 2 | 2 | 100% | 2/2 | 0/0 | 206 |

Matches behind the counts: training = `llm-shadow-t3` `{ai-rush-hard, live-balanced}` x `{island-bridges,
river-crossing, twin-valley}` x seeds 1,3 (Allied) and 2,4 (Soviet); held-out = `selector` `ai-horde-medium` on
`fortress-choke` and `open-steppe`, seeds 1 (Allied) and 2 (Soviet). Every match's first launch was a jump; the
pre-launch tick sat at 0.71-0.92/1.20 (one at 1.05/1.09 and one at 1.17/1.20). Full per-match listing:
`python3 docs/experiments/gate_launch_analysis.py <dirs>`. Seeds 1/3 and 2/4 reproduce the same launch tick in
every cell, so these are about 14 independent launch patterns, not 28.

Table B, big n, no ratios (selector arm, `results.json.xz` of the seen-ratio runs at `SeenAttackForceRatio=0.8`):
first attack time is the only launch signal there. Selector wins by faction and first-attack bucket:

| Cell | Faction | first attack < 150 s | 150-400 s | never launched |
|---|---|---|---|---|
| T training (1680) | Allied | 550/616 | 70/90 | 0/134 |
| T training | Soviet | 807/829 | 7/11 | 0/0 |
| H1 held-out (1120) | Allied | 360/368 | 40/59 | 0/133 |
| H1 held-out | Soviet | 473/483 | 77/77 | - |
| H2 held-out (960, seeds 1-80) | Allied | 192/276 | 130/204 | 0/0 |
| H2 held-out | Soviet | 378/480 | - | - |

Hard-tier `ai-*` opponents, H2, first attack time and selector wins:
- fortress-choke, Soviet: launch at 119 s in all 80 matches, 5/80 won.
- fortress-choke, Allied: launch at 131 s, 20/40; launch at 251 s, 12/40.
- open-steppe, Soviet: launch at 123-124 s, 80/80. open-steppe, Allied: launch at 134-135 s 31/31; at 311-312 s 49/49.

Reading, honestly:
1. Supports the mechanism: in the 28 logged matches, all first launches (and 32 of 42 launches) are gate openings
   between two ticks, with the previous tick 0.1-0.5 below the requirement, and none were launches that waited
   with the ratio already clear. The staging log shows the requirement bouncing: in
   `selector-same-cells/selector_ai-horde-medium_fortress-choke_2` the gate opened at frame 1785 (0.78/1.20 to
   1.00/0.80) with the army at 600/1500, so no launch; the requirement then rose back to 1.20 over about 60 s as the
   sighting aged (ratio 1.35/0.93 at frame 2085 falling to 0.75/1.20 by 2685) while the army passed 1500 by frame
   2910 and still sat under the requirement for 150 more ticks; the launch came at 3075, the tick after 0.78/1.20.
   Inferred, not logged: the launch tick is the next sighting reopening. The margin at a reopening is thin (1.00
   vs 0.80 at frame 1785), which a cap of 0.5 (requirement 1.0) would remove.
2. Hard fortress-choke is the worst cell class: the selector's Soviet first attack lands at 119 s, the first
   sighting opening, and wins 5 of 80. That is consistent with "launches on the first sighting and loses".
3. Counter-evidence, strong: the same sighting-driven timing wins elsewhere. Soviet launches at 119-124 s win 80/80
   on open-steppe against the same hard opponents, and 807/829 in T. So an early launch is not sufficient to lose;
   fortress-choke's defensive terrain (the choke) is the difference, and a lower requirement did not create the
   bad outcome by itself. The archives cannot separate "launched at the wrong ratio" from "should never attack
   into this choke at any ratio".
4. Counter-evidence, on the cost of the cap: launching late or never is the worse failure in the data. Allied
   never launches in 134/840 T matches and 133/560 H1 matches and wins none of them. A cap of 0.5 raises the bar at
   every fresh sighting, so it can only push first launches later or to never. Allied first attacks after 150 s win
   less often than early ones in T (70/90 vs 550/616), H1 (40/59 vs 360/368) and H2 (130/204 vs 192/276), though
   late attacks are also the ones against stronger opponents, so this is a correlation, not a cost of lateness.
5. Not tested: no fortress-choke cell exists in the training split (maps: island-bridges, river-crossing,
   twin-valley), so the motivating cells are all held-out. The decision cell below has the mechanism (jump
   launches occur there: 25 of 34 launches in Table A's training rows) but not the motivating map. The only
   fortress-choke evidence for the cap comes from held-out seeds, which by rule cannot decide.
6. `llm-t1` (Luna, LLM-primary) decision logs in the same archives (81 held-out matches, `--include-llm`,
   confounded by the LLM's postures): Allied 124 of 140 launches jump, first-launch-jump wins 20/37 against
   first-launch-not-jump 4/4; Soviet 43 of 111 jump and 16/40 wins with no jump-first match. Suggestive for Allied,
   n = 4 for the comparison, not evidence.

Verdict: the mechanism is partly supported. Launches do coincide with gate openings caused by sightings, and the
worst archived cell class launches at the first opening and loses. But no archived data separates a jump launch
from a steady one on outcome (the logged matches have no steady launches), the same timing wins 80/80 on the open
map, and late or absent launches are more costly than early ones in the archives. The test is worth running only as
a bounded check; the expected effect on the training decision cell is small and may be unmeasurable (see Power).

## Design
Arms `selector,selector-oracle`; contested benchmark; `--interleave`; `--knob BaseSightingWeightCap=1.0` (baseline)
vs `=0.5` (variant), each in its own output directory (arm-only; `live-*` opponents keep defaults). The value 0.5 is
fixed now; no other value is run, before or after the pilot, whatever the pilot shows. TODO(knob): confirm the exact
`--knob` spelling once the parallel worker's branch merges; `--knob` rejects an unknown name, so a wrong spelling
fails loudly rather than silently running the baseline.

Cells:
- P (pilot, smoke check only): `--arms selector --maps heldout --opponents ai-horde:hard,ai-armor:hard --seeds 2`
  at caps 1.0 and 0.5, decisions kept. Eight cells per cap; the four fortress-choke cells (2 opponents x seeds 1
  Allied, 2 Soviet) are the pilot. It checks, and decides nothing: (a) the knob is honoured (the launch note reports
  a weight, TODO(note)); (b) cap 1.0 reproduces a build without the knob (same `decisionLogHash` in the 4 cells);
  (c) launches at cap 0.5 come later or not at all, and nothing crashes. Its win counts are not used.
- T (decision): `--maps training --opponents training --seeds 40`: 3 maps x 14 opponents x 40 seeds = 1680 matches
  per arm per cap (the report's opponent count is checked against 14; the seen-ratio file miscounted this as 22).
- H (non-inferiority, held-out): `--maps heldout --opponents ai-horde:easy,ai-horde:medium,ai-horde:hard,
  ai-armor:easy,ai-armor:medium,ai-armor:hard --seed-list 41,...,80` (seeds 41-80 only, run directly; seeds 1-40 were
  examined on 2026-09-28 and earlier): 2 maps x 6 opponents x 40 seeds = 480 matches per arm per cap, balanced by
  faction. The seen-ratio study ran seeds 1-80 and discarded half; `--seed-list` avoids that.

Criteria (pairing by arm, opponent, map, seed; exact two-sided sign test on discordant pairs; reported by faction):
- C1 (T, decisive, arm `selector`): cap 0.5 better-minus-worse > 0 with sign p < 0.05. The selector is the arm that
  scouts and is what would ship. `selector-oracle` is a control, reported only; the oracle's evidence comes from
  exact information, so if the cap moves it too (discordant pairs > 0), the cap reaches a path other than base
  sightings and the result is annotated. TODO(knob): confirm whether the cap touches the oracle at all.
- C2 (H, seeds 41-80, non-inferiority, arm `selector`): selector wins at 0.5 >= wins at 1.0 - 10 (margin 10 of 480,
  2.1 points, the same margin as C4 of the seen-ratio file), and neither faction falls more than 8 of 240 (3.3
  points). The per-faction bound is there because the two risks point at different factions: Soviet fortress-choke
  is where a gain would show, Allied never-launch is where a loss would.
- C3: with the default set to 0.5, `dotnet test` (Adapter and Bot, Release) passes, including FullMatchTests vs
  ai-turtle. Any test that pins the old default is updated and named in the change.
Decision: C1, C2 and C3 all pass -> default `BaseSightingWeightCap` 1.0 -> 0.5 in `OperationalOptions` (parameter doc
updated) and a results section. Otherwise 1.0 stays and the numbers are recorded. No other values and no further
runs of this knob either way. Sign-test p is not adjusted; there is one primary test.

## Power
C1 can only pass with at least 6 discordant pairs all in the variant's favour (6-0 gives p = 0.031; 5-0 gives
0.0625). T has about 100 selector first attacks after 150 s and 134 Allied never-launch matches out of 1680 (Table B), and
the mechanism can only act on those; if fewer than 6 pairs are discordant C1 fails as under-powered, and that is recorded as "not
adopted, not refuted", not as evidence against the hypothesis. The pre-registration accepts this: the alternative
is deciding on held-out fortress-choke seeds, which are reserved for the non-inferiority check.

## Reported only (not decided on)
Median and share of first attacks by cap (`firstAttackSeconds`), never-launch counts by faction, hard-tier
fortress-choke wins (Soviet baseline 5/80 on H seeds 1-80), and Table A recomputed from the pilot's decision logs
with the weight in the launch note.

## Cost and run time
Selector arms use no model: $0, CPU only. Rates from the archives: `oracle-scouting/R0` played 2880 matches in
908 s alone on the machine (3.2 matches/s); the seen-ratio T cell (3360 matches) took 3116 s and H2 (1920) 2628 s
with six cells running in parallel.
- Pilot: 32 matches, under 1 minute.
- T: 3360 matches per cap, 6720 in all: about 35 minutes serial at 3.2/s, about 55 minutes if both caps run in
  parallel with H as in the seen-ratio run.
- H: 960 matches per cap (seed-list 41-80), 1920 in all: about 10 minutes serial.
- Total about 45 minutes serial, about 1 hour wall clock in parallel. Analysis is seconds. No credentials, no LLM
  spend.

## Analysis
- Decision: `python3 docs/experiments/seen_ratio_analysis.py <cap-1.0 dir> <cap-0.5 dir> [--min-seed 41]`. Its output
  labels read `0.8`/`1.0` and mean baseline/variant (first directory = baseline); the paired test and sign p are
  what C1 and C2 use. TODO: either add a `--labels` option or copy the script; not done here to keep this file
  the only change to that analysis.
- Launch classification: `python3 docs/experiments/gate_launch_analysis.py <pilot decisions dirs>`.

## run.sh
Not run and not kept: the draft `docs/results/2026-09-30-base-sighting-cap/run.sh` was removed when the line was
closed (it is in commit 663926b if needed).

## Outcome
Closed before any registered run. A free check, made with the knob branch (W1, 95f6b1d; the knob is routed
correctly), refuted the hypothesis:

`arena run --arms selector --maps heldout --opponents ai-horde:hard,ai-armor:hard --seeds 4 --benchmark contested
--knob BaseSightingWeightCap=X` at X = 1.0, 0.5 and 0.0 gave 12/16 wins each, with identical winners on all 16 cells.
Only durations differed, on two cells at 0.0: ai-armor:hard fortress-choke s4 592 -> 656 s, ai-horde:hard
open-steppe s3 684 -> 689 s. The four fortress-choke losses (ai-armor:hard s2, s3; ai-horde:hard s2, s4) persist
even with base sightings giving no evidence at all (X = 0.0), so base-sighting evidence does not cause them.

Launch evidence. The belief selector's losing launch in ai-horde:hard fortress-choke s2 was
`attacking r8 with army value 1800 (force ratio 1.29/1.05, w 0.37, upper 1399, baseAge 86s)`: an army sighting
blended with a stale base sighting, not a base-only w = 1 launch. This contradicts item 1 of the reading in
"Evidence before the run", which inferred sighting-reopening launches from the previous tick alone.

Oracle contrast (`selector-oracle`, same cells):
- ai-horde:hard fortress-choke: wins s2 and s4 at about 320 s, attacking earlier with 1,500 on true information
  (`force ratio 0.94/0.80, upper 1600`), and s3 on timeout; loses s1 on timeout.
- ai-armor:hard fortress-choke: loses all 4 (3 timeouts, 1 elimination).
- So perception matters against horde-hard on the choke map (the belief bot attacks later and loses), while
  armor-hard on the choke map is a strategy problem that full information does not fix.
- For reference, gpt-6-luna won all of these cells as Soviet with a late mass attack (about 500 s, about 6,000 army).

Conclusion: the base-sighting-cap line is closed. `BaseSightingWeightCap` stays a diagnostic knob at 1.0; no default
change, no further runs. The evidence section and `gate_launch_analysis.py` are kept as the record of what motivated
the test; its Table A/B numbers stand as measurements, but the mechanism reading drawn from them is refuted.
