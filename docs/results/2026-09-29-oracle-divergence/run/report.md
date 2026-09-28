# Bindery region sim arena report

Results are from the bindery region simulator with the approximate `bindery-sim-approx` rules, not retail RA2; they are directional.
The arm is a full `BotRuntime`. Opponents named `ai-*` are the independent scripted AI (`Bindery.Ra2.Bot.Sim.Opponents`, no shared planner code; `:easy`/`:medium`/`:hard`, default hard); the other opponents are pinned-playbook styles running a frozen copy of the bot's stack as of commit 7f3e2c7 (`Bindery.Ra2.Bot.Baseline`), a stationary benchmark; `live-<style>` runs a pinned style on the live stack. The arm plays Allied on odd seeds and Soviet on even seeds. Match limit 1200 s (a timeout is won on final asset value).
Opponents are split as well as maps. Held-out opponents (`ai-horde`, `ai-armor`, independent scripted styles) are never used for the selector's default playbook, bandit learning, distillation datasets or tuning; see "Held-out opponents" below. They are not untouched: their difficulty and the contested benchmark's handicaps were calibrated against the selector's win rate on all five maps, held-out maps included, so the selector's own held-out win rate is near one half partly by construction. Every other opponent is a training opponent: the selector's default playbook was chosen from a style-versus-style matrix against the pinned-playbook styles (training maps only), so win rates against training opponents are partly in-sample.
Benchmark: contested (opponent income ×1, Allied income ×1, Allied starting credits 20000, opponent starting credits default, combat noise ±0.25).
Labels in this run: `knob:SeenAttackForceRatio=1`, `oracle`.

Matches: 960

## Win rate (arm × split)

Distinct games drop repeats of an identical game (same arm faction, decision log and outcome on the same map): against a differently named opponent whose style had not diverged when the match ended, or on another seed that changed nothing; the distinct interval is the one to read. The arm plays Allied on odd seeds and Soviet on even seeds, and starts west on seeds 1-2, 5-6, ... and east on 3-4, 7-8, ...; the fixture is asymmetric, so the faction and side columns show the mix behind each rate.

| Arm | Split | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) | Distinct games | Distinct wins | 95% interval, distinct | As Allied | As Soviet | As west | As east | Eliminations won | Timeouts |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| selector | heldout | 354 | 126 | 0 | 480 | 0.738 | [0.696, 0.775] | 468 | 342 | [0.689, 0.769] | 164/240 | 190/240 | 176/240 | 178/240 | 302 | 108 |
| selector-oracle | heldout | 366 | 114 | 0 | 480 | 0.763 | [0.722, 0.798] | 452 | 338 | [0.706, 0.786] | 148/240 | 218/240 | 183/240 | 183/240 | 285 | 142 |

Benchmark check: the baseline `selector` scored 0.738 over 480 matches, outside the 30–70% band: the benchmark is saturated and win-rate comparisons carry little information (use `--benchmark contested`).

## Win rate by opponent style

| Arm | Split | ai-armor:easy | ai-armor:hard | ai-armor:medium | ai-horde:easy | ai-horde:hard | ai-horde:medium |
|---|---|---|---|---|---|---|---|
| selector | heldout | 61/80 | 47/80 | 62/80 | 65/80 | 53/80 | 66/80 |
| selector-oracle | heldout | 61/80 | 50/80 | 63/80 | 60/80 | 64/80 | 68/80 |

## Held-out opponents

Opponent split × map split. Held-out opponents never inform the selector's defaults, bandit learning (the arena abandons the bandit's episode instead of crediting it, as it does on held-out maps), distillation datasets or teacher runs, or tuning (the tuner refuses them); their difficulty was calibrated against the selector on all maps, so read the selector's own held-out rate as partly calibrated and other arms' rates relative to it.

| Arm | Opponents | Maps | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) |
|---|---|---|---|---|---|---|---|---|
| selector | heldout | heldout | 354 | 126 | 0 | 480 | 0.738 | [0.696, 0.775] |
| selector-oracle | heldout | heldout | 366 | 114 | 0 | 480 | 0.763 | [0.722, 0.798] |
| selector | heldout | all | 354 | 126 | 0 | 480 | 0.738 | [0.696, 0.775] |
| selector-oracle | heldout | all | 366 | 114 | 0 | 480 | 0.763 | [0.722, 0.798] |

## Perception bottleneck (belief − oracle)

Pairs share opponent, map and seed. Difference = arm − baseline, mean with a 95% seeded bootstrap interval; better/worse/tied count pairs by the metric's direction (n/d: no better direction, counted as higher/lower); p is the exact two-sided sign test over untied pairs, Holm-adjusted over the arm's metrics. `*` marks Holm p < 0.05.

