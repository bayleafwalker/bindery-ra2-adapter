# Bindery region sim arena report

Results are from the bindery region simulator with the approximate `bindery-sim-approx` rules, not retail RA2; they are directional.
The arm is a full `BotRuntime`. Opponents named `ai-*` are the independent scripted AI (`Bindery.Ra2.Bot.Sim.Opponents`, no shared planner code; `:easy`/`:medium`/`:hard`, default hard); the other opponents are pinned-playbook styles running a frozen copy of the bot's stack as of commit 7f3e2c7 (`Bindery.Ra2.Bot.Baseline`), a stationary benchmark; `live-<style>` runs a pinned style on the live stack. The arm plays Allied on odd seeds and Soviet on even seeds. Match limit 1200 s (a timeout is won on final asset value).
Opponents are split as well as maps. Held-out opponents (`ai-horde`, `ai-armor`, independent scripted styles) are never used for the selector's default playbook, bandit learning, distillation datasets or tuning; see "Held-out opponents" below. They are not untouched: their difficulty and the contested benchmark's handicaps were calibrated against the selector's win rate on all five maps, held-out maps included, so the selector's own held-out win rate is near one half partly by construction. Every other opponent is a training opponent: the selector's default playbook was chosen from a style-versus-style matrix against the pinned-playbook styles (training maps only), so win rates against training opponents are partly in-sample.
Benchmark: contested (opponent income ×1, Allied income ×1, Allied starting credits 20000, opponent starting credits default, combat noise ±0.25).
Labels in this run: `knob:SeenAttackForceRatio=0.8`, `oracle`.

Matches: 1920

## Win rate (arm × split)

Distinct games drop repeats of an identical game (same arm faction, decision log and outcome on the same map): against a differently named opponent whose style had not diverged when the match ended, or on another seed that changed nothing; the distinct interval is the one to read. The arm plays Allied on odd seeds and Soviet on even seeds, and starts west on seeds 1-2, 5-6, ... and east on 3-4, 7-8, ...; the fixture is asymmetric, so the faction and side columns show the mix behind each rate.

| Arm | Split | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) | Distinct games | Distinct wins | 95% interval, distinct | As Allied | As Soviet | As west | As east | Eliminations won | Timeouts |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| selector | heldout | 700 | 260 | 0 | 960 | 0.729 | [0.700, 0.756] | 918 | 658 | [0.687, 0.745] | 322/480 | 378/480 | 352/480 | 348/480 | 597 | 213 |
| selector-oracle | heldout | 680 | 279 | 1 | 960 | 0.708 | [0.679, 0.736] | 858 | 578 | [0.642, 0.704] | 246/480 | 434/480 | 334/480 | 346/480 | 556 | 205 |

Benchmark check: the baseline `selector` scored 0.729 over 960 matches, outside the 30–70% band: the benchmark is saturated and win-rate comparisons carry little information (use `--benchmark contested`).

## Win rate by opponent style

| Arm | Split | ai-armor:easy | ai-armor:hard | ai-armor:medium | ai-horde:easy | ai-horde:hard | ai-horde:medium |
|---|---|---|---|---|---|---|---|
| selector | heldout | 125/160 | 96/160 | 120/160 | 128/160 | 101/160 | 130/160 |
| selector-oracle | heldout | 120/160 | 82/160 | 112/160 | 120/160 | 126/160 | 120/160 |

## Held-out opponents

Opponent split × map split. Held-out opponents never inform the selector's defaults, bandit learning (the arena abandons the bandit's episode instead of crediting it, as it does on held-out maps), distillation datasets or teacher runs, or tuning (the tuner refuses them); their difficulty was calibrated against the selector on all maps, so read the selector's own held-out rate as partly calibrated and other arms' rates relative to it.

| Arm | Opponents | Maps | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) |
|---|---|---|---|---|---|---|---|---|
| selector | heldout | heldout | 700 | 260 | 0 | 960 | 0.729 | [0.700, 0.756] |
| selector-oracle | heldout | heldout | 680 | 279 | 1 | 960 | 0.708 | [0.679, 0.736] |
| selector | heldout | all | 700 | 260 | 0 | 960 | 0.729 | [0.700, 0.756] |
| selector-oracle | heldout | all | 680 | 279 | 1 | 960 | 0.708 | [0.679, 0.736] |

## Perception bottleneck (belief − oracle)

Pairs share opponent, map and seed. Difference = arm − baseline, mean with a 95% seeded bootstrap interval; better/worse/tied count pairs by the metric's direction (n/d: no better direction, counted as higher/lower); p is the exact two-sided sign test over untied pairs, Holm-adjusted over the arm's metrics. `*` marks Holm p < 0.05.

