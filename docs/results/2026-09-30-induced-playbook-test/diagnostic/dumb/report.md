# Bindery region sim arena report

Results are from the bindery region simulator with the approximate `bindery-sim-approx` rules, not retail RA2; they are directional.
The arm is a full `BotRuntime`. Opponents named `ai-*` are the independent scripted AI (`Bindery.Ra2.Bot.Sim.Opponents`, no shared planner code; `:easy`/`:medium`/`:hard`, default hard); the other opponents are pinned-playbook styles running a frozen copy of the bot's stack as of commit 7f3e2c7 (`Bindery.Ra2.Bot.Baseline`), a stationary benchmark; `live-<style>` runs a pinned style on the live stack. The arm plays Allied on odd seeds and Soviet on even seeds. Match limit 1200 s (a timeout is won on final asset value).
Opponents are split as well as maps. Held-out opponents (`ai-horde`, `ai-armor`, independent scripted styles) are never used for the selector's default playbook, bandit learning, distillation datasets or tuning; see "Held-out opponents" below. They are not untouched: their difficulty and the contested benchmark's handicaps were calibrated against the selector's win rate on all five maps, held-out maps included, so the selector's own held-out win rate is near one half partly by construction. Every other opponent is a training opponent: the selector's default playbook was chosen from a style-versus-style matrix against the pinned-playbook styles (training maps only), so win rates against training opponents are partly in-sample.
Benchmark: standard (opponent income ×1, Allied income ×1, Allied starting credits default, opponent starting credits default, combat noise ±0).
Labels in this run: `pinned:generic-expand`, `pinned:induced-generic-expand-soviet-8f797e00`.

Matches: 162

## Win rate (arm × split)

Distinct games drop repeats of an identical game (same arm faction, decision log and outcome on the same map): against a differently named opponent whose style had not diverged when the match ended, or on another seed that changed nothing; the distinct interval is the one to read. The arm plays Allied on odd seeds and Soviet on even seeds, and starts west on seeds 1-2, 5-6, ... and east on 3-4, 7-8, ...; the fixture is asymmetric, so the faction and side columns show the mix behind each rate.

| Arm | Split | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) | Distinct games | Distinct wins | 95% interval, distinct | As Allied | As Soviet | As west | As east | Eliminations won | Timeouts | LLM delivery |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| pinned:generic-expand | training | 40 | 14 | 0 | 54 | 0.741 | [0.611, 0.839] | 54 | 40 | [0.611, 0.839] | 0/0 | 40/54 | 20/27 | 20/27 | 37 | 9 | n/a |
| pinned:induced-generic-expand-soviet-8f797e00 | training | 39 | 15 | 0 | 54 | 0.722 | [0.591, 0.824] | 54 | 39 | [0.591, 0.824] | 0/0 | 39/54 | 19/27 | 20/27 | 35 | 11 | n/a |
| selector | training | 54 | 0 | 0 | 54 | 1.000 | [0.934, 1.000] | 30 | 30 | [0.886, 1.000] | 0/0 | 54/54 | 27/27 | 27/27 | 54 | 0 | n/a |

Warning: the faction mix is unbalanced for pinned:generic-expand/training (0 Allied, 54 Soviet), pinned:induced-generic-expand-soviet-8f797e00/training (0 Allied, 54 Soviet), selector/training (0 Allied, 54 Soviet) (an odd `--seeds` gives the arm Allied more often); a win rate over it mixes faction strength into the result. Use an even `--seeds`.

Benchmark check: the baseline `selector` scored 1.000 over 54 matches, outside the 30–70% band: the benchmark is saturated and win-rate comparisons carry little information (use `--benchmark contested`).

## Win rate by opponent style

| Arm | Split | ai-air:easy | ai-balanced:easy | ai-rush:easy | ai-turtle:easy | balanced | harass | rush | tech | turtle |
|---|---|---|---|---|---|---|---|---|---|---|
| pinned:generic-expand | training | 6/6 | 6/6 | 4/6 | 6/6 | 2/6 | 4/6 | 5/6 | 1/6 | 6/6 |
| pinned:induced-generic-expand-soviet-8f797e00 | training | 6/6 | 6/6 | 4/6 | 6/6 | 1/6 | 4/6 | 5/6 | 1/6 | 6/6 |
| selector | training | 6/6 | 6/6 | 6/6 | 6/6 | 6/6 | 6/6 | 6/6 | 6/6 | 6/6 |

Identical games (same arm decision log and outcome against differently named opponents; counted once in the distinct columns and paired tables):

- selector: balanced = harass = rush = tech = turtle on 6 map × seed cells

## Held-out opponents

No held-out opponent in this run (`ai-horde`, `ai-armor`; `--opponents heldout` adds them). Every win rate above is against training opponents.

## Paired differences vs selector

Pairs share opponent, map and seed. Difference = arm − baseline, mean with a 95% seeded bootstrap interval; better/worse/tied count pairs by the metric's direction (n/d: no better direction, counted as higher/lower); p is the exact two-sided sign test over untied pairs, Holm-adjusted over the arm's metrics. `*` marks Holm p < 0.05.

