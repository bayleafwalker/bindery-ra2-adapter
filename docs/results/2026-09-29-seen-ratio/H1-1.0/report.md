# Bindery region sim arena report

Results are from the bindery region simulator with the approximate `bindery-sim-approx` rules, not retail RA2; they are directional.
The arm is a full `BotRuntime`. Opponents named `ai-*` are the independent scripted AI (`Bindery.Ra2.Bot.Sim.Opponents`, no shared planner code; `:easy`/`:medium`/`:hard`, default hard); the other opponents are pinned-playbook styles running a frozen copy of the bot's stack as of commit 7f3e2c7 (`Bindery.Ra2.Bot.Baseline`), a stationary benchmark; `live-<style>` runs a pinned style on the live stack. The arm plays Allied on odd seeds and Soviet on even seeds. Match limit 1200 s (a timeout is won on final asset value).
Opponents are split as well as maps. Held-out opponents (`ai-horde`, `ai-armor`, independent scripted styles) are never used for the selector's default playbook, bandit learning, distillation datasets or tuning; see "Held-out opponents" below. They are not untouched: their difficulty and the contested benchmark's handicaps were calibrated against the selector's win rate on all five maps, held-out maps included, so the selector's own held-out win rate is near one half partly by construction. Every other opponent is a training opponent: the selector's default playbook was chosen from a style-versus-style matrix against the pinned-playbook styles (training maps only), so win rates against training opponents are partly in-sample.
Benchmark: contested (opponent income ×1, Allied income ×1, Allied starting credits 20000, opponent starting credits default, combat noise ±0.25).
Labels in this run: `knob:SeenAttackForceRatio=1`, `oracle`.

Matches: 2240

## Win rate (arm × split)

Distinct games drop repeats of an identical game (same arm faction, decision log and outcome on the same map): against a differently named opponent whose style had not diverged when the match ended, or on another seed that changed nothing; the distinct interval is the one to read. The arm plays Allied on odd seeds and Soviet on even seeds, and starts west on seeds 1-2, 5-6, ... and east on 3-4, 7-8, ...; the fixture is asymmetric, so the faction and side columns show the mix behind each rate.

| Arm | Split | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) | Distinct games | Distinct wins | 95% interval, distinct | As Allied | As Soviet | As west | As east | Eliminations won | Timeouts |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| selector | heldout | 950 | 170 | 0 | 1120 | 0.848 | [0.826, 0.868] | 633 | 503 | [0.761, 0.824] | 400/560 | 550/560 | 473/560 | 477/560 | 912 | 45 |
| selector-oracle | heldout | 1028 | 91 | 1 | 1120 | 0.918 | [0.900, 0.933] | 581 | 500 | [0.830, 0.886] | 489/560 | 539/560 | 512/560 | 516/560 | 956 | 84 |

Benchmark check: the baseline `selector` scored 0.848 over 1120 matches, outside the 30–70% band: the benchmark is saturated and win-rate comparisons carry little information (use `--benchmark contested`).

## Win rate by opponent style

| Arm | Split | ai-air | ai-balanced | ai-rush | ai-turtle | balanced | harass | live-balanced | live-harass | live-rush | live-tech | live-turtle | rush | tech | turtle |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| selector | heldout | 80/80 | 80/80 | 80/80 | 80/80 | 80/80 | 80/80 | 38/80 | 40/80 | 39/80 | 61/80 | 52/80 | 80/80 | 80/80 | 80/80 |
| selector-oracle | heldout | 80/80 | 80/80 | 80/80 | 78/80 | 80/80 | 80/80 | 62/80 | 66/80 | 68/80 | 75/80 | 39/80 | 80/80 | 80/80 | 80/80 |

Identical games (same arm decision log and outcome against differently named opponents; counted once in the distinct columns and paired tables):

- selector: balanced = harass = rush = tech = turtle on 54 map × seed cells
- selector: live-balanced = live-harass on 40 map × seed cells
- selector: live-balanced = live-rush on 38 map × seed cells
- selector-oracle: balanced = harass = rush = tech = turtle on 36 map × seed cells
- selector-oracle: live-balanced = live-harass on 23 map × seed cells
- selector-oracle: live-balanced = live-rush on 22 map × seed cells
- selector-oracle: live-balanced = live-rush = live-tech on 18 map × seed cells

## Held-out opponents

No held-out opponent in this run (`ai-horde`, `ai-armor`; `--opponents heldout` adds them). Every win rate above is against training opponents.

## Perception bottleneck (belief − oracle)

Pairs share opponent, map and seed. Difference = arm − baseline, mean with a 95% seeded bootstrap interval; better/worse/tied count pairs by the metric's direction (n/d: no better direction, counted as higher/lower); p is the exact two-sided sign test over untied pairs, Holm-adjusted over the arm's metrics. `*` marks Holm p < 0.05.