### selector vs selector-oracle (952 pairs; 8 identical to another opponent's pair collapsed)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 952 | 0.706 | 0.727 | 0.02 [-0.007, 0.046] | 90 / 70 / 792 | 0.1328 | 0.5313 |
| final asset margin | 952 | 4218 | 7613 | 3395 [2179, 4601] | 458 / 494 / 0 | 0.2566 | 0.7699 |
| value destroyed | 952 | 11513 | 12542 | 1029 [640, 1402] | 569 / 336 / 47 | <0.0001 | <0.0001 * |
| value lost | 952 | 16851 | 14408 | -2443 [-3340, -1557] | 544 / 350 / 58 | <0.0001 | <0.0001 * |
| trade share | 952 | 0.582 | 0.62 | 0.038 [0.021, 0.054] | 592 / 355 / 5 | <0.0001 | <0.0001 * |
| peak army value | 952 | 3325 | 3848 | 522 [435, 610] | 596 / 314 / 42 | <0.0001 | <0.0001 * |
| units built | 952 | 59.461 | 57.393 | -2.068 [-3.78, -0.422] | 498 / 289 / 165 | <0.0001 | <0.0001 * |
| production idle fraction | 952 | 0.007 | 0.008 | 0.001 [-0, 0.002] | 553 / 371 / 28 | <0.0001 | <0.0001 * |
| average credits (float) | 952 | 12542 | 11719 | -823 [-941, -710] | 742 / 206 / 4 | <0.0001 | <0.0001 * |
| first attack s | 952 | 214 | 159 | -54.424 [-58.557, -50.419] | 204 / 748 / 0 (n/d) | <0.0001 | <0.0001 * |
| duration s | 952 | 718 | 714 | -3.793 [-22.554, 14.545] | 530 / 341 / 81 (n/d) | <0.0001 | <0.0001 * |
| activations /10 min | 952 | 1.956 | 2.158 | 0.202 [0.135, 0.268] | 404 / 535 / 13 (n/d) | <0.0001 | 0.0001 * |
| invalid plan rate | 952 | 0 | 0 | 0 [0, 0] | 0 / 0 / 952 | 1.0000 | 1.0000 |
| USD per match | 952 | 0 | 0 | 0 [0, 0] | 0 / 0 / 952 | 1.0000 | 1.0000 |

## Economy, production and combat (arm side, per-match averages)

Production idle: seconds with a factory, nothing queued and credits for the cheapest item, over match seconds. Trade efficiency: enemy value destroyed in combat / own value lost in combat (pooled over matches).

Time to first attack: first second a combat unit stood in a region holding an enemy structure (mean over matches where it happened; count in brackets).

| Arm | Duration s | Units built | Buildings built | Peak army value | Idle fraction | Avg credits | Final assets (arm / opp) | Trade efficiency | First attack s |
|---|---|---|---|---|---|---|---|---|---|
| selector | 711 | 57.2 | 9.3 | 3840 | 0.008 | 11678 | 27501 / 19935 | 0.88 (12020400/13720400) | 159 (960/960) |
| selector-oracle | 714 | 59.3 | 10.2 | 3320 | 0.007 | 12495 | 25911 / 21703 | 0.69 (11040100/16047600) | 214 (960/960) |

## Strategy layer (arm side)

Proposals, invalid plans and lateness are the arm's primary strategist's: rejected / proposals; seconds from a proposal's snapshot to its validation; late-discard count / proposals. Fallback and emergency proposals (immediate and always valid) are counted apart so they cannot flatter the strategist under test. Shadow columns are the shadow strategist's own lateness and would-be invalid plans. Churn: activations and posture flips per 10 game minutes.

Shadow agreement: shadow proposals naming the same playbook as the primary proposal for the same request / shadow proposals whose request got a primary proposal.

| Arm | Proposals | Invalid plans | Mean lateness s | Late-discarded | Fallback proposals | Failed requests | Shadow proposals | Shadow invalid | Shadow mean lateness s | Shadow agreement | Activations /10 min | Posture flips /10 min |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| selector | 36987 | 0/36987 (0.000) | 0.00 | 0/36987 (0.000) | 0 | 0 | 0 | n/a | n/a | n/a | 2.52 | 0.86 |
| selector-oracle | 36785 | 0/36785 (0.000) | 0.00 | 0/36785 (0.000) | 0 | 0 | 0 | n/a | n/a | n/a | 2.05 | 0.75 |

## Command gate and simulator rejections (arm side, totals)

- **selector**: 0 dropped by the gate (none); 5152 commands rejected by the simulator.
- **selector-oracle**: 0 dropped by the gate (none); 22903 commands rejected by the simulator.

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

