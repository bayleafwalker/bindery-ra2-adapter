# Bindery region sim arena report

Results are from the bindery region simulator with the approximate `bindery-sim-approx` rules, not retail RA2; they are directional.
The arm is a full `BotRuntime`. Opponents named `ai-*` are the independent scripted AI (`Bindery.Ra2.Bot.Sim.Opponents`, no shared planner code; `:easy`/`:medium`/`:hard`, default hard); the other opponents are pinned-playbook styles running a frozen copy of the bot's stack as of commit 7f3e2c7 (`Bindery.Ra2.Bot.Baseline`), a stationary benchmark; `live-<style>` runs a pinned style on the live stack. The arm plays Allied on odd seeds and Soviet on even seeds. Match limit 1200 s (a timeout is won on final asset value).
Opponents are split as well as maps. Held-out opponents (`ai-horde`, `ai-armor`, independent scripted styles) are never used for the selector's default playbook, bandit learning, distillation datasets or tuning; see "Held-out opponents" below. They are not untouched: their difficulty and the contested benchmark's handicaps were calibrated against the selector's win rate on all five maps, held-out maps included, so the selector's own held-out win rate is near one half partly by construction. Every other opponent is a training opponent: the selector's default playbook was chosen from a style-versus-style matrix against the pinned-playbook styles (training maps only), so win rates against training opponents are partly in-sample.
Benchmark: contested (opponent income ×1, Allied income ×1, Allied starting credits 20000, opponent starting credits default, combat noise ±0.25).
Labels in this run: `knob:SeenAttackForceRatio=1`, `oracle`.

Matches: 2880

## Win rate (arm × split)

Distinct games drop repeats of an identical game (same arm faction, decision log and outcome on the same map): against a differently named opponent whose style had not diverged when the match ended, or on another seed that changed nothing; the distinct interval is the one to read. The arm plays Allied on odd seeds and Soviet on even seeds, and starts west on seeds 1-2, 5-6, ... and east on 3-4, 7-8, ...; the fixture is asymmetric, so the faction and side columns show the mix behind each rate.

| Arm | Split | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) | Distinct games | Distinct wins | 95% interval, distinct | As Allied | As Soviet | As west | As east | Eliminations won | Timeouts |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| selector | heldout | 1039 | 401 | 0 | 1440 | 0.722 | [0.698, 0.744] | 1354 | 953 | [0.679, 0.728] | 475/720 | 564/720 | 515/720 | 524/720 | 895 | 303 |
| selector-oracle | heldout | 1091 | 349 | 0 | 1440 | 0.758 | [0.735, 0.779] | 1275 | 926 | [0.701, 0.750] | 435/720 | 656/720 | 544/720 | 547/720 | 847 | 430 |

Benchmark check: the baseline `selector` scored 0.722 over 1440 matches, outside the 30–70% band: the benchmark is saturated and win-rate comparisons carry little information (use `--benchmark contested`).

## Win rate by opponent style

| Arm | Split | ai-armor:easy | ai-armor:hard | ai-armor:medium | ai-horde:easy | ai-horde:hard | ai-horde:medium |
|---|---|---|---|---|---|---|---|
| selector | heldout | 189/240 | 139/240 | 177/240 | 192/240 | 149/240 | 193/240 |
| selector-oracle | heldout | 181/240 | 143/240 | 191/240 | 180/240 | 188/240 | 208/240 |

## Held-out opponents

Opponent split × map split. Held-out opponents never inform the selector's defaults, bandit learning (the arena abandons the bandit's episode instead of crediting it, as it does on held-out maps), distillation datasets or teacher runs, or tuning (the tuner refuses them); their difficulty was calibrated against the selector on all maps, so read the selector's own held-out rate as partly calibrated and other arms' rates relative to it.

| Arm | Opponents | Maps | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) |
|---|---|---|---|---|---|---|---|---|
| selector | heldout | heldout | 1039 | 401 | 0 | 1440 | 0.722 | [0.698, 0.744] |
| selector-oracle | heldout | heldout | 1091 | 349 | 0 | 1440 | 0.758 | [0.735, 0.779] |
| selector | heldout | all | 1039 | 401 | 0 | 1440 | 0.722 | [0.698, 0.744] |
| selector-oracle | heldout | all | 1091 | 349 | 0 | 1440 | 0.758 | [0.735, 0.779] |

## Perception bottleneck (belief − oracle)

Pairs share opponent, map and seed. Difference = arm − baseline, mean with a 95% seeded bootstrap interval; better/worse/tied count pairs by the metric's direction (n/d: no better direction, counted as higher/lower); p is the exact two-sided sign test over untied pairs, Holm-adjusted over the arm's metrics. `*` marks Holm p < 0.05.

