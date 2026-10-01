# Bindery region sim arena report

Results are from the bindery region simulator with the approximate `bindery-sim-approx` rules, not retail RA2; they are directional.
The arm is a full `BotRuntime`. Opponents named `ai-*` are the independent scripted AI (`Bindery.Ra2.Bot.Sim.Opponents`, no shared planner code; `:easy`/`:medium`/`:hard`, default hard); the other opponents are pinned-playbook styles running a frozen copy of the bot's stack as of commit 7f3e2c7 (`Bindery.Ra2.Bot.Baseline`), a stationary benchmark; `live-<style>` runs a pinned style on the live stack. The arm plays Allied on odd seeds and Soviet on even seeds. Match limit 1200 s (a timeout is won on final asset value).
Opponents are split as well as maps. Held-out opponents (`ai-horde`, `ai-armor`, independent scripted styles) are never used for the selector's default playbook, bandit learning, distillation datasets or tuning; see "Held-out opponents" below. They are not untouched: their difficulty and the contested benchmark's handicaps were calibrated against the selector's win rate on all five maps, held-out maps included, so the selector's own held-out win rate is near one half partly by construction. Every other opponent is a training opponent: the selector's default playbook was chosen from a style-versus-style matrix against the pinned-playbook styles (training maps only), so win rates against training opponents are partly in-sample.
Benchmark: contested (opponent income ×1, Allied income ×1, Allied starting credits 20000, opponent starting credits default, combat noise ±0.25).
Labels in this run: `llm-openai:gpt-6-luna`, `vocabulary:Parameters`.

Matches: 60

## Win rate (arm × split)

Distinct games drop repeats of an identical game (same arm faction, decision log and outcome on the same map): against a differently named opponent whose style had not diverged when the match ended, or on another seed that changed nothing; the distinct interval is the one to read. The arm plays Allied on odd seeds and Soviet on even seeds, and starts west on seeds 1-2, 5-6, ... and east on 3-4, 7-8, ...; the fixture is asymmetric, so the faction and side columns show the mix behind each rate.

| Arm | Split | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) | Distinct games | Distinct wins | 95% interval, distinct | As Allied | As Soviet | As west | As east | Eliminations won | Timeouts | LLM delivery |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| llm-t1 | training | 7 | 23 | 0 | 30 | 0.233 | [0.118, 0.409] | 30 | 7 | [0.118, 0.409] | 7/30 | 0/0 | 3/15 | 4/15 | 1 | 7 | 956 answered, 2 failed (0.002) |
| selector | training | 10 | 20 | 0 | 30 | 0.333 | [0.192, 0.512] | 24 | 10 | [0.245, 0.612] | 10/30 | 0/0 | 5/15 | 5/15 | 3 | 7 | n/a |

Warning: the faction mix is unbalanced for llm-t1/training (30 Allied, 0 Soviet), selector/training (30 Allied, 0 Soviet) (an odd `--seeds` gives the arm Allied more often); a win rate over it mixes faction strength into the result. Use an even `--seeds`.

Benchmark check: the baseline `selector` scored 0.333 over 30 matches, inside the 30–70% band, so win-rate differences between arms can show.

## Win rate by opponent style

| Arm | Split | live-balanced | live-harass | live-rush | live-tech | live-turtle |
|---|---|---|---|---|---|---|
| llm-t1 | training | 0/6 | 0/6 | 2/6 | 3/6 | 2/6 |
| selector | training | 0/6 | 0/6 | 2/6 | 4/6 | 4/6 |

Identical games (same arm decision log and outcome against differently named opponents; counted once in the distinct columns and paired tables):

- selector: live-balanced = live-harass on 6 map × seed cells

## Held-out opponents

No held-out opponent in this run (`ai-horde`, `ai-armor`; `--opponents heldout` adds them). Every win rate above is against training opponents.

## Paired differences vs selector

Pairs share opponent, map and seed. Difference = arm − baseline, mean with a 95% seeded bootstrap interval; better/worse/tied count pairs by the metric's direction (n/d: no better direction, counted as higher/lower); p is the exact two-sided sign test over untied pairs, Holm-adjusted over the arm's metrics. `*` marks Holm p < 0.05.

