# Bindery region sim arena report

Results are from the bindery region simulator with the approximate `bindery-sim-approx` rules, not retail RA2; they are directional.
The arm is a full `BotRuntime`. Opponents named `ai-*` are the independent scripted AI (`Bindery.Ra2.Bot.Sim.Opponents`, no shared planner code; `:easy`/`:medium`/`:hard`, default hard); the other opponents are pinned-playbook styles running a frozen copy of the bot's stack as of commit 7f3e2c7 (`Bindery.Ra2.Bot.Baseline`), a stationary benchmark; `live-<style>` runs a pinned style on the live stack. The arm plays Allied on odd seeds and Soviet on even seeds. Match limit 1200 s (a timeout is won on final asset value).
Opponents are split as well as maps. Held-out opponents (`ai-horde`, `ai-armor`, independent scripted styles) are never used for the selector's default playbook, bandit learning, distillation datasets or tuning; see "Held-out opponents" below. They are not untouched: their difficulty and the contested benchmark's handicaps were calibrated against the selector's win rate on all five maps, held-out maps included, so the selector's own held-out win rate is near one half partly by construction. Every other opponent is a training opponent: the selector's default playbook was chosen from a style-versus-style matrix against the pinned-playbook styles (training maps only), so win rates against training opponents are partly in-sample.
Benchmark: contested (opponent income ×1, Allied income ×1, Allied starting credits 20000, opponent starting credits default, combat noise ±0.25).
Labels in this run: `knob:SeenAttackForceRatio=1.2`, `oracle`.

Matches: 960

## Win rate (arm × split)

Distinct games drop repeats of an identical game (same arm faction, decision log and outcome on the same map): against a differently named opponent whose style had not diverged when the match ended, or on another seed that changed nothing; the distinct interval is the one to read. The arm plays Allied on odd seeds and Soviet on even seeds, and starts west on seeds 1-2, 5-6, ... and east on 3-4, 7-8, ...; the fixture is asymmetric, so the faction and side columns show the mix behind each rate.

| Arm | Split | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) | Distinct games | Distinct wins | 95% interval, distinct | As Allied | As Soviet | As west | As east | Eliminations won | Timeouts |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| selector | heldout | 353 | 127 | 0 | 480 | 0.735 | [0.694, 0.773] | 468 | 341 | [0.687, 0.767] | 163/240 | 190/240 | 175/240 | 178/240 | 305 | 104 |
| selector-oracle | heldout | 312 | 168 | 0 | 480 | 0.650 | [0.606, 0.691] | 450 | 282 | [0.581, 0.670] | 137/240 | 175/240 | 154/240 | 158/240 | 231 | 165 |

Benchmark check: the baseline `selector` scored 0.735 over 480 matches, outside the 30–70% band: the benchmark is saturated and win-rate comparisons carry little information (use `--benchmark contested`).

## Win rate by opponent style

| Arm | Split | ai-armor:easy | ai-armor:hard | ai-armor:medium | ai-horde:easy | ai-horde:hard | ai-horde:medium |
|---|---|---|---|---|---|---|---|
| selector | heldout | 61/80 | 47/80 | 61/80 | 65/80 | 53/80 | 66/80 |
| selector-oracle | heldout | 62/80 | 48/80 | 60/80 | 60/80 | 38/80 | 44/80 |

## Held-out opponents

Opponent split × map split. Held-out opponents never inform the selector's defaults, bandit learning (the arena abandons the bandit's episode instead of crediting it, as it does on held-out maps), distillation datasets or teacher runs, or tuning (the tuner refuses them); their difficulty was calibrated against the selector on all maps, so read the selector's own held-out rate as partly calibrated and other arms' rates relative to it.

| Arm | Opponents | Maps | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) |
|---|---|---|---|---|---|---|---|---|
| selector | heldout | heldout | 353 | 127 | 0 | 480 | 0.735 | [0.694, 0.773] |
| selector-oracle | heldout | heldout | 312 | 168 | 0 | 480 | 0.650 | [0.606, 0.691] |
| selector | heldout | all | 353 | 127 | 0 | 480 | 0.735 | [0.694, 0.773] |
| selector-oracle | heldout | all | 312 | 168 | 0 | 480 | 0.650 | [0.606, 0.691] |

## Perception bottleneck (belief − oracle)

Pairs share opponent, map and seed. Difference = arm − baseline, mean with a 95% seeded bootstrap interval; better/worse/tied count pairs by the metric's direction (n/d: no better direction, counted as higher/lower); p is the exact two-sided sign test over untied pairs, Holm-adjusted over the arm's metrics. `*` marks Holm p < 0.05.

