# Bindery region sim arena report

Results are from the bindery region simulator with the approximate `bindery-sim-approx` rules, not retail RA2; they are directional.
The arm is a full `BotRuntime`. Opponents named `ai-*` are the independent scripted AI (`Bindery.Ra2.Bot.Sim.Opponents`, no shared planner code; `:easy`/`:medium`/`:hard`, default hard); the other opponents are pinned-playbook styles running a frozen copy of the bot's stack as of commit 7f3e2c7 (`Bindery.Ra2.Bot.Baseline`), a stationary benchmark; `live-<style>` runs a pinned style on the live stack. The arm plays Allied on odd seeds and Soviet on even seeds. Match limit 1200 s (a timeout is won on final asset value).
Opponents are split as well as maps. Held-out opponents (`ai-horde`, `ai-armor`, independent scripted styles) are never used for the selector's default playbook, bandit learning, distillation datasets or tuning; see "Held-out opponents" below. They are not untouched: their difficulty and the contested benchmark's handicaps were calibrated against the selector's win rate on all five maps, held-out maps included, so the selector's own held-out win rate is near one half partly by construction. Every other opponent is a training opponent: the selector's default playbook was chosen from a style-versus-style matrix against the pinned-playbook styles (training maps only), so win rates against training opponents are partly in-sample.
Benchmark: contested (opponent income ×1, Allied income ×1, Allied starting credits 20000, opponent starting credits default, combat noise ±0.25).
Labels in this run: `pinned:generic-expand`, `pinned:induced-generic-expand-soviet-8f797e00`.

Matches: 324

## Win rate (arm × split)

Distinct games drop repeats of an identical game (same arm faction, decision log and outcome on the same map): against a differently named opponent whose style had not diverged when the match ended, or on another seed that changed nothing; the distinct interval is the one to read. The arm plays Allied on odd seeds and Soviet on even seeds, and starts west on seeds 1-2, 5-6, ... and east on 3-4, 7-8, ...; the fixture is asymmetric, so the faction and side columns show the mix behind each rate.

| Arm | Split | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) | Distinct games | Distinct wins | 95% interval, distinct | As Allied | As Soviet | As west | As east | Eliminations won | Timeouts | LLM delivery |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| pinned:generic-expand | training | 61 | 47 | 0 | 108 | 0.565 | [0.471, 0.654] | 108 | 61 | [0.471, 0.654] | 0/0 | 61/108 | 30/54 | 31/54 | 57 | 28 | n/a |
| pinned:induced-generic-expand-soviet-8f797e00 | training | 53 | 55 | 0 | 108 | 0.491 | [0.398, 0.584] | 108 | 53 | [0.398, 0.584] | 0/0 | 53/108 | 26/54 | 27/54 | 49 | 37 | n/a |
| selector | training | 103 | 5 | 0 | 108 | 0.954 | [0.896, 0.980] | 95 | 90 | [0.883, 0.977] | 0/0 | 103/108 | 52/54 | 51/54 | 94 | 14 | n/a |

Warning: the faction mix is unbalanced for pinned:generic-expand/training (0 Allied, 108 Soviet), pinned:induced-generic-expand-soviet-8f797e00/training (0 Allied, 108 Soviet), selector/training (0 Allied, 108 Soviet) (an odd `--seeds` gives the arm Allied more often); a win rate over it mixes faction strength into the result. Use an even `--seeds`.

Benchmark check: the baseline `selector` scored 0.954 over 108 matches, outside the 30–70% band: the benchmark is saturated and win-rate comparisons carry little information (use `--benchmark contested`).

## Win rate by opponent style

| Arm | Split | ai-air | ai-balanced | ai-rush | ai-turtle | live-balanced | live-harass | live-rush | live-tech | live-turtle |
|---|---|---|---|---|---|---|---|---|---|---|
| pinned:generic-expand | training | 12/12 | 12/12 | 4/12 | 12/12 | 1/12 | 1/12 | 11/12 | 2/12 | 6/12 |
| pinned:induced-generic-expand-soviet-8f797e00 | training | 12/12 | 12/12 | 2/12 | 12/12 | 1/12 | 1/12 | 9/12 | 0/12 | 4/12 |
| selector | training | 12/12 | 12/12 | 12/12 | 12/12 | 12/12 | 12/12 | 12/12 | 9/12 | 10/12 |

