# Bindery region sim arena report

Results are from the bindery region simulator with the approximate `bindery-sim-approx` rules, not retail RA2; they are directional.
The arm is a full `BotRuntime`. Opponents named `ai-*` are the independent scripted AI (`Bindery.Ra2.Bot.Sim.Opponents`, no shared planner code; `:easy`/`:medium`/`:hard`, default hard); the other opponents are pinned-playbook styles running a frozen copy of the bot's stack as of commit 7f3e2c7 (`Bindery.Ra2.Bot.Baseline`), a stationary benchmark; `live-<style>` runs a pinned style on the live stack. The arm plays Allied on odd seeds and Soviet on even seeds. Match limit 1200 s (a timeout is won on final asset value).
Opponents are split as well as maps. Held-out opponents (`ai-horde`, `ai-armor`, independent scripted styles) are never used for the selector's default playbook, bandit learning, distillation datasets or tuning; see "Held-out opponents" below. They are not untouched: their difficulty and the contested benchmark's handicaps were calibrated against the selector's win rate on all five maps, held-out maps included, so the selector's own held-out win rate is near one half partly by construction. Every other opponent is a training opponent: the selector's default playbook was chosen from a style-versus-style matrix against the pinned-playbook styles (training maps only), so win rates against training opponents are partly in-sample.
Benchmark: contested (opponent income ×1, Allied income ×1, Allied starting credits 20000, opponent starting credits default, combat noise ±0.25).
Labels in this run: `oracle`.

Matches: 960

## Win rate (arm × split)

Distinct games drop repeats of an identical game (same arm faction, decision log and outcome on the same map): against a differently named opponent whose style had not diverged when the match ended, or on another seed that changed nothing; the distinct interval is the one to read. The arm plays Allied on odd seeds and Soviet on even seeds, and starts west on seeds 1-2, 5-6, ... and east on 3-4, 7-8, ...; the fixture is asymmetric, so the faction and side columns show the mix behind each rate.

| Arm | Split | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) | Distinct games | Distinct wins | 95% interval, distinct | As Allied | As Soviet | As west | As east | Eliminations won | Timeouts |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| selector | heldout | 356 | 124 | 0 | 480 | 0.742 | [0.701, 0.779] | 468 | 344 | [0.693, 0.773] | 166/240 | 190/240 | 179/240 | 177/240 | 302 | 109 |
| selector-oracle | heldout | 337 | 143 | 0 | 480 | 0.702 | [0.660, 0.741] | 450 | 307 | [0.638, 0.724] | 121/240 | 216/240 | 168/240 | 169/240 | 280 | 95 |

Benchmark check: the baseline `selector` scored 0.742 over 480 matches, outside the 30–70% band: the benchmark is saturated and win-rate comparisons carry little information (use `--benchmark contested`).

## Win rate by opponent style

| Arm | Split | ai-armor:easy | ai-armor:hard | ai-armor:medium | ai-horde:easy | ai-horde:hard | ai-horde:medium |
|---|---|---|---|---|---|---|---|
| selector | heldout | 63/80 | 47/80 | 62/80 | 65/80 | 53/80 | 66/80 |
| selector-oracle | heldout | 60/80 | 41/80 | 55/80 | 60/80 | 63/80 | 58/80 |

## Held-out opponents

Opponent split × map split. Held-out opponents never inform the selector's defaults, bandit learning (the arena abandons the bandit's episode instead of crediting it, as it does on held-out maps), distillation datasets or teacher runs, or tuning (the tuner refuses them); their difficulty was calibrated against the selector on all maps, so read the selector's own held-out rate as partly calibrated and other arms' rates relative to it.

| Arm | Opponents | Maps | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) |
|---|---|---|---|---|---|---|---|---|
| selector | heldout | heldout | 356 | 124 | 0 | 480 | 0.742 | [0.701, 0.779] |
| selector-oracle | heldout | heldout | 337 | 143 | 0 | 480 | 0.702 | [0.660, 0.741] |
| selector | heldout | all | 356 | 124 | 0 | 480 | 0.742 | [0.701, 0.779] |
| selector-oracle | heldout | all | 337 | 143 | 0 | 480 | 0.702 | [0.660, 0.741] |

## Perception bottleneck (belief − oracle)

Pairs share opponent, map and seed. Difference = arm − baseline, mean with a 95% seeded bootstrap interval; better/worse/tied count pairs by the metric's direction (n/d: no better direction, counted as higher/lower); p is the exact two-sided sign test over untied pairs, Holm-adjusted over the arm's metrics. `*` marks Holm p < 0.05.

