# Bindery region sim arena report

Results are from the bindery region simulator with the approximate `bindery-sim-approx` rules, not retail RA2; they are directional.
The arm is a full `BotRuntime`. Opponents named `ai-*` are the independent scripted AI (`Bindery.Ra2.Bot.Sim.Opponents`, no shared planner code; `:easy`/`:medium`/`:hard`, default hard); the other opponents are pinned-playbook styles running a frozen copy of the bot's stack as of commit 7f3e2c7 (`Bindery.Ra2.Bot.Baseline`), a stationary benchmark; `live-<style>` runs a pinned style on the live stack. The arm plays Allied on odd seeds and Soviet on even seeds. Match limit 1200 s (a timeout is won on final asset value).
Opponents are split as well as maps. Held-out opponents (`ai-horde`, `ai-armor`, independent scripted styles) are never used for the selector's default playbook, bandit learning, distillation datasets or tuning; see "Held-out opponents" below. They are not untouched: their difficulty and the contested benchmark's handicaps were calibrated against the selector's win rate on all five maps, held-out maps included, so the selector's own held-out win rate is near one half partly by construction. Every other opponent is a training opponent: the selector's default playbook was chosen from a style-versus-style matrix against the pinned-playbook styles (training maps only), so win rates against training opponents are partly in-sample.
Benchmark: contested (opponent income ×1, Allied income ×1, Allied starting credits 20000, opponent starting credits default, combat noise ±0.25).
Labels in this run: `knob:SeenAttackForceRatio=0.8`, `oracle`.

Matches: 3360

## Win rate (arm × split)

Distinct games drop repeats of an identical game (same arm faction, decision log and outcome on the same map): against a differently named opponent whose style had not diverged when the match ended, or on another seed that changed nothing; the distinct interval is the one to read. The arm plays Allied on odd seeds and Soviet on even seeds, and starts west on seeds 1-2, 5-6, ... and east on 3-4, 7-8, ...; the fixture is asymmetric, so the faction and side columns show the mix behind each rate.

| Arm | Split | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) | Distinct games | Distinct wins | 95% interval, distinct | As Allied | As Soviet | As west | As east | Eliminations won | Timeouts |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| selector | training | 1434 | 246 | 0 | 1680 | 0.854 | [0.836, 0.870] | 1013 | 826 | [0.790, 0.838] | 620/840 | 814/840 | 725/840 | 709/840 | 1328 | 131 |
| selector-oracle | training | 1515 | 165 | 0 | 1680 | 0.902 | [0.887, 0.915] | 941 | 798 | [0.824, 0.870] | 736/840 | 779/840 | 752/840 | 763/840 | 1362 | 221 |

Benchmark check: the baseline `selector` scored 0.854 over 1680 matches, outside the 30–70% band: the benchmark is saturated and win-rate comparisons carry little information (use `--benchmark contested`).

## Win rate by opponent style

| Arm | Split | ai-air | ai-balanced | ai-rush | ai-turtle | balanced | harass | live-balanced | live-harass | live-rush | live-tech | live-turtle | rush | tech | turtle |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| selector | training | 120/120 | 120/120 | 107/120 | 120/120 | 120/120 | 120/120 | 60/120 | 55/120 | 87/120 | 99/120 | 66/120 | 120/120 | 120/120 | 120/120 |
| selector-oracle | training | 120/120 | 120/120 | 118/120 | 120/120 | 120/120 | 120/120 | 72/120 | 87/120 | 89/120 | 98/120 | 91/120 | 120/120 | 120/120 | 120/120 |

Identical games (same arm decision log and outcome against differently named opponents; counted once in the distinct columns and paired tables):

- selector: balanced = harass = rush = tech = turtle on 97 map × seed cells
- selector: live-balanced = live-harass on 59 map × seed cells
- selector: live-balanced = live-rush on 48 map × seed cells
- selector: live-balanced = live-rush = live-tech on 12 map × seed cells
- selector-oracle: balanced = harass = rush = tech = turtle on 72 map × seed cells
- selector-oracle: live-balanced = live-harass on 30 map × seed cells
- selector-oracle: live-balanced = live-rush on 22 map × seed cells
- selector-oracle: live-balanced = live-rush = live-tech on 18 map × seed cells

## Held-out opponents

No held-out opponent in this run (`ai-horde`, `ai-armor`; `--opponents heldout` adds them). Every win rate above is against training opponents.

## Perception bottleneck (belief − oracle)

Pairs share opponent, map and seed. Difference = arm − baseline, mean with a 95% seeded bootstrap interval; better/worse/tied count pairs by the metric's direction (n/d: no better direction, counted as higher/lower); p is the exact two-sided sign test over untied pairs, Holm-adjusted over the arm's metrics. `*` marks Holm p < 0.05.

