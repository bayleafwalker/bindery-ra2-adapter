# Bindery region sim arena report

Results are from the bindery region simulator with the approximate `bindery-sim-approx` rules, not retail RA2; they are directional.
The arm is a full `BotRuntime`. Opponents named `ai-*` are the independent scripted AI (`Bindery.Ra2.Bot.Sim.Opponents`, no shared planner code; `:easy`/`:medium`/`:hard`, default hard); the other opponents are pinned-playbook styles running a frozen copy of the bot's stack as of commit 7f3e2c7 (`Bindery.Ra2.Bot.Baseline`), a stationary benchmark; `live-<style>` runs a pinned style on the live stack. The arm plays Allied on odd seeds and Soviet on even seeds. Match limit 1200 s (a timeout is won on final asset value).
Opponents are split as well as maps. Held-out opponents (`ai-horde`, `ai-armor`, independent scripted styles) are never used for the selector's default playbook, bandit learning, distillation datasets or tuning; see "Held-out opponents" below. They are not untouched: their difficulty and the contested benchmark's handicaps were calibrated against the selector's win rate on all five maps, held-out maps included, so the selector's own held-out win rate is near one half partly by construction. Every other opponent is a training opponent: the selector's default playbook was chosen from a style-versus-style matrix against the pinned-playbook styles (training maps only), so win rates against training opponents are partly in-sample.
Benchmark: contested (opponent income ×1, Allied income ×1, Allied starting credits 20000, opponent starting credits default, combat noise ±0.25).
Labels in this run: `llm-openai:gpt-6-luna`, `vocabulary:Parameters`.

Matches: 40

## Win rate (arm × split)

Distinct games drop repeats of an identical game (same arm faction, decision log and outcome on the same map): against a differently named opponent whose style had not diverged when the match ended, or on another seed that changed nothing; the distinct interval is the one to read. The arm plays Allied on odd seeds and Soviet on even seeds, and starts west on seeds 1-2, 5-6, ... and east on 3-4, 7-8, ...; the fixture is asymmetric, so the faction and side columns show the mix behind each rate.

| Arm | Split | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) | Distinct games | Distinct wins | 95% interval, distinct | As Allied | As Soviet | As west | As east | Eliminations won | Timeouts |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| llm-t1 | heldout | 20 | 0 | 0 | 20 | 1.000 | [0.839, 1.000] | 20 | 20 | [0.839, 1.000] | 0/0 | 20/20 | 10/10 | 10/10 | 20 | 0 |
| selector | heldout | 17 | 3 | 0 | 20 | 0.850 | [0.640, 0.948] | 20 | 17 | [0.640, 0.948] | 0/0 | 17/20 | 8/10 | 9/10 | 17 | 0 |

Warning: the faction mix is unbalanced for llm-t1/heldout (0 Allied, 20 Soviet), selector/heldout (0 Allied, 20 Soviet) (an odd `--seeds` gives the arm Allied more often); a win rate over it mixes faction strength into the result. Use an even `--seeds`.

Benchmark check: the baseline `selector` scored 0.850 over 20 matches, outside the 30–70% band: the benchmark is saturated and win-rate comparisons carry little information (use `--benchmark contested`).

## Win rate by opponent style

| Arm | Split | ai-armor:easy | ai-armor:hard | ai-armor:medium | ai-horde:easy | ai-horde:hard |
|---|---|---|---|---|---|---|
| llm-t1 | heldout | 4/4 | 4/4 | 4/4 | 4/4 | 4/4 |
| selector | heldout | 4/4 | 3/4 | 4/4 | 4/4 | 2/4 |

## Held-out opponents

Opponent split × map split. Held-out opponents never inform the selector's defaults, bandit learning (the arena abandons the bandit's episode instead of crediting it, as it does on held-out maps), distillation datasets or teacher runs, or tuning (the tuner refuses them); their difficulty was calibrated against the selector on all maps, so read the selector's own held-out rate as partly calibrated and other arms' rates relative to it.

| Arm | Opponents | Maps | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) |
|---|---|---|---|---|---|---|---|---|
| llm-t1 | heldout | heldout | 20 | 0 | 0 | 20 | 1.000 | [0.839, 1.000] |
| selector | heldout | heldout | 17 | 3 | 0 | 20 | 0.850 | [0.640, 0.948] |
| llm-t1 | heldout | all | 20 | 0 | 0 | 20 | 1.000 | [0.839, 1.000] |
| selector | heldout | all | 17 | 3 | 0 | 20 | 0.850 | [0.640, 0.948] |

### Paired differences vs selector, held-out opponents only