### selector vs selector-oracle (478 pairs; 2 identical to another opponent's pair collapsed)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 478 | 0.762 | 0.736 | -0.025 [-0.063, 0.013] | 36 / 48 / 394 | 0.2299 | 0.6896 |
| final asset margin | 478 | 8206 | 7733 | -473 [-2102, 1226] | 208 / 270 / 0 | 0.0052 | 0.0365 * |
| value destroyed | 478 | 12639 | 12603 | -36.402 [-661, 546] | 257 / 194 / 27 | 0.0035 | 0.0277 * |
| value lost | 478 | 14512 | 14311 | -201 [-1598, 1089] | 243 / 197 / 38 | 0.0318 | 0.1591 |
| trade share | 478 | 0.618 | 0.625 | 0.007 [-0.016, 0.032] | 276 / 200 / 2 | 0.0006 | 0.0057 * |
| peak army value | 478 | 3724 | 3886 | 162 [35.146, 287] | 247 / 202 / 29 | 0.0377 | 0.1591 |
| units built | 478 | 60.82 | 57.841 | -2.979 [-5.444, -0.644] | 230 / 158 / 90 | 0.0003 | 0.0036 * |
| production idle fraction | 478 | 0.009 | 0.008 | -0 [-0.003, 0.002] | 264 / 187 / 27 | 0.0003 | 0.0037 * |
| average credits (float) | 478 | 12393 | 11615 | -778 [-933, -625] | 366 / 112 / 0 | <0.0001 | <0.0001 * |
| first attack s | 478 | 215 | 157 | -58.243 [-63.847, -52.253] | 96 / 382 / 0 (n/d) | <0.0001 | <0.0001 * |
| duration s | 478 | 730 | 715 | -15.002 [-42.226, 11.341] | 232 / 176 / 70 (n/d) | 0.0064 | 0.0384 * |
| activations /10 min | 478 | 1.945 | 2.174 | 0.229 [0.125, 0.333] | 204 / 270 / 4 (n/d) | 0.0028 | 0.0251 * |
| invalid plan rate | 478 | 0 | 0 | 0 [0, 0] | 0 / 0 / 478 | 1.0000 | 1.0000 |
| USD per match | 478 | 0 | 0 | 0 [0, 0] | 0 / 0 / 478 | 1.0000 | 1.0000 |

## Economy, production and combat (arm side, per-match averages)

Production idle: seconds with a factory, nothing queued and credits for the cheapest item, over match seconds. Trade efficiency: enemy value destroyed in combat / own value lost in combat (pooled over matches).

Time to first attack: first second a combat unit stood in a region holding an enemy structure (mean over matches where it happened; count in brackets).

| Arm | Duration s | Units built | Buildings built | Peak army value | Idle fraction | Avg credits | Final assets (arm / opp) | Trade efficiency | First attack s |
|---|---|---|---|---|---|---|---|---|---|
| selector | 713 | 57.7 | 9.4 | 3881 | 0.008 | 11595 | 27439 / 19732 | 0.88 (6044000/6842000) | 157 (480/480) |
| selector-oracle | 728 | 60.7 | 10.1 | 3719 | 0.009 | 12369 | 28398 / 20213 | 0.87 (6061600/6938400) | 215 (480/480) |

## Strategy layer (arm side)

Proposals, invalid plans and lateness are the arm's primary strategist's: rejected / proposals; seconds from a proposal's snapshot to its validation; late-discard count / proposals. Fallback and emergency proposals (immediate and always valid) are counted apart so they cannot flatter the strategist under test. Shadow columns are the shadow strategist's own lateness and would-be invalid plans. Churn: activations and posture flips per 10 game minutes.

Shadow agreement: shadow proposals naming the same playbook as the primary proposal for the same request / shadow proposals whose request got a primary proposal.

| Arm | Proposals | Invalid plans | Mean lateness s | Late-discarded | Fallback proposals | Failed requests | Shadow proposals | Shadow invalid | Shadow mean lateness s | Shadow agreement | Activations /10 min | Posture flips /10 min |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| selector | 18547 | 0/18547 (0.000) | 0.00 | 0/18547 (0.000) | 0 | 0 | 0 | n/a | n/a | n/a | 2.56 | 0.86 |
| selector-oracle | 18512 | 0/18512 (0.000) | 0.00 | 0/18512 (0.000) | 0 | 0 | 0 | n/a | n/a | n/a | 2.04 | 0.76 |

## Command gate and simulator rejections (arm side, totals)

- **selector**: 0 dropped by the gate (none); 2490 commands rejected by the simulator.
- **selector-oracle**: 0 dropped by the gate (none); 16407 commands rejected by the simulator.

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