Identical games (same arm decision log and outcome against differently named opponents; counted once in the distinct columns and paired tables):

- selector: live-balanced = live-rush on 11 map × seed cells
- selector: live-balanced = live-rush = live-tech on 1 map × seed cell

## Held-out opponents

No held-out opponent in this run (`ai-horde`, `ai-armor`; `--opponents heldout` adds them). Every win rate above is against training opponents.

## Paired differences vs selector

Pairs share opponent, map and seed. Difference = arm − baseline, mean with a 95% seeded bootstrap interval; better/worse/tied count pairs by the metric's direction (n/d: no better direction, counted as higher/lower); p is the exact two-sided sign test over untied pairs, Holm-adjusted over the arm's metrics. `*` marks Holm p < 0.05.

### pinned:generic-expand vs selector (108 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 108 | 0.954 | 0.565 | -0.389 [-0.481, -0.287] | 1 / 43 / 64 | <0.0001 | <0.0001 * |
| final asset margin | 108 | 3471 | -2820 | -6292 [-11459, -1497] | 60 / 48 / 0 | 0.2898 | 1.0000 |
| value destroyed | 108 | 12595 | 20810 | 8215 [6372, 9977] | 89 / 19 / 0 | <0.0001 | <0.0001 * |
| value lost | 108 | 2480 | 10175 | 7695 [5557, 9944] | 11 / 97 / 0 | <0.0001 | <0.0001 * |
| trade share | 108 | 0.904 | 0.722 | -0.182 [-0.234, -0.135] | 18 / 90 / 0 | <0.0001 | <0.0001 * |
| peak army value | 108 | 2987 | 6185 | 3198 [2800, 3580] | 95 / 12 / 1 | <0.0001 | <0.0001 * |
| units built | 108 | 41.676 | 86.204 | 44.528 [40.194, 48.593] | 105 / 3 / 0 | <0.0001 | <0.0001 * |
| production idle fraction | 108 | 0.015 | 0.027 | 0.013 [0.003, 0.024] | 63 / 44 / 1 | 0.0814 | 0.4068 |
| average credits (float) | 108 | 6776 | 4967 | -1809 [-2152, -1458] | 76 / 32 / 0 | <0.0001 | 0.0002 * |
| first attack s | 108 | 122 | 125 | 2.815 [0, 8.444] | 1 / 0 / 107 (n/d) | 1.0000 | 1.0000 |
| duration s | 108 | 454 | 857 | 403 [345, 460] | 92 / 6 / 10 (n/d) | <0.0001 | <0.0001 * |
| activations /10 min | 108 | 2.134 | 1.679 | -0.455 [-0.871, -0.025] | 28 / 80 / 0 (n/d) | <0.0001 | <0.0001 * |
| invalid plan rate | 108 | 0 | 0 | 0 [0, 0] | 0 / 0 / 108 | 1.0000 | 1.0000 |
| USD per match | 108 | 0 | 0 | 0 [0, 0] | 0 / 0 / 108 | 1.0000 | 1.0000 |