### pinned:generic-expand vs selector (54 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 54 | 1 | 0.741 | -0.259 [-0.37, -0.148] | 0 / 14 / 40 | 0.0001 | 0.0009 * |
| final asset margin | 54 | 8928 | 11539 | 2611 [-3774, 8202] | 37 / 17 / 0 | 0.0091 | 0.0454 * |
| value destroyed | 54 | 7922 | 19522 | 11600 [10315, 12946] | 54 / 0 / 0 | <0.0001 | <0.0001 * |
| value lost | 54 | 96.296 | 8319 | 8222 [5276, 11393] | 0 / 53 / 1 | <0.0001 | <0.0001 * |
| trade share | 54 | 0.99 | 0.783 | -0.207 [-0.279, -0.14] | 6 / 47 / 1 | <0.0001 | <0.0001 * |
| peak army value | 54 | 2889 | 7046 | 4157 [3857, 4487] | 54 / 0 / 0 | <0.0001 | <0.0001 * |
| units built | 54 | 30.852 | 84 | 53.148 [49.444, 57.556] | 54 / 0 / 0 | <0.0001 | <0.0001 * |
| production idle fraction | 54 | 0.01 | 0.03 | 0.02 [0.005, 0.039] | 38 / 16 / 0 | 0.0038 | 0.0230 * |
| average credits (float) | 54 | 7103 | 5768 | -1335 [-1808, -872] | 36 / 18 / 0 | 0.0198 | 0.0793 |
| first attack s | 54 | 121 | 121 | 0 [0, 0] | 0 / 0 / 54 (n/d) | 1.0000 | 1.0000 |
| duration s | 54 | 290 | 777 | 487 [422, 556] | 54 / 0 / 0 (n/d) | <0.0001 | <0.0001 * |
| activations /10 min | 54 | 2.092 | 1.144 | -0.948 [-1.12, -0.757] | 8 / 46 / 0 (n/d) | <0.0001 | <0.0001 * |
| invalid plan rate | 54 | 0 | 0 | 0 [0, 0] | 0 / 0 / 54 | 1.0000 | 1.0000 |
| USD per match | 54 | 0 | 0 | 0 [0, 0] | 0 / 0 / 54 | 1.0000 | 1.0000 |

### pinned:induced-generic-expand-soviet-8f797e00 vs selector (54 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 54 | 1 | 0.722 | -0.278 [-0.407, -0.167] | 0 / 15 / 39 | <0.0001 | 0.0004 * |
| final asset margin | 54 | 8928 | 10254 | 1326 [-4457, 6656] | 36 / 18 / 0 | 0.0198 | 0.0992 |
| value destroyed | 54 | 7922 | 18978 | 11056 [9739, 12387] | 54 / 0 / 0 | <0.0001 | <0.0001 * |
| value lost | 54 | 96.296 | 8757 | 8661 [5733, 11743] | 0 / 54 / 0 | <0.0001 | <0.0001 * |
| trade share | 54 | 0.99 | 0.766 | -0.223 [-0.294, -0.152] | 6 / 48 / 0 | <0.0001 | <0.0001 * |
| peak army value | 54 | 2889 | 7152 | 4263 [3911, 4694] | 54 / 0 / 0 | <0.0001 | <0.0001 * |
| units built | 54 | 30.852 | 85.37 | 54.519 [50.593, 59.093] | 54 / 0 / 0 | <0.0001 | <0.0001 * |
| production idle fraction | 54 | 0.01 | 0.034 | 0.023 [0.008, 0.043] | 35 / 19 / 0 | 0.0402 | 0.1609 |
| average credits (float) | 54 | 7103 | 5639 | -1465 [-1926, -1031] | 37 / 17 / 0 | 0.0091 | 0.0544 |
| first attack s | 54 | 121 | 121 | 0 [0, 0] | 0 / 0 / 54 (n/d) | 1.0000 | 1.0000 |
| duration s | 54 | 290 | 795 | 506 [437, 579] | 54 / 0 / 0 (n/d) | <0.0001 | <0.0001 * |
| activations /10 min | 54 | 2.092 | 1.128 | -0.964 [-1.136, -0.773] | 8 / 46 / 0 (n/d) | <0.0001 | <0.0001 * |
| invalid plan rate | 54 | 0 | 0 | 0 [0, 0] | 0 / 0 / 54 | 1.0000 | 1.0000 |
| USD per match | 54 | 0 | 0 | 0 [0, 0] | 0 / 0 / 54 | 1.0000 | 1.0000 |

## Economy, production and combat (arm side, per-match averages)

Production idle: seconds with a factory, nothing queued and credits for the cheapest item, over match seconds. Trade efficiency: enemy value destroyed in combat / own value lost in combat (pooled over matches).

Time to first attack: first second a combat unit stood in a region holding an enemy structure (mean over matches where it happened; count in brackets).

