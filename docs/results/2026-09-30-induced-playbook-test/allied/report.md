# Bindery region sim arena report

Results are from the bindery region simulator with the approximate `bindery-sim-approx` rules, not retail RA2; they are directional.
The arm is a full `BotRuntime`. Opponents named `ai-*` are the independent scripted AI (`Bindery.Ra2.Bot.Sim.Opponents`, no shared planner code; `:easy`/`:medium`/`:hard`, default hard); the other opponents are pinned-playbook styles running a frozen copy of the bot's stack as of commit 7f3e2c7 (`Bindery.Ra2.Bot.Baseline`), a stationary benchmark; `live-<style>` runs a pinned style on the live stack. The arm plays Allied on odd seeds and Soviet on even seeds. Match limit 1200 s (a timeout is won on final asset value).
Opponents are split as well as maps. Held-out opponents (`ai-horde`, `ai-armor`, independent scripted styles) are never used for the selector's default playbook, bandit learning, distillation datasets or tuning; see "Held-out opponents" below. They are not untouched: their difficulty and the contested benchmark's handicaps were calibrated against the selector's win rate on all five maps, held-out maps included, so the selector's own held-out win rate is near one half partly by construction. Every other opponent is a training opponent: the selector's default playbook was chosen from a style-versus-style matrix against the pinned-playbook styles (training maps only), so win rates against training opponents are partly in-sample.
Benchmark: standard (opponent income ×1, Allied income ×1, Allied starting credits default, opponent starting credits default, combat noise ±0).
Labels in this run: `pinned:allied-boom`, `pinned:induced-allied-boom-allied-553cbefc`.

Matches: 60

## Win rate (arm × split)

Distinct games drop repeats of an identical game (same arm faction, decision log and outcome on the same map): against a differently named opponent whose style had not diverged when the match ended, or on another seed that changed nothing; the distinct interval is the one to read. The arm plays Allied on odd seeds and Soviet on even seeds, and starts west on seeds 1-2, 5-6, ... and east on 3-4, 7-8, ...; the fixture is asymmetric, so the faction and side columns show the mix behind each rate.

| Arm | Split | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) | Distinct games | Distinct wins | 95% interval, distinct | As Allied | As Soviet | As west | As east | Eliminations won | Timeouts | LLM delivery |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| pinned:allied-boom | heldout | 11 | 9 | 0 | 20 | 0.550 | [0.342, 0.742] | 20 | 11 | [0.342, 0.742] | 11/20 | 0/0 | 6/10 | 5/10 | 9 | 5 | n/a |
| pinned:induced-allied-boom-allied-553cbefc | heldout | 10 | 10 | 0 | 20 | 0.500 | [0.299, 0.701] | 20 | 10 | [0.299, 0.701] | 10/20 | 0/0 | 5/10 | 5/10 | 10 | 3 | n/a |
| selector | heldout | 10 | 10 | 0 | 20 | 0.500 | [0.299, 0.701] | 20 | 10 | [0.299, 0.701] | 10/20 | 0/0 | 5/10 | 5/10 | 9 | 3 | n/a |

Warning: the faction mix is unbalanced for pinned:allied-boom/heldout (20 Allied, 0 Soviet), pinned:induced-allied-boom-allied-553cbefc/heldout (20 Allied, 0 Soviet), selector/heldout (20 Allied, 0 Soviet) (an odd `--seeds` gives the arm Allied more often); a win rate over it mixes faction strength into the result. Use an even `--seeds`.

Benchmark check: the baseline `selector` scored 0.500 over 20 matches, inside the 30–70% band, so win-rate differences between arms can show.

## Win rate by opponent style

| Arm | Split | ai-armor:easy | ai-armor:hard | ai-armor:medium | ai-horde:easy | ai-horde:hard |
|---|---|---|---|---|---|---|
| pinned:allied-boom | heldout | 3/4 | 2/4 | 2/4 | 2/4 | 2/4 |
| pinned:induced-allied-boom-allied-553cbefc | heldout | 2/4 | 2/4 | 2/4 | 2/4 | 2/4 |
| selector | heldout | 2/4 | 2/4 | 2/4 | 2/4 | 2/4 |