### pinned:induced-generic-expand-soviet-8f797e00 vs selector (108 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 108 | 0.954 | 0.491 | -0.463 [-0.556, -0.37] | 0 / 50 / 58 | <0.0001 | <0.0001 * |
| final asset margin | 108 | 3471 | -4837 | -8308 [-13222, -3401] | 51 / 57 / 0 | 0.6306 | 1.0000 |
| value destroyed | 108 | 12595 | 20298 | 7703 [5793, 9544] | 86 / 22 / 0 | <0.0001 | <0.0001 * |
| value lost | 108 | 2480 | 10827 | 8347 [6247, 10605] | 11 / 97 / 0 | <0.0001 | <0.0001 * |
| trade share | 108 | 0.904 | 0.701 | -0.203 [-0.254, -0.156] | 18 / 90 / 0 | <0.0001 | <0.0001 * |
| peak army value | 108 | 2987 | 6425 | 3438 [3021, 3816] | 96 / 11 / 1 | <0.0001 | <0.0001 * |
| units built | 108 | 41.676 | 87.796 | 46.12 [41.676, 50.435] | 105 / 3 / 0 | <0.0001 | <0.0001 * |
| production idle fraction | 108 | 0.015 | 0.03 | 0.015 [0.005, 0.028] | 56 / 51 / 1 | 0.6992 | 1.0000 |
| average credits (float) | 108 | 6776 | 4771 | -2005 [-2339, -1662] | 79 / 29 / 0 | <0.0001 | <0.0001 * |
| first attack s | 108 | 122 | 125 | 3.074 [0, 9.222] | 1 / 0 / 107 (n/d) | 1.0000 | 1.0000 |
| duration s | 108 | 454 | 897 | 443 [381, 500] | 92 / 6 / 10 (n/d) | <0.0001 | <0.0001 * |
| activations /10 min | 108 | 2.134 | 1.692 | -0.441 [-0.843, -0.025] | 30 / 78 / 0 (n/d) | <0.0001 | <0.0001 * |
| invalid plan rate | 108 | 0 | 0 | 0 [0, 0] | 0 / 0 / 108 | 1.0000 | 1.0000 |
| USD per match | 108 | 0 | 0 | 0 [0, 0] | 0 / 0 / 108 | 1.0000 | 1.0000 |

## Economy, production and combat (arm side, per-match averages)

Production idle: seconds with a factory, nothing queued and credits for the cheapest item, over match seconds. Trade efficiency: enemy value destroyed in combat / own value lost in combat (pooled over matches).

Time to first attack: first second a combat unit stood in a region holding an enemy structure (mean over matches where it happened; count in brackets).

| Arm | Duration s | Units built | Buildings built | Peak army value | Idle fraction | Avg credits | Final assets (arm / opp) | Trade efficiency | First attack s |
|---|---|---|---|---|---|---|---|---|---|
| pinned:generic-expand | 857 | 86.2 | 7.9 | 6185 | 0.027 | 4967 | 23045 / 25866 | 2.05 (2247500/1098900) | 125 (108/108) |
| pinned:induced-generic-expand-soviet-8f797e00 | 897 | 87.8 | 8.0 | 6425 | 0.030 | 4771 | 22262 / 27099 | 1.87 (2192200/1169300) | 125 (108/108) |
| selector | 454 | 41.7 | 5.2 | 2987 | 0.015 | 6776 | 20673 / 17202 | 5.08 (1360300/267800) | 122 (108/108) |

## Strategy layer (arm side)

Proposals, invalid plans and lateness are the arm's primary strategist's: rejected / proposals; seconds from a proposal's snapshot to its validation; late-discard count / proposals. Fallback and emergency proposals (immediate and always valid) are counted apart so they cannot flatter the strategist under test. Shadow columns are the shadow strategist's own lateness and would-be invalid plans. Churn: activations and posture flips per 10 game minutes.

Shadow agreement: shadow proposals naming the same playbook as the primary proposal for the same request / shadow proposals whose request got a primary proposal.

| Arm | Proposals | Invalid plans | Mean lateness s | Late-discarded | Fallback proposals | Failed requests | Shadow proposals | Shadow invalid | Shadow mean lateness s | Shadow agreement | Activations /10 min | Posture flips /10 min |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| pinned:generic-expand | 5191 | 0/5191 (0.000) | 0.00 | 0/5191 (0.000) | 119 | 0 | 0 | n/a | n/a | n/a | 1.70 | 0.64 |
| pinned:induced-generic-expand-soviet-8f797e00 | 5435 | 0/5435 (0.000) | 0.00 | 0/5435 (0.000) | 127 | 0 | 0 | n/a | n/a | n/a | 1.68 | 0.64 |
| selector | 2623 | 0/2623 (0.000) | 0.00 | 0/2623 (0.000) | 0 | 0 | 0 | n/a | n/a | n/a | 2.61 | 1.09 |

## Command gate and simulator rejections (arm side, totals)

- **pinned:generic-expand**: 0 dropped by the gate (none); 362 commands rejected by the simulator.
- **pinned:induced-generic-expand-soviet-8f797e00**: 0 dropped by the gate (none); 364 commands rejected by the simulator.
- **selector**: 0 dropped by the gate (none); 111 commands rejected by the simulator.

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