Pairs share opponent, map and seed. Difference = arm − baseline, mean with a 95% seeded bootstrap interval; better/worse/tied count pairs by the metric's direction (n/d: no better direction, counted as higher/lower); p is the exact two-sided sign test over untied pairs, Holm-adjusted over the arm's metrics. `*` marks Holm p < 0.05.

### llm-t1 vs selector (20 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 20 | 0.85 | 1 | 0.15 [0, 0.3] | 3 / 0 / 17 | 0.2500 | 1.0000 |
| final asset margin | 20 | -3063 | 13800 | 16863 [9718, 25688] | 19 / 1 / 0 | <0.0001 | 0.0005 * |
| value destroyed | 20 | 11305 | 23930 | 12625 [11465, 13865] | 20 / 0 / 0 | <0.0001 | <0.0001 * |
| value lost | 20 | 6590 | 1835 | -4755 [-10740, 610] | 8 / 12 / 0 | 0.5034 | 1.0000 |
| trade share | 20 | 0.812 | 0.931 | 0.119 [0.018, 0.239] | 15 / 5 / 0 | 0.0414 | 0.2483 |
| peak army value | 20 | 2720 | 6350 | 3630 [3330, 3875] | 20 / 0 / 0 | <0.0001 | <0.0001 * |
| units built | 20 | 42.9 | 80.55 | 37.65 [29.35, 44.6] | 18 / 1 / 1 | <0.0001 | 0.0008 * |
| production idle fraction | 20 | 0.008 | 0.005 | -0.003 [-0.004, -0.002] | 17 / 3 / 0 | 0.0026 | 0.0258 * |
| average credits (float) | 20 | 6702 | 7024 | 323 [-64.821, 810] | 9 / 11 / 0 | 0.8238 | 1.0000 |
| first attack s | 20 | 122 | 202 | 80.95 [20.4, 161] | 9 / 0 / 11 (n/d) | 0.0039 | 0.0313 * |
| duration s | 20 | 456 | 621 | 165 [48.5, 265] | 17 / 3 / 0 (n/d) | 0.0026 | 0.0258 * |
| activations /10 min | 20 | 1.957 | 2.178 | 0.221 [-0.155, 0.545] | 16 / 4 / 0 (n/d) | 0.0118 | 0.0827 |
| invalid plan rate | 20 | 0 | 0 | 0 [0, 0] | 0 / 0 / 20 | 1.0000 | 1.0000 |
| USD per match | 20 | 0 | 0 | 0 [0, 0] | 0 / 0 / 20 | 1.0000 | 1.0000 |

## Paired differences vs selector

Pairs share opponent, map and seed. Difference = arm − baseline, mean with a 95% seeded bootstrap interval; better/worse/tied count pairs by the metric's direction (n/d: no better direction, counted as higher/lower); p is the exact two-sided sign test over untied pairs, Holm-adjusted over the arm's metrics. `*` marks Holm p < 0.05.

### llm-t1 vs selector (20 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 20 | 0.85 | 1 | 0.15 [0, 0.3] | 3 / 0 / 17 | 0.2500 | 1.0000 |
| final asset margin | 20 | -3063 | 13800 | 16863 [9718, 25688] | 19 / 1 / 0 | <0.0001 | 0.0005 * |
| value destroyed | 20 | 11305 | 23930 | 12625 [11465, 13865] | 20 / 0 / 0 | <0.0001 | <0.0001 * |
| value lost | 20 | 6590 | 1835 | -4755 [-10740, 610] | 8 / 12 / 0 | 0.5034 | 1.0000 |
| trade share | 20 | 0.812 | 0.931 | 0.119 [0.018, 0.239] | 15 / 5 / 0 | 0.0414 | 0.2483 |
| peak army value | 20 | 2720 | 6350 | 3630 [3330, 3875] | 20 / 0 / 0 | <0.0001 | <0.0001 * |
| units built | 20 | 42.9 | 80.55 | 37.65 [29.35, 44.6] | 18 / 1 / 1 | <0.0001 | 0.0008 * |
| production idle fraction | 20 | 0.008 | 0.005 | -0.003 [-0.004, -0.002] | 17 / 3 / 0 | 0.0026 | 0.0258 * |
| average credits (float) | 20 | 6702 | 7024 | 323 [-64.821, 810] | 9 / 11 / 0 | 0.8238 | 1.0000 |
| first attack s | 20 | 122 | 202 | 80.95 [20.4, 161] | 9 / 0 / 11 (n/d) | 0.0039 | 0.0313 * |
| duration s | 20 | 456 | 621 | 165 [48.5, 265] | 17 / 3 / 0 (n/d) | 0.0026 | 0.0258 * |
| activations /10 min | 20 | 1.957 | 2.178 | 0.221 [-0.155, 0.545] | 16 / 4 / 0 (n/d) | 0.0118 | 0.0827 |
| invalid plan rate | 20 | 0 | 0 | 0 [0, 0] | 0 / 0 / 20 | 1.0000 | 1.0000 |
| USD per match | 20 | 0 | 0 | 0 [0, 0] | 0 / 0 / 20 | 1.0000 | 1.0000 |