## Held-out opponents

Opponent split × map split. Held-out opponents never inform the selector's defaults, bandit learning (the arena abandons the bandit's episode instead of crediting it, as it does on held-out maps), distillation datasets or teacher runs, or tuning (the tuner refuses them); their difficulty was calibrated against the selector on all maps, so read the selector's own held-out rate as partly calibrated and other arms' rates relative to it.

| Arm | Opponents | Maps | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) |
|---|---|---|---|---|---|---|---|---|
| pinned:allied-boom | heldout | heldout | 11 | 9 | 0 | 20 | 0.550 | [0.342, 0.742] |
| pinned:induced-allied-boom-allied-553cbefc | heldout | heldout | 10 | 10 | 0 | 20 | 0.500 | [0.299, 0.701] |
| selector | heldout | heldout | 10 | 10 | 0 | 20 | 0.500 | [0.299, 0.701] |
| pinned:allied-boom | heldout | all | 11 | 9 | 0 | 20 | 0.550 | [0.342, 0.742] |
| pinned:induced-allied-boom-allied-553cbefc | heldout | all | 10 | 10 | 0 | 20 | 0.500 | [0.299, 0.701] |
| selector | heldout | all | 10 | 10 | 0 | 20 | 0.500 | [0.299, 0.701] |

### Paired differences vs selector, held-out opponents only

Pairs share opponent, map and seed. Difference = arm − baseline, mean with a 95% seeded bootstrap interval; better/worse/tied count pairs by the metric's direction (n/d: no better direction, counted as higher/lower); p is the exact two-sided sign test over untied pairs, Holm-adjusted over the arm's metrics. `*` marks Holm p < 0.05.

### pinned:allied-boom vs selector (20 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 20 | 0.5 | 0.55 | 0.05 [0, 0.15] | 1 / 0 / 19 | 1.0000 | 1.0000 |
| final asset margin | 20 | 5050 | 9618 | 4568 [-450, 9818] | 14 / 4 / 2 | 0.0309 | 0.4015 |
| value destroyed | 20 | 12665 | 12155 | -510 [-1880, 630] | 10 / 5 / 5 | 0.3018 | 1.0000 |
| value lost | 20 | 23740 | 23095 | -645 [-5000, 3130] | 5 / 12 / 3 | 0.1435 | 1.0000 |
| trade share | 20 | 0.441 | 0.442 | 0.002 [-0.04, 0.048] | 8 / 10 / 2 | 0.8145 | 1.0000 |
| peak army value | 20 | 4950 | 4045 | -905 [-1520, -50] | 2 / 18 / 0 | 0.0004 | 0.0056 * |
| units built | 20 | 63.35 | 68.4 | 5.05 [1.6, 8.7] | 12 / 8 / 0 | 0.5034 | 1.0000 |
| production idle fraction | 20 | 0.006 | 0.004 | -0.002 [-0.006, -0] | 13 / 5 / 2 | 0.0963 | 1.0000 |
| average credits (float) | 20 | 7382 | 7764 | 382 [-163, 948] | 8 / 12 / 0 | 0.5034 | 1.0000 |
| first attack s | 20 | 192 | 192 | 0 [0, 0] | 0 / 0 / 20 (n/d) | 1.0000 | 1.0000 |
| duration s | 20 | 884 | 920 | 36 [-3.3, 72.75] | 13 / 5 / 2 (n/d) | 0.0963 | 1.0000 |
| activations /10 min | 20 | 2.347 | 2.117 | -0.23 [-0.572, 0.089] | 5 / 14 / 1 (n/d) | 0.0636 | 0.7628 |
| invalid plan rate | 20 | 0 | 0 | 0 [0, 0] | 0 / 0 / 20 | 1.0000 | 1.0000 |
| USD per match | 20 | 0 | 0 | 0 [0, 0] | 0 / 0 / 20 | 1.0000 | 1.0000 |