### selector vs selector-oracle (478 pairs; 2 identical to another opponent's pair collapsed)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 478 | 0.701 | 0.741 | 0.04 [0.004, 0.079] | 52 / 33 / 393 | 0.0503 | 0.2010 |
| final asset margin | 478 | 3998 | 8209 | 4211 [2497, 6034] | 231 / 247 / 0 | 0.4927 | 1.0000 |
| value destroyed | 478 | 11507 | 12680 | 1173 [609, 1731] | 290 / 167 / 21 | <0.0001 | <0.0001 * |
| value lost | 478 | 17085 | 13970 | -3115 [-4416, -1832] | 279 / 168 / 31 | <0.0001 | <0.0001 * |
| trade share | 478 | 0.582 | 0.628 | 0.047 [0.023, 0.07] | 303 / 174 / 1 | <0.0001 | <0.0001 * |
| peak army value | 478 | 3365 | 3840 | 475 [353, 595] | 294 / 157 / 27 | <0.0001 | <0.0001 * |
| units built | 478 | 59.224 | 57.111 | -2.113 [-4.523, 0.238] | 247 / 147 / 84 | <0.0001 | <0.0001 * |
| production idle fraction | 478 | 0.006 | 0.008 | 0.002 [0.001, 0.004] | 273 / 192 / 13 | 0.0002 | 0.0012 * |
| average credits (float) | 478 | 12512 | 11665 | -847 [-1005, -694] | 377 / 100 / 1 | <0.0001 | <0.0001 * |
| first attack s | 478 | 214 | 157 | -56.492 [-62.04, -50.542] | 96 / 382 / 0 (n/d) | <0.0001 | <0.0001 * |
| duration s | 478 | 715 | 712 | -2.764 [-29.356, 23.088] | 271 / 173 / 34 (n/d) | <0.0001 | <0.0001 * |
| activations /10 min | 478 | 1.957 | 2.166 | 0.21 [0.111, 0.307] | 201 / 271 / 6 (n/d) | 0.0015 | 0.0073 * |
| invalid plan rate | 478 | 0 | 0 | 0 [0, 0] | 0 / 0 / 478 | 1.0000 | 1.0000 |
| USD per match | 478 | 0 | 0 | 0 [0, 0] | 0 / 0 / 478 | 1.0000 | 1.0000 |

## Economy, production and combat (arm side, per-match averages)

Production idle: seconds with a factory, nothing queued and credits for the cheapest item, over match seconds. Trade efficiency: enemy value destroyed in combat / own value lost in combat (pooled over matches).

Time to first attack: first second a combat unit stood in a region holding an enemy structure (mean over matches where it happened; count in brackets).

| Arm | Duration s | Units built | Buildings built | Peak army value | Idle fraction | Avg credits | Final assets (arm / opp) | Trade efficiency | First attack s |
|---|---|---|---|---|---|---|---|---|---|
| selector | 711 | 57.0 | 9.3 | 3836 | 0.008 | 11644 | 27794 / 19613 | 0.91 (6080700/6678800) | 157 (480/480) |
| selector-oracle | 713 | 59.1 | 10.4 | 3362 | 0.006 | 12488 | 25681 / 21687 | 0.68 (5520200/8168400) | 214 (480/480) |

## Strategy layer (arm side)

Proposals, invalid plans and lateness are the arm's primary strategist's: rejected / proposals; seconds from a proposal's snapshot to its validation; late-discard count / proposals. Fallback and emergency proposals (immediate and always valid) are counted apart so they cannot flatter the strategist under test. Shadow columns are the shadow strategist's own lateness and would-be invalid plans. Churn: activations and posture flips per 10 game minutes.

Shadow agreement: shadow proposals naming the same playbook as the primary proposal for the same request / shadow proposals whose request got a primary proposal.

| Arm | Proposals | Invalid plans | Mean lateness s | Late-discarded | Fallback proposals | Failed requests | Shadow proposals | Shadow invalid | Shadow mean lateness s | Shadow agreement | Activations /10 min | Posture flips /10 min |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| selector | 18487 | 0/18487 (0.000) | 0.00 | 0/18487 (0.000) | 0 | 0 | 0 | n/a | n/a | n/a | 2.54 | 0.86 |
| selector-oracle | 18406 | 0/18406 (0.000) | 0.00 | 0/18406 (0.000) | 0 | 0 | 0 | n/a | n/a | n/a | 2.05 | 0.78 |

## Command gate and simulator rejections (arm side, totals)

- **selector**: 0 dropped by the gate (none); 2578 commands rejected by the simulator.
- **selector-oracle**: 0 dropped by the gate (none); 11594 commands rejected by the simulator.

## Inference cost (arm side)

- **selector** (no model): 0 in / 0 out tokens and $0.0000 per match, of which $0.0000 on failed requests.
- **selector-oracle** (no model): 0 in / 0 out tokens and $0.0000 per match, of which $0.0000 on failed requests.

## Hidden-information leakage

- **selector**: 0 validator `fog.*` rejections.
- **selector-oracle**: 0 validator `fog.*` rejections.

Probe: two lockstep simulations, hidden state of one perturbed (`SimLeakageProbe`: enemy credits and queue, wounded hidden enemies, a hidden unit in an unseen region, one announced there through the event path, and one just across a border inside the arm's weapon reach), strategist-context hash compared on every following frame until the window closes or the objects the arm can see first differ. A differing frame is one where the context changed while everything visible was still identical. Fog-violation frames are arm frames, over the whole run of both simulations, that carried an enemy object or event from a region the arm did not see: a per-frame check that finds leaks the perturbation does not exercise.

| Arm | Map | Seed | Perturbed at s | Lockstep before | Compared s | Differing frames | Fog-violation frames | Note |
|---|---|---|---|---|---|---|---|---|
| selector | open-steppe | 1 | 90 | yes | 45 | 0/671 | 0 | stopped at 135 s: the arm saw a legitimate difference |
| selector | open-steppe | 1 | 240 | yes | 0 | 0/1 | 0 | stopped at 240 s: the arm saw a legitimate difference |
| selector-oracle | open-steppe | 1 | 90 | no | 0 | 0/0 | 0 | not applicable: oracle frames carry hidden state by design |
| selector-oracle | open-steppe | 1 | 240 | no | 0 | 0/0 | 0 | not applicable: oracle frames carry hidden state by design |