## Economy, production and combat (arm side, per-match averages)

Production idle: seconds with a factory, nothing queued and credits for the cheapest item, over match seconds. Trade efficiency: enemy value destroyed in combat / own value lost in combat (pooled over matches).

Time to first attack: first second a combat unit stood in a region holding an enemy structure (mean over matches where it happened; count in brackets).

| Arm | Duration s | Units built | Buildings built | Peak army value | Idle fraction | Avg credits | Final assets (arm / opp) | Trade efficiency | First attack s |
|---|---|---|---|---|---|---|---|---|---|
| llm-t1 | 621 | 80.6 | 6.3 | 6350 | 0.005 | 7024 | 40590 / 26790 | 13.04 (478600/36700) | 202 (20/20) |
| selector | 456 | 42.9 | 5.3 | 2720 | 0.008 | 6702 | 16840 / 19903 | 1.72 (226100/131800) | 122 (20/20) |

## Strategy layer (arm side)

Proposals, invalid plans and lateness are the arm's primary strategist's: rejected / proposals; seconds from a proposal's snapshot to its validation; late-discard count / proposals. Fallback and emergency proposals (immediate and always valid) are counted apart so they cannot flatter the strategist under test. Shadow columns are the shadow strategist's own lateness and would-be invalid plans. Churn: activations and posture flips per 10 game minutes.

Shadow agreement: shadow proposals naming the same playbook as the primary proposal for the same request / shadow proposals whose request got a primary proposal.

| Arm | Proposals | Invalid plans | Mean lateness s | Late-discarded | Fallback proposals | Failed requests | Shadow proposals | Shadow invalid | Shadow mean lateness s | Shadow agreement | Activations /10 min | Posture flips /10 min |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| llm-t1 | 614 | 0/614 (0.000) | 10.02 | 21/614 (0.034) | 20 | 7 | 0 | n/a | n/a | n/a | 2.17 | 0.97 |
| selector | 487 | 0/487 (0.000) | 0.00 | 0/487 (0.000) | 0 | 0 | 0 | n/a | n/a | n/a | 2.24 | 0.39 |

## Command gate and simulator rejections (arm side, totals)

- **llm-t1**: 0 dropped by the gate (none); 27 commands rejected by the simulator.
- **selector**: 0 dropped by the gate (none); 20 commands rejected by the simulator.

## Inference cost (arm side)

- **llm-t1** (gpt-6-luna): 246009 in / 25530 out tokens and $0.0000 per match, of which $0.0000 on failed requests; 615 requests on unpriced models are not in the cost.
- **selector** (no model): 0 in / 0 out tokens and $0.0000 per match, of which $0.0000 on failed requests.

## Hidden-information leakage

- **llm-t1**: 0 validator `fog.*` rejections.
- **selector**: 0 validator `fog.*` rejections.

Probe: two lockstep simulations, hidden state of one perturbed (`SimLeakageProbe`: enemy credits and queue, wounded hidden enemies, a hidden unit in an unseen region, one announced there through the event path, and one just across a border inside the arm's weapon reach), strategist-context hash compared on every following frame until the window closes or the objects the arm can see first differ. A differing frame is one where the context changed while everything visible was still identical. Fog-violation frames are arm frames, over the whole run of both simulations, that carried an enemy object or event from a region the arm did not see: a per-frame check that finds leaks the perturbation does not exercise.

| Arm | Map | Seed | Perturbed at s | Lockstep before | Compared s | Differing frames | Fog-violation frames | Note |
|---|---|---|---|---|---|---|---|---|
| llm-t1 | open-steppe | 1 | 90 | yes | 16 | 243/243 | 0 | stopped at 106 s: the arm saw a legitimate difference |
| llm-t1 | open-steppe | 1 | 240 | no | 0 | 0/0 | 0 | stopped at 240 s: the arm saw a legitimate difference |
| selector | open-steppe | 1 | 90 | yes | 45 | 0/671 | 0 | stopped at 135 s: the arm saw a legitimate difference |
| selector | open-steppe | 1 | 240 | yes | 0 | 0/1 | 0 | stopped at 240 s: the arm saw a legitimate difference |

