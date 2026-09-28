# Bindery region sim arena report

Results are from the bindery region simulator with the approximate `bindery-sim-approx` rules, not retail RA2; they are directional.
The arm is a full `BotRuntime`. Opponents named `ai-*` are the independent scripted AI (`Bindery.Ra2.Bot.Sim.Opponents`, no shared planner code; `:easy`/`:medium`/`:hard`, default hard); the other opponents are pinned-playbook styles running a frozen copy of the bot's stack as of commit 7f3e2c7 (`Bindery.Ra2.Bot.Baseline`), a stationary benchmark; `live-<style>` runs a pinned style on the live stack. The arm plays Allied on odd seeds and Soviet on even seeds. Match limit 1200 s (a timeout is won on final asset value).
Opponents are split as well as maps. Held-out opponents (`ai-horde`, `ai-armor`, independent scripted styles) are never used for the selector's default playbook, bandit learning, distillation datasets or tuning; see "Held-out opponents" below. They are not untouched: their difficulty and the contested benchmark's handicaps were calibrated against the selector's win rate on all five maps, held-out maps included, so the selector's own held-out win rate is near one half partly by construction. Every other opponent is a training opponent: the selector's default playbook was chosen from a style-versus-style matrix against the pinned-playbook styles (training maps only), so win rates against training opponents are partly in-sample.
Benchmark: contested (opponent income ×1, Allied income ×1, Allied starting credits 20000, opponent starting credits default, combat noise ±0.25).
Labels in this run: `knob:SeenAttackForceRatio=1`, `oracle`.

Matches: 1920

## Win rate (arm × split)

Distinct games drop repeats of an identical game (same arm faction, decision log and outcome on the same map): against a differently named opponent whose style had not diverged when the match ended, or on another seed that changed nothing; the distinct interval is the one to read. The arm plays Allied on odd seeds and Soviet on even seeds, and starts west on seeds 1-2, 5-6, ... and east on 3-4, 7-8, ...; the fixture is asymmetric, so the faction and side columns show the mix behind each rate.

| Arm | Split | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) | Distinct games | Distinct wins | 95% interval, distinct | As Allied | As Soviet | As west | As east | Eliminations won | Timeouts |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| selector | heldout | 702 | 258 | 0 | 960 | 0.731 | [0.702, 0.758] | 918 | 660 | [0.689, 0.747] | 324/480 | 378/480 | 350/480 | 352/480 | 597 | 213 |
| selector-oracle | heldout | 726 | 234 | 0 | 960 | 0.756 | [0.728, 0.782] | 863 | 629 | [0.698, 0.757] | 288/480 | 438/480 | 360/480 | 366/480 | 567 | 282 |

Benchmark check: the baseline `selector` scored 0.731 over 960 matches, outside the 30–70% band: the benchmark is saturated and win-rate comparisons carry little information (use `--benchmark contested`).

## Win rate by opponent style

| Arm | Split | ai-armor:easy | ai-armor:hard | ai-armor:medium | ai-horde:easy | ai-horde:hard | ai-horde:medium |
|---|---|---|---|---|---|---|---|
| selector | heldout | 125/160 | 95/160 | 121/160 | 129/160 | 102/160 | 130/160 |
| selector-oracle | heldout | 121/160 | 95/160 | 126/160 | 120/160 | 126/160 | 138/160 |

## Held-out opponents

Opponent split × map split. Held-out opponents never inform the selector's defaults, bandit learning (the arena abandons the bandit's episode instead of crediting it, as it does on held-out maps), distillation datasets or teacher runs, or tuning (the tuner refuses them); their difficulty was calibrated against the selector on all maps, so read the selector's own held-out rate as partly calibrated and other arms' rates relative to it.

| Arm | Opponents | Maps | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) |
|---|---|---|---|---|---|---|---|---|
| selector | heldout | heldout | 702 | 258 | 0 | 960 | 0.731 | [0.702, 0.758] |
| selector-oracle | heldout | heldout | 726 | 234 | 0 | 960 | 0.756 | [0.728, 0.782] |
| selector | heldout | all | 702 | 258 | 0 | 960 | 0.731 | [0.702, 0.758] |
| selector-oracle | heldout | all | 726 | 234 | 0 | 960 | 0.756 | [0.728, 0.782] |

## Perception bottleneck (belief − oracle)

Pairs share opponent, map and seed. Difference = arm − baseline, mean with a 95% seeded bootstrap interval; better/worse/tied count pairs by the metric's direction (n/d: no better direction, counted as higher/lower); p is the exact two-sided sign test over untied pairs, Holm-adjusted over the arm's metrics. `*` marks Holm p < 0.05.