### pinned:induced-allied-boom-allied-553cbefc vs selector (20 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 20 | 0.5 | 0.5 | 0 [0, 0] | 0 / 0 / 20 | 1.0000 | 1.0000 |
| final asset margin | 20 | 5050 | 8430 | 3380 [-1370, 8453] | 13 / 7 / 0 | 0.2632 | 1.0000 |
| value destroyed | 20 | 12665 | 12755 | 90 [-1335, 1445] | 14 / 5 / 1 | 0.0636 | 0.8264 |
| value lost | 20 | 23740 | 24060 | 320 [-3145, 3215] | 6 / 13 / 1 | 0.1671 | 1.0000 |
| trade share | 20 | 0.441 | 0.449 | 0.008 [-0.036, 0.069] | 11 / 9 / 0 | 0.8238 | 1.0000 |
| peak army value | 20 | 4950 | 3830 | -1120 [-1590, -650] | 2 / 18 / 0 | 0.0004 | 0.0056 * |
| units built | 20 | 63.35 | 66.2 | 2.85 [-3.8, 7.7] | 12 / 7 / 1 | 0.3593 | 1.0000 |
| production idle fraction | 20 | 0.006 | 0.004 | -0.002 [-0.006, 0] | 13 / 7 / 0 | 0.2632 | 1.0000 |
| average credits (float) | 20 | 7382 | 7356 | -25.316 [-604, 603] | 10 / 10 / 0 | 1.0000 | 1.0000 |
| first attack s | 20 | 192 | 192 | 0 [0, 0] | 0 / 0 / 20 (n/d) | 1.0000 | 1.0000 |
| duration s | 20 | 884 | 899 | 15.4 [-49.95, 66.8] | 13 / 7 / 0 (n/d) | 0.2632 | 1.0000 |
| activations /10 min | 20 | 2.347 | 2.132 | -0.215 [-0.464, 0.028] | 6 / 14 / 0 (n/d) | 0.1153 | 1.0000 |
| invalid plan rate | 20 | 0 | 0 | 0 [0, 0] | 0 / 0 / 20 | 1.0000 | 1.0000 |
| USD per match | 20 | 0 | 0 | 0 [0, 0] | 0 / 0 / 20 | 1.0000 | 1.0000 |

## Paired differences vs selector

Pairs share opponent, map and seed. Difference = arm − baseline, mean with a 95% seeded bootstrap interval; better/worse/tied count pairs by the metric's direction (n/d: no better direction, counted as higher/lower); p is the exact two-sided sign test over untied pairs, Holm-adjusted over the arm's metrics. `*` marks Holm p < 0.05.

### pinned:allied-boom vs selector (20 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 20 | 0.5 | 0.55 | 0.05 [0, 0.15] | 1 / 0 / 19 | 1.0000 | 1.0000 |
| final asset margin | 20 | 5050 | 9618 | 4568 [-450, 9818] | 14 / 4 / 2 | 0.0309 | 0.4015 |
| value destroyed | 20 | 12665 | 12155 | -510 [-1880, 630] | 10 / 5 / 5 | 0.3018 | 1.0000 |
| value lost | 20 | 23740 | 23095 | -645 [-5000, 3130] | 5 / 12 / 3 | 0.1435 | 1.0000 |
| trade share | 20 | 0.441 | 0.442 | 0.002 [-0.04, 0.048] | 8 / 10 / 2 | 0.8145 | 1.0000 |
| peak army value | 20 | 4950 | 4045 | -905 [-1520, -50] | 2 / 18 / 0 | 0.0004 | 0.0056 * |
| units built | 20 | 63.35 | 68.4 | 5.05 [1.6, 8.7] | 12 / 8 / 0 | 0.5034 | 1.0000 |
| production idle fraction | 20 | 0.006 | 0.004 | -0.002 [-0.006, -0] | 13 / 5 / 2 | 0.0963 | 1.0000 |
| average credits (float) | 20 | 7382 | 7764 | 382 [-163, 948] | 8 / 12 / 0 | 0.5034 | 1.0000 |
| first attack s | 20 | 192 | 192 | 0 [0, 0] | 0 / 0 / 20 (n/d) | 1.0000 | 1.0000 |
| duration s | 20 | 884 | 920 | 36 [-3.3, 72.75] | 13 / 5 / 2 (n/d) | 0.0963 | 1.0000 |
| activations /10 min | 20 | 2.347 | 2.117 | -0.23 [-0.572, 0.089] | 5 / 14 / 1 (n/d) | 0.0636 | 0.7628 |
| invalid plan rate | 20 | 0 | 0 | 0 [0, 0] | 0 / 0 / 20 | 1.0000 | 1.0000 |
| USD per match | 20 | 0 | 0 | 0 [0, 0] | 0 / 0 / 20 | 1.0000 | 1.0000 |