### selector vs selector-oracle (1098 pairs; 582 identical to another opponent's pair collapsed)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 1098 | 0.87 | 0.802 | -0.067 [-0.092, -0.042] | 62 / 136 / 900 | <0.0001 | <0.0001 * |
| final asset margin | 1098 | 11703 | 8701 | -3002 [-3990, -2059] | 415 / 559 / 124 | <0.0001 | <0.0001 * |
| value destroyed | 1098 | 12731 | 10837 | -1895 [-2445, -1312] | 374 / 411 / 313 | 0.1988 | 0.5964 |
| value lost | 1098 | 5959 | 5830 | -128 [-519, 243] | 301 / 478 / 319 | <0.0001 | <0.0001 * |
| trade share | 1098 | 0.791 | 0.733 | -0.058 [-0.071, -0.045] | 262 / 585 / 251 | <0.0001 | <0.0001 * |
| peak army value | 1098 | 3797 | 3245 | -552 [-670, -436] | 517 / 391 / 190 | <0.0001 | 0.0002 * |
| units built | 1098 | 43.457 | 38.415 | -5.042 [-6.505, -3.505] | 675 / 257 / 166 | <0.0001 | <0.0001 * |
| production idle fraction | 1098 | 0.015 | 0.013 | -0.002 [-0.005, 0] | 747 / 278 / 73 | <0.0001 | <0.0001 * |
| average credits (float) | 1098 | 10995 | 11479 | 484 [367, 598] | 562 / 498 / 38 | 0.0529 | 0.2117 |
| first attack s | 942 | 248 | 140 | -107 [-114, -101] | 53 / 888 / 1 (n/d) | <0.0001 | <0.0001 * |
| duration s | 1098 | 555 | 482 | -73.002 [-91.747, -53.679] | 679 / 289 / 130 (n/d) | <0.0001 | <0.0001 * |
| activations /10 min | 1098 | 2.962 | 2.529 | -0.434 [-0.69, -0.225] | 273 / 782 / 43 (n/d) | <0.0001 | <0.0001 * |
| invalid plan rate | 1098 | 0 | 0 | 0 [0, 0] | 0 / 0 / 1098 | 1.0000 | 1.0000 |
| USD per match | 1098 | 0 | 0 | 0 [0, 0] | 0 / 0 / 1098 | 1.0000 | 1.0000 |

## Economy, production and combat (arm side, per-match averages)

Production idle: seconds with a factory, nothing queued and credits for the cheapest item, over match seconds. Trade efficiency: enemy value destroyed in combat / own value lost in combat (pooled over matches).

Time to first attack: first second a combat unit stood in a region holding an enemy structure (mean over matches where it happened; count in brackets).

| Arm | Duration s | Units built | Buildings built | Peak army value | Idle fraction | Avg credits | Final assets (arm / opp) | Trade efficiency | First attack s |
|---|---|---|---|---|---|---|---|---|---|
| selector | 415 | 33.5 | 5.6 | 3139 | 0.012 | 11553 | 22993 / 14815 | 2.38 (16207800/6805200) | 136 (1546/1680) |
| selector-oracle | 461 | 36.6 | 6.5 | 3471 | 0.014 | 11230 | 24102 / 13913 | 2.63 (18195950/6920000) | 239 (1625/1680) |

## Strategy layer (arm side)

Proposals, invalid plans and lateness are the arm's primary strategist's: rejected / proposals; seconds from a proposal's snapshot to its validation; late-discard count / proposals. Fallback and emergency proposals (immediate and always valid) are counted apart so they cannot flatter the strategist under test. Shadow columns are the shadow strategist's own lateness and would-be invalid plans. Churn: activations and posture flips per 10 game minutes.

Shadow agreement: shadow proposals naming the same playbook as the primary proposal for the same request / shadow proposals whose request got a primary proposal.

| Arm | Proposals | Invalid plans | Mean lateness s | Late-discarded | Fallback proposals | Failed requests | Shadow proposals | Shadow invalid | Shadow mean lateness s | Shadow agreement | Activations /10 min | Posture flips /10 min |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| selector | 38351 | 0/38351 (0.000) | 0.00 | 0/38351 (0.000) | 4 | 0 | 0 | n/a | n/a | n/a | 2.85 | 1.26 |
| selector-oracle | 43961 | 0/43961 (0.000) | 0.00 | 0/43961 (0.000) | 0 | 0 | 0 | n/a | n/a | n/a | 3.32 | 1.94 |

## Command gate and simulator rejections (arm side, totals)

- **selector**: 0 dropped by the gate (none); 1086 commands rejected by the simulator.
- **selector-oracle**: 0 dropped by the gate (none); 43797 commands rejected by the simulator.

## Inference cost (arm side)

- **selector** (no model): 0 in / 0 out tokens and $0.0000 per match, of which $0.0000 on failed requests.
- **selector-oracle** (no model): 0 in / 0 out tokens and $0.0000 per match, of which $0.0000 on failed requests.

## Hidden-information leakage

- **selector**: 0 validator `fog.*` rejections.
- **selector-oracle**: 0 validator `fog.*` rejections.

Probe: two lockstep simulations, hidden state of one perturbed (`SimLeakageProbe`: enemy credits and queue, wounded hidden enemies, a hidden unit in an unseen region, one announced there through the event path, and one just across a border inside the arm's weapon reach), strategist-context hash compared on every following frame until the window closes or the objects the arm can see first differ. A differing frame is one where the context changed while everything visible was still identical. Fog-violation frames are arm frames, over the whole run of both simulations, that carried an enemy object or event from a region the arm did not see: a per-frame check that finds leaks the perturbation does not exercise.

| Arm | Map | Seed | Perturbed at s | Lockstep before | Compared s | Differing frames | Fog-violation frames | Note |
|---|---|---|---|---|---|---|---|---|
| selector | twin-valley | 1 | 90 | yes | 44 | 0/659 | 0 | stopped at 134 s: the arm saw a legitimate difference |
| selector | twin-valley | 1 | 240 | yes | 0 | 0/3 | 0 | stopped at 240 s: the arm saw a legitimate difference |
| selector-oracle | twin-valley | 1 | 90 | no | 0 | 0/0 | 0 | not applicable: oracle frames carry hidden state by design |
| selector-oracle | twin-valley | 1 | 240 | no | 0 | 0/0 | 0 | not applicable: oracle frames carry hidden state by design |