### selector vs selector-oracle (951 pairs; 9 identical to another opponent's pair collapsed)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 951 | 0.754 | 0.729 | -0.025 [-0.053, 0.001] | 72 / 96 / 783 | 0.0757 | 0.2270 |
| final asset margin | 951 | 7901 | 7417 | -483 [-1690, 713] | 409 / 542 / 0 | <0.0001 | 0.0002 * |
| value destroyed | 951 | 12562 | 12548 | -14.826 [-426, 385] | 506 / 393 / 52 | 0.0002 | 0.0011 * |
| value lost | 951 | 14640 | 14583 | -56.151 [-1019, 899] | 484 / 396 / 71 | 0.0033 | 0.0133 * |
| trade share | 951 | 0.615 | 0.619 | 0.004 [-0.013, 0.021] | 539 / 406 / 6 | <0.0001 | 0.0002 * |
| peak army value | 951 | 3673 | 3893 | 220 [130, 312] | 506 / 396 / 49 | 0.0003 | 0.0014 * |
| units built | 951 | 60.858 | 58.027 | -2.831 [-4.499, -1.072] | 461 / 314 / 176 | <0.0001 | <0.0001 * |
| production idle fraction | 951 | 0.008 | 0.008 | -0 [-0.002, 0.001] | 539 / 361 / 51 | <0.0001 | <0.0001 * |
| average credits (float) | 951 | 12410 | 11682 | -728 [-842, -614] | 718 / 233 / 0 | <0.0001 | <0.0001 * |
| first attack s | 951 | 215 | 159 | -56.13 [-60.424, -51.685] | 204 / 747 / 0 (n/d) | <0.0001 | <0.0001 * |
| duration s | 951 | 731 | 717 | -13.478 [-32.321, 5.416] | 470 / 345 / 136 (n/d) | <0.0001 | 0.0001 * |
| activations /10 min | 951 | 1.946 | 2.171 | 0.226 [0.155, 0.295] | 404 / 536 / 11 (n/d) | <0.0001 | 0.0002 * |
| invalid plan rate | 951 | 0 | 0 | 0 [0, 0] | 0 / 0 / 951 | 1.0000 | 1.0000 |
| USD per match | 951 | 0 | 0 | 0 [0, 0] | 0 / 0 / 951 | 1.0000 | 1.0000 |

## Economy, production and combat (arm side, per-match averages)

Production idle: seconds with a factory, nothing queued and credits for the cheapest item, over match seconds. Trade efficiency: enemy value destroyed in combat / own value lost in combat (pooled over matches).

Time to first attack: first second a combat unit stood in a region holding an enemy structure (mean over matches where it happened; count in brackets).

| Arm | Duration s | Units built | Buildings built | Peak army value | Idle fraction | Avg credits | Final assets (arm / opp) | Trade efficiency | First attack s |
|---|---|---|---|---|---|---|---|---|---|
| selector | 713 | 57.8 | 9.4 | 3884 | 0.008 | 11636 | 27333 / 19967 | 0.87 (12023700/13874100) | 159 (960/960) |
| selector-oracle | 727 | 60.6 | 9.9 | 3663 | 0.008 | 12358 | 28183 / 20328 | 0.86 (12037200/13928700) | 215 (960/960) |

## Strategy layer (arm side)

Proposals, invalid plans and lateness are the arm's primary strategist's: rejected / proposals; seconds from a proposal's snapshot to its validation; late-discard count / proposals. Fallback and emergency proposals (immediate and always valid) are counted apart so they cannot flatter the strategist under test. Shadow columns are the shadow strategist's own lateness and would-be invalid plans. Churn: activations and posture flips per 10 game minutes.

Shadow agreement: shadow proposals naming the same playbook as the primary proposal for the same request / shadow proposals whose request got a primary proposal.

| Arm | Proposals | Invalid plans | Mean lateness s | Late-discarded | Fallback proposals | Failed requests | Shadow proposals | Shadow invalid | Shadow mean lateness s | Shadow agreement | Activations /10 min | Posture flips /10 min |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| selector | 37087 | 0/37087 (0.000) | 0.00 | 0/37087 (0.000) | 0 | 0 | 0 | n/a | n/a | n/a | 2.54 | 0.86 |
| selector-oracle | 36900 | 0/36900 (0.000) | 0.00 | 0/36900 (0.000) | 0 | 0 | 0 | n/a | n/a | n/a | 2.04 | 0.75 |

## Command gate and simulator rejections (arm side, totals)

- **selector**: 0 dropped by the gate (none); 5081 commands rejected by the simulator.
- **selector-oracle**: 0 dropped by the gate (none); 31911 commands rejected by the simulator.

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