### pinned:induced-allied-boom-allied-553cbefc vs selector (20 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 20 | 0.5 | 0.5 | 0 [0, 0] | 0 / 0 / 20 | 1.0000 | 1.0000 |
| final asset margin | 20 | 5050 | 8430 | 3380 [-1370, 8453] | 13 / 7 / 0 | 0.2632 | 1.0000 |
| value destroyed | 20 | 12665 | 12755 | 90 [-1335, 1445] | 14 / 5 / 1 | 0.0636 | 0.8264 |
| value lost | 20 | 23740 | 24060 | 320 [-3145, 3215] | 6 / 13 / 1 | 0.1671 | 1.0000 |
| trade share | 20 | 0.441 | 0.449 | 0.008 [-0.036, 0.069] | 11 / 9 / 0 | 0.8238 | 1.0000 |
| peak army value | 20 | 4950 | 3830 | -1120 [-1590, -650] | 2 / 18 / 0 | 0.0004 | 0.0056 * |
| units built | 20 | 63.35 | 66.2 | 2.85 [-3.8, 7.7] | 12 / 7 / 1 | 0.3593 | 1.0000 |
| production idle fraction | 20 | 0.006 | 0.004 | -0.002 [-0.006, 0] | 13 / 7 / 0 | 0.2632 | 1.0000 |
| average credits (float) | 20 | 7382 | 7356 | -25.316 [-604, 603] | 10 / 10 / 0 | 1.0000 | 1.0000 |
| first attack s | 20 | 192 | 192 | 0 [0, 0] | 0 / 0 / 20 (n/d) | 1.0000 | 1.0000 |
| duration s | 20 | 884 | 899 | 15.4 [-49.95, 66.8] | 13 / 7 / 0 (n/d) | 0.2632 | 1.0000 |
| activations /10 min | 20 | 2.347 | 2.132 | -0.215 [-0.464, 0.028] | 6 / 14 / 0 (n/d) | 0.1153 | 1.0000 |
| invalid plan rate | 20 | 0 | 0 | 0 [0, 0] | 0 / 0 / 20 | 1.0000 | 1.0000 |
| USD per match | 20 | 0 | 0 | 0 [0, 0] | 0 / 0 / 20 | 1.0000 | 1.0000 |

## Economy, production and combat (arm side, per-match averages)

Production idle: seconds with a factory, nothing queued and credits for the cheapest item, over match seconds. Trade efficiency: enemy value destroyed in combat / own value lost in combat (pooled over matches).

Time to first attack: first second a combat unit stood in a region holding an enemy structure (mean over matches where it happened; count in brackets).

| Arm | Duration s | Units built | Buildings built | Peak army value | Idle fraction | Avg credits | Final assets (arm / opp) | Trade efficiency | First attack s |
|---|---|---|---|---|---|---|---|---|---|
| pinned:allied-boom | 920 | 68.4 | 10.0 | 4045 | 0.004 | 7764 | 29775 / 20158 | 0.53 (243100/461900) | 192 (20/20) |
| pinned:induced-allied-boom-allied-553cbefc | 899 | 66.2 | 9.1 | 3830 | 0.004 | 7356 | 28085 / 19655 | 0.53 (255100/481200) | 192 (20/20) |
| selector | 884 | 63.4 | 11.4 | 4950 | 0.006 | 7382 | 24715 / 19665 | 0.53 (253300/474800) | 192 (20/20) |

## Strategy layer (arm side)