### selector vs selector-oracle (477 pairs; 3 identical to another opponent's pair collapsed)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 477 | 0.648 | 0.734 | 0.086 [0.052, 0.124] | 59 / 18 / 400 | <0.0001 | <0.0001 * |
| final asset margin | 477 | 5760 | 7972 | 2212 [501, 3986] | 228 / 249 / 0 | 0.3598 | 1.0000 |
| value destroyed | 477 | 13189 | 12695 | -494 [-1189, 161] | 219 / 219 / 39 | 1.0000 | 1.0000 |
| value lost | 477 | 17675 | 14279 | -3396 [-4682, -2246] | 299 / 143 / 35 | <0.0001 | <0.0001 * |
| trade share | 477 | 0.551 | 0.626 | 0.075 [0.056, 0.097] | 326 / 150 / 1 | <0.0001 | <0.0001 * |
| peak army value | 477 | 3975 | 3930 | -45.283 [-181, 92.453] | 244 / 219 / 14 | 0.2647 | 1.0000 |
| units built | 477 | 65 | 57.828 | -7.172 [-9.53, -4.95] | 207 / 184 / 86 | 0.2659 | 1.0000 |
| production idle fraction | 477 | 0.007 | 0.009 | 0.001 [-0.001, 0.003] | 198 / 252 / 27 | 0.0124 | 0.0991 |
| average credits (float) | 477 | 12434 | 11610 | -824 [-998, -664] | 328 / 149 / 0 | <0.0001 | <0.0001 * |
| first attack s | 477 | 224 | 157 | -67.31 [-74.092, -60.43] | 96 / 381 / 0 (n/d) | <0.0001 | <0.0001 * |
| duration s | 477 | 819 | 715 | -104 [-128, -79.363] | 165 / 252 / 60 (n/d) | <0.0001 | 0.0002 * |
| activations /10 min | 477 | 2.094 | 2.173 | 0.079 [-0.025, 0.185] | 211 / 256 / 10 (n/d) | 0.0416 | 0.2914 |
| invalid plan rate | 477 | 0 | 0 | 0 [0, 0] | 0 / 0 / 477 | 1.0000 | 1.0000 |
| USD per match | 477 | 0 | 0 | 0 [0, 0] | 0 / 0 / 477 | 1.0000 | 1.0000 |

## Economy, production and combat (arm side, per-match averages)

Production idle: seconds with a factory, nothing queued and credits for the cheapest item, over match seconds. Trade efficiency: enemy value destroyed in combat / own value lost in combat (pooled over matches).

Time to first attack: first second a combat unit stood in a region holding an enemy structure (mean over matches where it happened; count in brackets).

| Arm | Duration s | Units built | Buildings built | Peak army value | Idle fraction | Avg credits | Final assets (arm / opp) | Trade efficiency | First attack s |
|---|---|---|---|---|---|---|---|---|---|
| selector | 713 | 57.7 | 9.4 | 3922 | 0.009 | 11579 | 27553 / 19620 | 0.89 (6086000/6813400) | 157 (480/480) |
| selector-oracle | 816 | 64.8 | 11.3 | 3966 | 0.007 | 12399 | 28354 / 22613 | 0.75 (6321700/8433900) | 224 (480/480) |

## Strategy layer (arm side)

Proposals, invalid plans and lateness are the arm's primary strategist's: rejected / proposals; seconds from a proposal's snapshot to its validation; late-discard count / proposals. Fallback and emergency proposals (immediate and always valid) are counted apart so they cannot flatter the strategist under test. Shadow columns are the shadow strategist's own lateness and would-be invalid plans. Churn: activations and posture flips per 10 game minutes.

Shadow agreement: shadow proposals naming the same playbook as the primary proposal for the same request / shadow proposals whose request got a primary proposal.

| Arm | Proposals | Invalid plans | Mean lateness s | Late-discarded | Fallback proposals | Failed requests | Shadow proposals | Shadow invalid | Shadow mean lateness s | Shadow agreement | Activations /10 min | Posture flips /10 min |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| selector | 18539 | 0/18539 (0.000) | 0.00 | 0/18539 (0.000) | 0 | 0 | 0 | n/a | n/a | n/a | 2.57 | 0.90 |
| selector-oracle | 20931 | 0/20931 (0.000) | 0.00 | 0/20931 (0.000) | 0 | 0 | 0 | n/a | n/a | n/a | 2.25 | 0.99 |

## Command gate and simulator rejections (arm side, totals)

- **selector**: 0 dropped by the gate (none); 2460 commands rejected by the simulator.
- **selector-oracle**: 0 dropped by the gate (none); 13130 commands rejected by the simulator.

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