| Arm | Duration s | Units built | Buildings built | Peak army value | Idle fraction | Avg credits | Final assets (arm / opp) | Trade efficiency | First attack s |
|---|---|---|---|---|---|---|---|---|---|
| pinned:generic-expand | 777 | 84.0 | 7.3 | 7046 | 0.030 | 5768 | 25783 / 14244 | 2.35 (1054200/449200) | 121 (54/54) |
| pinned:induced-generic-expand-soviet-8f797e00 | 795 | 85.4 | 7.3 | 7152 | 0.034 | 5639 | 25213 / 14959 | 2.17 (1024800/472900) | 121 (54/54) |
| selector | 290 | 30.9 | 3.2 | 2889 | 0.010 | 7103 | 17998 / 9070 | 82.27 (427800/5200) | 121 (54/54) |

## Strategy layer (arm side)

Proposals, invalid plans and lateness are the arm's primary strategist's: rejected / proposals; seconds from a proposal's snapshot to its validation; late-discard count / proposals. Fallback and emergency proposals (immediate and always valid) are counted apart so they cannot flatter the strategist under test. Shadow columns are the shadow strategist's own lateness and would-be invalid plans. Churn: activations and posture flips per 10 game minutes.

Shadow agreement: shadow proposals naming the same playbook as the primary proposal for the same request / shadow proposals whose request got a primary proposal.

| Arm | Proposals | Invalid plans | Mean lateness s | Late-discarded | Fallback proposals | Failed requests | Shadow proposals | Shadow invalid | Shadow mean lateness s | Shadow agreement | Activations /10 min | Posture flips /10 min |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| pinned:generic-expand | 2250 | 0/2250 (0.000) | 0.00 | 0/2250 (0.000) | 25 | 0 | 0 | n/a | n/a | n/a | 1.16 | 0.17 |
| pinned:induced-generic-expand-soviet-8f797e00 | 2310 | 0/2310 (0.000) | 0.00 | 0/2310 (0.000) | 25 | 0 | 0 | n/a | n/a | n/a | 1.13 | 0.17 |
| selector | 802 | 0/802 (0.000) | 0.00 | 0/802 (0.000) | 0 | 0 | 0 | n/a | n/a | n/a | 2.07 | 0.00 |

## Command gate and simulator rejections (arm side, totals)

- **pinned:generic-expand**: 0 dropped by the gate (none); 201 commands rejected by the simulator.
- **pinned:induced-generic-expand-soviet-8f797e00**: 0 dropped by the gate (none); 265 commands rejected by the simulator.
- **selector**: 0 dropped by the gate (none); 0 commands rejected by the simulator.

## Inference cost (arm side)

- **pinned:generic-expand** (no model): 0 in / 0 out tokens and $0.0000 per match, of which $0.0000 on failed requests.
- **pinned:induced-generic-expand-soviet-8f797e00** (no model): 0 in / 0 out tokens and $0.0000 per match, of which $0.0000 on failed requests.
- **selector** (no model): 0 in / 0 out tokens and $0.0000 per match, of which $0.0000 on failed requests.

## Hidden-information leakage

- **pinned:generic-expand**: 0 validator `fog.*` rejections.
- **pinned:induced-generic-expand-soviet-8f797e00**: 0 validator `fog.*` rejections.
- **selector**: 0 validator `fog.*` rejections.

Probe: two lockstep simulations, hidden state of one perturbed (`SimLeakageProbe`: enemy credits and queue, wounded hidden enemies, a hidden unit in an unseen region, one announced there through the event path, and one just across a border inside the arm's weapon reach), strategist-context hash compared on every following frame until the window closes or the objects the arm can see first differ. A differing frame is one where the context changed while everything visible was still identical. Fog-violation frames are arm frames, over the whole run of both simulations, that carried an enemy object or event from a region the arm did not see: a per-frame check that finds leaks the perturbation does not exercise.

| Arm | Map | Seed | Perturbed at s | Lockstep before | Compared s | Differing frames | Fog-violation frames | Note |
|---|---|---|---|---|---|---|---|---|
| pinned:induced-generic-expand-soviet-8f797e00 | twin-valley | 1 | 90 | yes | 44 | 0/659 | 0 | stopped at 134 s: the arm saw a legitimate difference |
| pinned:induced-generic-expand-soviet-8f797e00 | twin-valley | 1 | 240 | yes | 0 | 0/4 | 0 | stopped at 240 s: the arm saw a legitimate difference |
| pinned:generic-expand | twin-valley | 1 | 90 | yes | 44 | 0/659 | 0 | stopped at 134 s: the arm saw a legitimate difference |
| pinned:generic-expand | twin-valley | 1 | 240 | yes | 0 | 0/4 | 0 | stopped at 240 s: the arm saw a legitimate difference |
| selector | twin-valley | 1 | 90 | yes | 44 | 0/659 | 0 | stopped at 134 s: the arm saw a legitimate difference |
| selector | twin-valley | 1 | 240 | yes | 0 | 0/3 | 0 | stopped at 240 s: the arm saw a legitimate difference |