### selector vs selector-oracle (1426 pairs; 14 identical to another opponent's pair collapsed)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 1426 | 0.755 | 0.719 | -0.036 [-0.058, -0.016] | 99 / 151 / 1176 | 0.0012 | 0.0036 * |
| final asset margin | 1426 | 7789 | 7129 | -660 [-1667, 372] | 615 / 811 / 0 | <0.0001 | <0.0001 * |
| value destroyed | 1426 | 12560 | 12435 | -124 [-452, 202] | 752 / 585 / 89 | <0.0001 | <0.0001 * |
| value lost | 1426 | 14742 | 14613 | -130 [-923, 669] | 729 / 593 / 104 | 0.0002 | 0.0008 * |
| trade share | 1426 | 0.614 | 0.617 | 0.003 [-0.011, 0.018] | 804 / 609 / 13 | <0.0001 | <0.0001 * |
| peak army value | 1426 | 3686 | 3907 | 221 [146, 293] | 762 / 596 / 68 | <0.0001 | <0.0001 * |
| units built | 1426 | 60.895 | 57.67 | -3.225 [-4.697, -1.849] | 688 / 475 / 263 | <0.0001 | <0.0001 * |
| production idle fraction | 1426 | 0.008 | 0.008 | -0 [-0.001, 0.001] | 802 / 545 / 79 | <0.0001 | <0.0001 * |
| average credits (float) | 1426 | 12399 | 11708 | -691 [-787, -599] | 1067 / 355 / 4 | <0.0001 | <0.0001 * |
| first attack s | 1426 | 215 | 159 | -56.299 [-59.734, -52.729] | 304 / 1122 / 0 (n/d) | <0.0001 | <0.0001 * |
| duration s | 1426 | 732 | 713 | -19.766 [-35.474, -4.565] | 695 / 529 / 202 (n/d) | <0.0001 | <0.0001 * |
| activations /10 min | 1426 | 1.944 | 2.146 | 0.202 [0.148, 0.26] | 596 / 810 / 20 (n/d) | <0.0001 | <0.0001 * |
| invalid plan rate | 1426 | 0 | 0 | 0 [0, 0] | 0 / 0 / 1426 | 1.0000 | 1.0000 |
| USD per match | 1426 | 0 | 0 | 0 [0, 0] | 0 / 0 / 1426 | 1.0000 | 1.0000 |

## Economy, production and combat (arm side, per-match averages)

Production idle: seconds with a factory, nothing queued and credits for the cheapest item, over match seconds. Trade efficiency: enemy value destroyed in combat / own value lost in combat (pooled over matches).

Time to first attack: first second a combat unit stood in a region holding an enemy structure (mean over matches where it happened; count in brackets).

| Arm | Duration s | Units built | Buildings built | Peak army value | Idle fraction | Avg credits | Final assets (arm / opp) | Trade efficiency | First attack s |
|---|---|---|---|---|---|---|---|---|---|
| selector | 709 | 57.5 | 9.3 | 3897 | 0.008 | 11660 | 27208 / 20130 | 0.86 (17874000/20845700) | 159 (1440/1440) |
| selector-oracle | 728 | 60.6 | 10.0 | 3675 | 0.008 | 12345 | 28098 / 20356 | 0.86 (18049800/21032300) | 215 (1440/1440) |

## Strategy layer (arm side)

Proposals, invalid plans and lateness are the arm's primary strategist's: rejected / proposals; seconds from a proposal's snapshot to its validation; late-discard count / proposals. Fallback and emergency proposals (immediate and always valid) are counted apart so they cannot flatter the strategist under test. Shadow columns are the shadow strategist's own lateness and would-be invalid plans. Churn: activations and posture flips per 10 game minutes.

Shadow agreement: shadow proposals naming the same playbook as the primary proposal for the same request / shadow proposals whose request got a primary proposal.

| Arm | Proposals | Invalid plans | Mean lateness s | Late-discarded | Fallback proposals | Failed requests | Shadow proposals | Shadow invalid | Shadow mean lateness s | Shadow agreement | Activations /10 min | Posture flips /10 min |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| selector | 55222 | 0/55222 (0.000) | 0.00 | 0/55222 (0.000) | 0 | 0 | 0 | n/a | n/a | n/a | 2.50 | 0.83 |
| selector-oracle | 55481 | 0/55481 (0.000) | 0.00 | 0/55481 (0.000) | 0 | 0 | 0 | n/a | n/a | n/a | 2.04 | 0.76 |

## Command gate and simulator rejections (arm side, totals)

- **selector**: 0 dropped by the gate (none); 7579 commands rejected by the simulator.
- **selector-oracle**: 0 dropped by the gate (none); 47677 commands rejected by the simulator.

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