### selector vs selector-oracle (711 pairs; 409 identical to another opponent's pair collapsed)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 711 | 0.887 | 0.793 | -0.094 [-0.122, -0.068] | 22 / 88 / 601 | <0.0001 | <0.0001 * |
| final asset margin | 711 | 10985 | 9691 | -1294 [-3026, 322] | 341 / 295 / 75 | 0.0743 | 0.2971 |
| value destroyed | 711 | 11736 | 9779 | -1957 [-2627, -1307] | 265 / 268 / 178 | 0.9310 | 1.0000 |
| value lost | 711 | 5591 | 4846 | -745 [-1122, -351] | 284 / 214 / 213 | 0.0020 | 0.0098 * |
| trade share | 711 | 0.788 | 0.73 | -0.057 [-0.072, -0.044] | 196 / 337 / 178 | <0.0001 | <0.0001 * |
| peak army value | 711 | 3540 | 3220 | -320 [-501, -158] | 353 / 251 / 107 | <0.0001 | 0.0002 * |
| units built | 711 | 39.579 | 35.385 | -4.194 [-6.055, -2.34] | 451 / 191 / 69 | <0.0001 | <0.0001 * |
| production idle fraction | 711 | 0.013 | 0.009 | -0.003 [-0.005, -0.001] | 493 / 194 / 24 | <0.0001 | <0.0001 * |
| average credits (float) | 711 | 11106 | 11915 | 809 [638, 979] | 435 / 260 / 16 | <0.0001 | <0.0001 * |
| first attack s | 598 | 221 | 152 | -68.421 [-72.216, -64.339] | 77 / 521 / 0 (n/d) | <0.0001 | <0.0001 * |
| duration s | 711 | 500 | 432 | -68.001 [-87.596, -49.381] | 434 / 227 / 50 (n/d) | <0.0001 | <0.0001 * |
| activations /10 min | 711 | 2.283 | 2.268 | -0.015 [-0.118, 0.092] | 227 / 462 / 22 (n/d) | <0.0001 | <0.0001 * |
| invalid plan rate | 711 | 0 | 0 | 0 [0, 0] | 0 / 0 / 711 | 1.0000 | 1.0000 |
| USD per match | 711 | 0 | 0 | 0 [0, 0] | 0 / 0 / 711 | 1.0000 | 1.0000 |

## Economy, production and combat (arm side, per-match averages)

Production idle: seconds with a factory, nothing queued and credits for the cheapest item, over match seconds. Trade efficiency: enemy value destroyed in combat / own value lost in combat (pooled over matches).

Time to first attack: first second a combat unit stood in a region holding an enemy structure (mean over matches where it happened; count in brackets).

| Arm | Duration s | Units built | Buildings built | Peak army value | Idle fraction | Avg credits | Final assets (arm / opp) | Trade efficiency | First attack s |
|---|---|---|---|---|---|---|---|---|---|
| selector | 385 | 31.8 | 4.6 | 3168 | 0.010 | 11797 | 23174 / 13998 | 2.69 (10064800/3744800) | 144 (987/1120) |
| selector-oracle | 427 | 34.1 | 5.7 | 3335 | 0.012 | 11303 | 24288 / 14212 | 2.66 (11533450/4332200) | 222 (1108/1120) |

## Strategy layer (arm side)

Proposals, invalid plans and lateness are the arm's primary strategist's: rejected / proposals; seconds from a proposal's snapshot to its validation; late-discard count / proposals. Fallback and emergency proposals (immediate and always valid) are counted apart so they cannot flatter the strategist under test. Shadow columns are the shadow strategist's own lateness and would-be invalid plans. Churn: activations and posture flips per 10 game minutes.

Shadow agreement: shadow proposals naming the same playbook as the primary proposal for the same request / shadow proposals whose request got a primary proposal.

| Arm | Proposals | Invalid plans | Mean lateness s | Late-discarded | Fallback proposals | Failed requests | Shadow proposals | Shadow invalid | Shadow mean lateness s | Shadow agreement | Activations /10 min | Posture flips /10 min |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| selector | 23226 | 0/23226 (0.000) | 0.00 | 0/23226 (0.000) | 0 | 0 | 0 | n/a | n/a | n/a | 2.30 | 0.58 |
| selector-oracle | 26021 | 0/26021 (0.000) | 0.00 | 0/26021 (0.000) | 37 | 0 | 0 | n/a | n/a | n/a | 2.50 | 0.94 |

## Command gate and simulator rejections (arm side, totals)

- **selector**: 0 dropped by the gate (none); 1239 commands rejected by the simulator.
- **selector-oracle**: 0 dropped by the gate (none); 32384 commands rejected by the simulator.

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