### llm-t1 vs selector (30 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 30 | 0.333 | 0.233 | -0.1 [-0.233, 0.033] | 1 / 4 / 25 | 0.3750 | 1.0000 |
| final asset margin | 30 | 4580 | -627 | -5207 [-10833, 533] | 6 / 23 / 1 | 0.0023 | 0.0324 * |
| value destroyed | 30 | 7213 | 4793 | -2420 [-5337, 443] | 12 / 17 / 1 | 0.4583 | 1.0000 |
| value lost | 30 | 14077 | 14400 | 323 [-1767, 2130] | 13 / 15 / 2 | 0.8506 | 1.0000 |
| trade share | 30 | 0.242 | 0.169 | -0.073 [-0.146, -0.008] | 9 / 20 / 1 | 0.0614 | 0.7371 |
| peak army value | 30 | 3037 | 4067 | 1030 [-127, 2287] | 18 / 9 / 3 | 0.1221 | 1.0000 |
| units built | 30 | 40.033 | 39.533 | -0.5 [-10.8, 11.6] | 12 / 15 / 3 | 0.7011 | 1.0000 |
| production idle fraction | 30 | 0.018 | 0.007 | -0.011 [-0.03, 0] | 16 / 12 / 2 | 0.5716 | 1.0000 |
| average credits (float) | 30 | 14797 | 15139 | 342 [-424, 1296] | 18 / 12 / 0 | 0.3616 | 1.0000 |
| first attack s | 9 | 229 | 432 | 204 [56.778, 348] | 6 / 1 / 2 (n/d) | 0.1250 | 1.0000 |
| duration s | 30 | 595 | 581 | -14.167 [-123, 92.5] | 14 / 11 / 5 (n/d) | 0.6900 | 1.0000 |
| activations /10 min | 30 | 3.718 | 4.298 | 0.58 [-0.345, 1.438] | 22 / 8 / 0 (n/d) | 0.0161 | 0.2096 |
| invalid plan rate | 30 | 0 | 0 | 0 [0, 0] | 0 / 0 / 30 | 1.0000 | 1.0000 |
| USD per match | 30 | 0 | 0 | 0 [0, 0] | 0 / 0 / 30 | 1.0000 | 1.0000 |

## Economy, production and combat (arm side, per-match averages)

Production idle: seconds with a factory, nothing queued and credits for the cheapest item, over match seconds. Trade efficiency: enemy value destroyed in combat / own value lost in combat (pooled over matches).

Time to first attack: first second a combat unit stood in a region holding an enemy structure (mean over matches where it happened; count in brackets).

| Arm | Duration s | Units built | Buildings built | Peak army value | Idle fraction | Avg credits | Final assets (arm / opp) | Trade efficiency | First attack s |
|---|---|---|---|---|---|---|---|---|---|
| llm-t1 | 581 | 39.5 | 8.2 | 4067 | 0.007 | 15139 | 20190 / 20817 | 0.33 (143800/432000) | 397 (12/30) |
| selector | 595 | 40.0 | 10.0 | 3037 | 0.018 | 14797 | 21523 / 16943 | 0.51 (216400/422300) | 255 (16/30) |

## Strategy layer (arm side)

Proposals, invalid plans and lateness are the arm's primary strategist's: rejected / proposals; seconds from a proposal's snapshot to its validation; late-discard count / proposals. Fallback and emergency proposals (immediate and always valid) are counted apart so they cannot flatter the strategist under test. Shadow columns are the shadow strategist's own lateness and would-be invalid plans. Churn: activations and posture flips per 10 game minutes.

Shadow agreement: shadow proposals naming the same playbook as the primary proposal for the same request / shadow proposals whose request got a primary proposal.

| Arm | Proposals | Invalid plans | Mean lateness s | Late-discarded | Fallback proposals | Failed requests | Shadow proposals | Shadow invalid | Shadow mean lateness s | Shadow agreement | Activations /10 min | Posture flips /10 min |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| llm-t1 | 956 | 0/956 (0.000) | 8.84 | 19/956 (0.020) | 67 | 50 | 0 | n/a | n/a | n/a | 3.55 | 2.51 |
| selector | 1080 | 0/1080 (0.000) | 0.00 | 0/1080 (0.000) | 0 | 0 | 0 | n/a | n/a | n/a | 4.07 | 3.02 |

## Command gate and simulator rejections (arm side, totals)

- **llm-t1**: 0 dropped by the gate (none); 6 commands rejected by the simulator.
- **selector**: 0 dropped by the gate (none); 27 commands rejected by the simulator.

## Inference cost (arm side)

- **llm-t1** (gpt-6-luna): 262193 in / 28482 out tokens and $0.0000 per match, of which $0.0000 on failed requests; 958 requests on unpriced models are not in the cost.
- **selector** (no model): 0 in / 0 out tokens and $0.0000 per match, of which $0.0000 on failed requests.

## Hidden-information leakage

- **llm-t1**: 0 validator `fog.*` rejections.
- **selector**: 0 validator `fog.*` rejections.

Probe: two lockstep simulations, hidden state of one perturbed (`SimLeakageProbe`: enemy credits and queue, wounded hidden enemies, a hidden unit in an unseen region, one announced there through the event path, and one just across a border inside the arm's weapon reach), strategist-context hash compared on every following frame until the window closes or the objects the arm can see first differ. A differing frame is one where the context changed while everything visible was still identical. Fog-violation frames are arm frames, over the whole run of both simulations, that carried an enemy object or event from a region the arm did not see: a per-frame check that finds leaks the perturbation does not exercise.

| Arm | Map | Seed | Perturbed at s | Lockstep before | Compared s | Differing frames | Fog-violation frames | Note |
|---|---|---|---|---|---|---|---|---|
| llm-t1 | twin-valley | 1 | 90 | yes | 45 | 671/671 | 0 | stopped at 135 s: the arm saw a legitimate difference |
| llm-t1 | twin-valley | 1 | 240 | no | 0 | 0/0 | 0 | stopped at 240 s: the arm saw a legitimate difference |
| selector | twin-valley | 1 | 90 | yes | 44 | 0/659 | 0 | stopped at 134 s: the arm saw a legitimate difference |
| selector | twin-valley | 1 | 240 | yes | 0 | 0/3 | 0 | stopped at 240 s: the arm saw a legitimate difference |