Proposals, invalid plans and lateness are the arm's primary strategist's: rejected / proposals; seconds from a proposal's snapshot to its validation; late-discard count / proposals. Fallback and emergency proposals (immediate and always valid) are counted apart so they cannot flatter the strategist under test. Shadow columns are the shadow strategist's own lateness and would-be invalid plans. Churn: activations and posture flips per 10 game minutes.

Shadow agreement: shadow proposals naming the same playbook as the primary proposal for the same request / shadow proposals whose request got a primary proposal.

| Arm | Proposals | Invalid plans | Mean lateness s | Late-discarded | Fallback proposals | Failed requests | Shadow proposals | Shadow invalid | Shadow mean lateness s | Shadow agreement | Activations /10 min | Posture flips /10 min |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| pinned:allied-boom | 1041 | 0/1041 (0.000) | 0.00 | 0/1041 (0.000) | 44 | 0 | 0 | n/a | n/a | n/a | 2.32 | 0.72 |
| pinned:induced-allied-boom-allied-553cbefc | 1013 | 0/1013 (0.000) | 0.00 | 0/1013 (0.000) | 44 | 0 | 0 | n/a | n/a | n/a | 2.40 | 0.80 |
| selector | 970 | 0/970 (0.000) | 0.00 | 0/970 (0.000) | 0 | 0 | 0 | n/a | n/a | n/a | 2.65 | 1.02 |

## Command gate and simulator rejections (arm side, totals)

- **pinned:allied-boom**: 0 dropped by the gate (none); 202 commands rejected by the simulator.
- **pinned:induced-allied-boom-allied-553cbefc**: 0 dropped by the gate (none); 185 commands rejected by the simulator.
- **selector**: 0 dropped by the gate (none); 215 commands rejected by the simulator.

## Inference cost (arm side)

- **pinned:allied-boom** (no model): 0 in / 0 out tokens and $0.0000 per match, of which $0.0000 on failed requests.
- **pinned:induced-allied-boom-allied-553cbefc** (no model): 0 in / 0 out tokens and $0.0000 per match, of which $0.0000 on failed requests.
- **selector** (no model): 0 in / 0 out tokens and $0.0000 per match, of which $0.0000 on failed requests.

## Hidden-information leakage

- **pinned:allied-boom**: 0 validator `fog.*` rejections.
- **pinned:induced-allied-boom-allied-553cbefc**: 0 validator `fog.*` rejections.
- **selector**: 0 validator `fog.*` rejections.

Probe: two lockstep simulations, hidden state of one perturbed (`SimLeakageProbe`: enemy credits and queue, wounded hidden enemies, a hidden unit in an unseen region, one announced there through the event path, and one just across a border inside the arm's weapon reach), strategist-context hash compared on every following frame until the window closes or the objects the arm can see first differ. A differing frame is one where the context changed while everything visible was still identical. Fog-violation frames are arm frames, over the whole run of both simulations, that carried an enemy object or event from a region the arm did not see: a per-frame check that finds leaks the perturbation does not exercise.

| Arm | Map | Seed | Perturbed at s | Lockstep before | Compared s | Differing frames | Fog-violation frames | Note |
|---|---|---|---|---|---|---|---|---|
| pinned:induced-allied-boom-allied-553cbefc | open-steppe | 1 | 90 | yes | 45 | 0/671 | 0 | stopped at 135 s: the arm saw a legitimate difference |
| pinned:induced-allied-boom-allied-553cbefc | open-steppe | 1 | 240 | yes | 0 | 0/1 | 0 | stopped at 240 s: the arm saw a legitimate difference |
| pinned:allied-boom | open-steppe | 1 | 90 | yes | 45 | 0/671 | 0 | stopped at 135 s: the arm saw a legitimate difference |
| pinned:allied-boom | open-steppe | 1 | 240 | yes | 0 | 0/1 | 0 | stopped at 240 s: the arm saw a legitimate difference |
| selector | open-steppe | 1 | 90 | yes | 45 | 0/671 | 0 | stopped at 135 s: the arm saw a legitimate difference |
| selector | open-steppe | 1 | 240 | yes | 0 | 0/1 | 0 | stopped at 240 s: the arm saw a legitimate difference |

