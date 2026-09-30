# Bindery region sim arena report

Results are from the bindery region simulator with the approximate `bindery-sim-approx` rules, not retail RA2; they are directional.
The arm is a full `BotRuntime`. Opponents named `ai-*` are the independent scripted AI (`Bindery.Ra2.Bot.Sim.Opponents`, no shared planner code; `:easy`/`:medium`/`:hard`, default hard); the other opponents are pinned-playbook styles running a frozen copy of the bot's stack as of commit 7f3e2c7 (`Bindery.Ra2.Bot.Baseline`), a stationary benchmark; `live-<style>` runs a pinned style on the live stack. The arm plays Allied on odd seeds and Soviet on even seeds. Match limit 1200 s (a timeout is won on final asset value).
Opponents are split as well as maps. Held-out opponents (`ai-horde`, `ai-armor`, independent scripted styles) are never used for the selector's default playbook, bandit learning, distillation datasets or tuning; see "Held-out opponents" below. They are not untouched: their difficulty and the contested benchmark's handicaps were calibrated against the selector's win rate on all five maps, held-out maps included, so the selector's own held-out win rate is near one half partly by construction. Every other opponent is a training opponent: the selector's default playbook was chosen from a style-versus-style matrix against the pinned-playbook styles (training maps only), so win rates against training opponents are partly in-sample.
Benchmark: standard (opponent income ×1, Allied income ×1, Allied starting credits default, opponent starting credits default, combat noise ±0).
Labels in this run: `pinned:generic-expand`, `pinned:induced-generic-expand-soviet-8f797e00`, `pinned:induced-soviet-rhino-rush-soviet-7d66f1a3`.

Matches: 80

## Win rate (arm × split)

Distinct games drop repeats of an identical game (same arm faction, decision log and outcome on the same map): against a differently named opponent whose style had not diverged when the match ended, or on another seed that changed nothing; the distinct interval is the one to read. The arm plays Allied on odd seeds and Soviet on even seeds, and starts west on seeds 1-2, 5-6, ... and east on 3-4, 7-8, ...; the fixture is asymmetric, so the faction and side columns show the mix behind each rate.

| Arm | Split | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) | Distinct games | Distinct wins | 95% interval, distinct | As Allied | As Soviet | As west | As east | Eliminations won | Timeouts | LLM delivery |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| pinned:generic-expand | heldout | 19 | 1 | 0 | 20 | 0.950 | [0.764, 0.991] | 20 | 19 | [0.764, 0.991] | 0/0 | 19/20 | 10/10 | 9/10 | 18 | 2 | n/a |
| pinned:induced-generic-expand-soviet-8f797e00 | heldout | 19 | 1 | 0 | 20 | 0.950 | [0.764, 0.991] | 20 | 19 | [0.764, 0.991] | 0/0 | 19/20 | 10/10 | 9/10 | 18 | 2 | n/a |
| pinned:induced-soviet-rhino-rush-soviet-7d66f1a3 | heldout | 9 | 10 | 1 | 20 | 0.450 | [0.258, 0.658] | 20 | 9 | [0.258, 0.658] | 0/0 | 9/20 | 5/10 | 4/10 | 2 | 15 | n/a |
| selector | heldout | 15 | 5 | 0 | 20 | 0.750 | [0.531, 0.888] | 20 | 15 | [0.531, 0.888] | 0/0 | 15/20 | 8/10 | 7/10 | 15 | 2 | n/a |

Warning: the faction mix is unbalanced for pinned:generic-expand/heldout (0 Allied, 20 Soviet), pinned:induced-generic-expand-soviet-8f797e00/heldout (0 Allied, 20 Soviet), pinned:induced-soviet-rhino-rush-soviet-7d66f1a3/heldout (0 Allied, 20 Soviet), selector/heldout (0 Allied, 20 Soviet) (an odd `--seeds` gives the arm Allied more often); a win rate over it mixes faction strength into the result. Use an even `--seeds`.

Benchmark check: the baseline `selector` scored 0.750 over 20 matches, outside the 30–70% band: the benchmark is saturated and win-rate comparisons carry little information (use `--benchmark contested`).

## Win rate by opponent style

| Arm | Split | ai-armor:easy | ai-armor:hard | ai-armor:medium | ai-horde:easy | ai-horde:hard |
|---|---|---|---|---|---|---|
| pinned:generic-expand | heldout | 4/4 | 4/4 | 4/4 | 3/4 | 4/4 |
| pinned:induced-generic-expand-soviet-8f797e00 | heldout | 4/4 | 4/4 | 4/4 | 3/4 | 4/4 |
| pinned:induced-soviet-rhino-rush-soviet-7d66f1a3 | heldout | 1/4 | 0/4 | 2/4 | 2/4 | 4/4 |
| selector | heldout | 4/4 | 2/4 | 3/4 | 4/4 | 2/4 |

## Held-out opponents

Opponent split × map split. Held-out opponents never inform the selector's defaults, bandit learning (the arena abandons the bandit's episode instead of crediting it, as it does on held-out maps), distillation datasets or teacher runs, or tuning (the tuner refuses them); their difficulty was calibrated against the selector on all maps, so read the selector's own held-out rate as partly calibrated and other arms' rates relative to it.

| Arm | Opponents | Maps | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) |
|---|---|---|---|---|---|---|---|---|
| pinned:generic-expand | heldout | heldout | 19 | 1 | 0 | 20 | 0.950 | [0.764, 0.991] |
| pinned:induced-generic-expand-soviet-8f797e00 | heldout | heldout | 19 | 1 | 0 | 20 | 0.950 | [0.764, 0.991] |
| pinned:induced-soviet-rhino-rush-soviet-7d66f1a3 | heldout | heldout | 9 | 10 | 1 | 20 | 0.450 | [0.258, 0.658] |
| selector | heldout | heldout | 15 | 5 | 0 | 20 | 0.750 | [0.531, 0.888] |
| pinned:generic-expand | heldout | all | 19 | 1 | 0 | 20 | 0.950 | [0.764, 0.991] |
| pinned:induced-generic-expand-soviet-8f797e00 | heldout | all | 19 | 1 | 0 | 20 | 0.950 | [0.764, 0.991] |
| pinned:induced-soviet-rhino-rush-soviet-7d66f1a3 | heldout | all | 9 | 10 | 1 | 20 | 0.450 | [0.258, 0.658] |
| selector | heldout | all | 15 | 5 | 0 | 20 | 0.750 | [0.531, 0.888] |

### Paired differences vs selector, held-out opponents only

Pairs share opponent, map and seed. Difference = arm − baseline, mean with a 95% seeded bootstrap interval; better/worse/tied count pairs by the metric's direction (n/d: no better direction, counted as higher/lower); p is the exact two-sided sign test over untied pairs, Holm-adjusted over the arm's metrics. `*` marks Holm p < 0.05.

### pinned:generic-expand vs selector (20 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 20 | 0.75 | 0.95 | 0.2 [0, 0.4] | 5 / 1 / 14 | 0.2188 | 1.0000 |
| final asset margin | 20 | 2373 | 20515 | 18143 [8825, 28140] | 18 / 2 / 0 | 0.0004 | 0.0040 * |
| value destroyed | 20 | 10730 | 22965 | 12235 [11350, 13200] | 20 / 0 / 0 | <0.0001 | <0.0001 * |
| value lost | 20 | 8745 | 3840 | -4905 [-12090, 1965] | 8 / 12 / 0 | 0.5034 | 1.0000 |
| trade share | 20 | 0.752 | 0.883 | 0.131 [-0.024, 0.286] | 16 / 4 / 0 | 0.0118 | 0.1064 |
| peak army value | 20 | 2705 | 6110 | 3405 [3145, 3625] | 20 / 0 / 0 | <0.0001 | <0.0001 * |
| units built | 20 | 44.7 | 88.4 | 43.7 [31.8, 58.75] | 20 / 0 / 0 | <0.0001 | <0.0001 * |
| production idle fraction | 20 | 0.013 | 0.005 | -0.007 [-0.018, -0.001] | 15 / 5 / 0 | 0.0414 | 0.3311 |
| average credits (float) | 20 | 6665 | 6673 | 7.97 [-193, 198] | 9 / 11 / 0 | 0.8238 | 1.0000 |
| first attack s | 20 | 122 | 122 | 0 [0, 0] | 0 / 0 / 20 (n/d) | 1.0000 | 1.0000 |
| duration s | 20 | 523 | 664 | 141 [-27.95, 318] | 15 / 5 / 0 (n/d) | 0.0414 | 0.3311 |
| activations /10 min | 20 | 2.124 | 1.194 | -0.931 [-1.384, -0.433] | 1 / 19 / 0 (n/d) | <0.0001 | 0.0004 * |
| invalid plan rate | 20 | 0 | 0 | 0 [0, 0] | 0 / 0 / 20 | 1.0000 | 1.0000 |
| USD per match | 20 | 0 | 0 | 0 [0, 0] | 0 / 0 / 20 | 1.0000 | 1.0000 |

### pinned:induced-generic-expand-soviet-8f797e00 vs selector (20 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 20 | 0.75 | 0.95 | 0.2 [0, 0.4] | 5 / 1 / 14 | 0.2188 | 1.0000 |
| final asset margin | 20 | 2373 | 20180 | 17808 [8343, 28020] | 18 / 2 / 0 | 0.0004 | 0.0040 * |
| value destroyed | 20 | 10730 | 22730 | 12000 [10865, 13070] | 20 / 0 / 0 | <0.0001 | <0.0001 * |
| value lost | 20 | 8745 | 3635 | -5110 [-12260, 1845] | 8 / 12 / 0 | 0.5034 | 1.0000 |
| trade share | 20 | 0.752 | 0.886 | 0.134 [-0.025, 0.291] | 16 / 4 / 0 | 0.0118 | 0.1064 |
| peak army value | 20 | 2705 | 6350 | 3645 [3380, 3860] | 20 / 0 / 0 | <0.0001 | <0.0001 * |
| units built | 20 | 44.7 | 89.95 | 45.25 [32.8, 60.65] | 20 / 0 / 0 | <0.0001 | <0.0001 * |
| production idle fraction | 20 | 0.013 | 0.005 | -0.007 [-0.018, -0.001] | 15 / 5 / 0 | 0.0414 | 0.3311 |
| average credits (float) | 20 | 6665 | 6702 | 36.932 [-167, 224] | 7 / 13 / 0 | 0.2632 | 1.0000 |
| first attack s | 20 | 122 | 122 | 0 [0, 0] | 0 / 0 / 20 (n/d) | 1.0000 | 1.0000 |
| duration s | 20 | 523 | 670 | 147 [-21.1, 323] | 15 / 5 / 0 (n/d) | 0.0414 | 0.3311 |
| activations /10 min | 20 | 2.124 | 1.009 | -1.116 [-1.478, -0.801] | 1 / 19 / 0 (n/d) | <0.0001 | 0.0004 * |
| invalid plan rate | 20 | 0 | 0 | 0 [0, 0] | 0 / 0 / 20 | 1.0000 | 1.0000 |
| USD per match | 20 | 0 | 0 | 0 [0, 0] | 0 / 0 / 20 | 1.0000 | 1.0000 |

### pinned:induced-soviet-rhino-rush-soviet-7d66f1a3 vs selector (20 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 20 | 0.75 | 0.475 | -0.275 [-0.575, 0.05] | 3 / 9 / 8 | 0.1460 | 1.0000 |
| final asset margin | 20 | 2373 | -3845 | -6218 [-18928, 6770] | 8 / 12 / 0 | 0.5034 | 1.0000 |
| value destroyed | 20 | 10730 | 18280 | 7550 [3170, 12610] | 17 / 3 / 0 | 0.0026 | 0.0309 * |
| value lost | 20 | 8745 | 14735 | 5990 [-2905, 14490] | 5 / 15 / 0 | 0.0414 | 0.4139 |
| trade share | 20 | 0.752 | 0.551 | -0.201 [-0.378, -0.017] | 6 / 14 / 0 | 0.1153 | 1.0000 |
| peak army value | 20 | 2705 | 5280 | 2575 [2225, 2885] | 20 / 0 / 0 | <0.0001 | <0.0001 * |
| units built | 20 | 44.7 | 66.3 | 21.6 [12.75, 30.35] | 16 / 4 / 0 | 0.0118 | 0.1300 |
| production idle fraction | 20 | 0.013 | 0.025 | 0.012 [-0.002, 0.033] | 14 / 6 / 0 | 0.1153 | 1.0000 |
| average credits (float) | 20 | 6665 | 8075 | 1410 [524, 2246] | 7 / 13 / 0 | 0.2632 | 1.0000 |
| first attack s | 20 | 122 | 122 | 0 [0, 0] | 0 / 0 / 20 (n/d) | 1.0000 | 1.0000 |
| duration s | 20 | 523 | 1155 | 632 [482, 768] | 18 / 1 / 1 (n/d) | <0.0001 | 0.0010 * |
| activations /10 min | 20 | 2.124 | 1.783 | -0.342 [-0.938, 0.168] | 9 / 11 / 0 (n/d) | 0.8238 | 1.0000 |
| invalid plan rate | 20 | 0 | 0 | 0 [0, 0] | 0 / 0 / 20 | 1.0000 | 1.0000 |
| USD per match | 20 | 0 | 0 | 0 [0, 0] | 0 / 0 / 20 | 1.0000 | 1.0000 |

## Paired differences vs selector

Pairs share opponent, map and seed. Difference = arm − baseline, mean with a 95% seeded bootstrap interval; better/worse/tied count pairs by the metric's direction (n/d: no better direction, counted as higher/lower); p is the exact two-sided sign test over untied pairs, Holm-adjusted over the arm's metrics. `*` marks Holm p < 0.05.

### pinned:generic-expand vs selector (20 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 20 | 0.75 | 0.95 | 0.2 [0, 0.4] | 5 / 1 / 14 | 0.2188 | 1.0000 |
| final asset margin | 20 | 2373 | 20515 | 18143 [8825, 28140] | 18 / 2 / 0 | 0.0004 | 0.0040 * |
| value destroyed | 20 | 10730 | 22965 | 12235 [11350, 13200] | 20 / 0 / 0 | <0.0001 | <0.0001 * |
| value lost | 20 | 8745 | 3840 | -4905 [-12090, 1965] | 8 / 12 / 0 | 0.5034 | 1.0000 |
| trade share | 20 | 0.752 | 0.883 | 0.131 [-0.024, 0.286] | 16 / 4 / 0 | 0.0118 | 0.1064 |
| peak army value | 20 | 2705 | 6110 | 3405 [3145, 3625] | 20 / 0 / 0 | <0.0001 | <0.0001 * |
| units built | 20 | 44.7 | 88.4 | 43.7 [31.8, 58.75] | 20 / 0 / 0 | <0.0001 | <0.0001 * |
| production idle fraction | 20 | 0.013 | 0.005 | -0.007 [-0.018, -0.001] | 15 / 5 / 0 | 0.0414 | 0.3311 |
| average credits (float) | 20 | 6665 | 6673 | 7.97 [-193, 198] | 9 / 11 / 0 | 0.8238 | 1.0000 |
| first attack s | 20 | 122 | 122 | 0 [0, 0] | 0 / 0 / 20 (n/d) | 1.0000 | 1.0000 |
| duration s | 20 | 523 | 664 | 141 [-27.95, 318] | 15 / 5 / 0 (n/d) | 0.0414 | 0.3311 |
| activations /10 min | 20 | 2.124 | 1.194 | -0.931 [-1.384, -0.433] | 1 / 19 / 0 (n/d) | <0.0001 | 0.0004 * |
| invalid plan rate | 20 | 0 | 0 | 0 [0, 0] | 0 / 0 / 20 | 1.0000 | 1.0000 |
| USD per match | 20 | 0 | 0 | 0 [0, 0] | 0 / 0 / 20 | 1.0000 | 1.0000 |

### pinned:induced-generic-expand-soviet-8f797e00 vs selector (20 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 20 | 0.75 | 0.95 | 0.2 [0, 0.4] | 5 / 1 / 14 | 0.2188 | 1.0000 |
| final asset margin | 20 | 2373 | 20180 | 17808 [8343, 28020] | 18 / 2 / 0 | 0.0004 | 0.0040 * |
| value destroyed | 20 | 10730 | 22730 | 12000 [10865, 13070] | 20 / 0 / 0 | <0.0001 | <0.0001 * |
| value lost | 20 | 8745 | 3635 | -5110 [-12260, 1845] | 8 / 12 / 0 | 0.5034 | 1.0000 |
| trade share | 20 | 0.752 | 0.886 | 0.134 [-0.025, 0.291] | 16 / 4 / 0 | 0.0118 | 0.1064 |
| peak army value | 20 | 2705 | 6350 | 3645 [3380, 3860] | 20 / 0 / 0 | <0.0001 | <0.0001 * |
| units built | 20 | 44.7 | 89.95 | 45.25 [32.8, 60.65] | 20 / 0 / 0 | <0.0001 | <0.0001 * |
| production idle fraction | 20 | 0.013 | 0.005 | -0.007 [-0.018, -0.001] | 15 / 5 / 0 | 0.0414 | 0.3311 |
| average credits (float) | 20 | 6665 | 6702 | 36.932 [-167, 224] | 7 / 13 / 0 | 0.2632 | 1.0000 |
| first attack s | 20 | 122 | 122 | 0 [0, 0] | 0 / 0 / 20 (n/d) | 1.0000 | 1.0000 |
| duration s | 20 | 523 | 670 | 147 [-21.1, 323] | 15 / 5 / 0 (n/d) | 0.0414 | 0.3311 |
| activations /10 min | 20 | 2.124 | 1.009 | -1.116 [-1.478, -0.801] | 1 / 19 / 0 (n/d) | <0.0001 | 0.0004 * |
| invalid plan rate | 20 | 0 | 0 | 0 [0, 0] | 0 / 0 / 20 | 1.0000 | 1.0000 |
| USD per match | 20 | 0 | 0 | 0 [0, 0] | 0 / 0 / 20 | 1.0000 | 1.0000 |

### pinned:induced-soviet-rhino-rush-soviet-7d66f1a3 vs selector (20 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 20 | 0.75 | 0.475 | -0.275 [-0.575, 0.05] | 3 / 9 / 8 | 0.1460 | 1.0000 |
| final asset margin | 20 | 2373 | -3845 | -6218 [-18928, 6770] | 8 / 12 / 0 | 0.5034 | 1.0000 |
| value destroyed | 20 | 10730 | 18280 | 7550 [3170, 12610] | 17 / 3 / 0 | 0.0026 | 0.0309 * |
| value lost | 20 | 8745 | 14735 | 5990 [-2905, 14490] | 5 / 15 / 0 | 0.0414 | 0.4139 |
| trade share | 20 | 0.752 | 0.551 | -0.201 [-0.378, -0.017] | 6 / 14 / 0 | 0.1153 | 1.0000 |
| peak army value | 20 | 2705 | 5280 | 2575 [2225, 2885] | 20 / 0 / 0 | <0.0001 | <0.0001 * |
| units built | 20 | 44.7 | 66.3 | 21.6 [12.75, 30.35] | 16 / 4 / 0 | 0.0118 | 0.1300 |
| production idle fraction | 20 | 0.013 | 0.025 | 0.012 [-0.002, 0.033] | 14 / 6 / 0 | 0.1153 | 1.0000 |
| average credits (float) | 20 | 6665 | 8075 | 1410 [524, 2246] | 7 / 13 / 0 | 0.2632 | 1.0000 |
| first attack s | 20 | 122 | 122 | 0 [0, 0] | 0 / 0 / 20 (n/d) | 1.0000 | 1.0000 |
| duration s | 20 | 523 | 1155 | 632 [482, 768] | 18 / 1 / 1 (n/d) | <0.0001 | 0.0010 * |
| activations /10 min | 20 | 2.124 | 1.783 | -0.342 [-0.938, 0.168] | 9 / 11 / 0 (n/d) | 0.8238 | 1.0000 |
| invalid plan rate | 20 | 0 | 0 | 0 [0, 0] | 0 / 0 / 20 | 1.0000 | 1.0000 |
| USD per match | 20 | 0 | 0 | 0 [0, 0] | 0 / 0 / 20 | 1.0000 | 1.0000 |

## Economy, production and combat (arm side, per-match averages)

Production idle: seconds with a factory, nothing queued and credits for the cheapest item, over match seconds. Trade efficiency: enemy value destroyed in combat / own value lost in combat (pooled over matches).

Time to first attack: first second a combat unit stood in a region holding an enemy structure (mean over matches where it happened; count in brackets).

| Arm | Duration s | Units built | Buildings built | Peak army value | Idle fraction | Avg credits | Final assets (arm / opp) | Trade efficiency | First attack s |
|---|---|---|---|---|---|---|---|---|---|
| pinned:generic-expand | 664 | 88.4 | 7.2 | 6110 | 0.005 | 6673 | 38120 / 17605 | 5.98 (459300/76800) | 122 (20/20) |
| pinned:induced-generic-expand-soviet-8f797e00 | 670 | 90.0 | 7.0 | 6350 | 0.005 | 6702 | 38475 / 18295 | 6.25 (454600/72700) | 122 (20/20) |
| pinned:induced-soviet-rhino-rush-soviet-7d66f1a3 | 1155 | 66.3 | 12.5 | 5280 | 0.025 | 8075 | 30580 / 34425 | 1.24 (365600/294700) | 122 (20/20) |
| selector | 523 | 44.7 | 6.0 | 2705 | 0.013 | 6665 | 15890 / 13518 | 1.23 (214600/174900) | 122 (20/20) |

## Strategy layer (arm side)

Proposals, invalid plans and lateness are the arm's primary strategist's: rejected / proposals; seconds from a proposal's snapshot to its validation; late-discard count / proposals. Fallback and emergency proposals (immediate and always valid) are counted apart so they cannot flatter the strategist under test. Shadow columns are the shadow strategist's own lateness and would-be invalid plans. Churn: activations and posture flips per 10 game minutes.

Shadow agreement: shadow proposals naming the same playbook as the primary proposal for the same request / shadow proposals whose request got a primary proposal.

| Arm | Proposals | Invalid plans | Mean lateness s | Late-discarded | Fallback proposals | Failed requests | Shadow proposals | Shadow invalid | Shadow mean lateness s | Shadow agreement | Activations /10 min | Posture flips /10 min |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| pinned:generic-expand | 690 | 0/690 (0.000) | 0.00 | 0/690 (0.000) | 6 | 0 | 0 | n/a | n/a | n/a | 1.36 | 0.36 |
| pinned:induced-generic-expand-soviet-8f797e00 | 694 | 0/694 (0.000) | 0.00 | 0/694 (0.000) | 3 | 0 | 0 | n/a | n/a | n/a | 1.03 | 0.04 |
| pinned:induced-soviet-rhino-rush-soviet-7d66f1a3 | 1242 | 0/1242 (0.000) | 0.00 | 0/1242 (0.000) | 33 | 0 | 0 | n/a | n/a | n/a | 1.79 | 1.01 |
| selector | 565 | 0/565 (0.000) | 0.00 | 0/565 (0.000) | 0 | 0 | 0 | n/a | n/a | n/a | 2.52 | 1.03 |

## Command gate and simulator rejections (arm side, totals)

- **pinned:generic-expand**: 0 dropped by the gate (none); 63 commands rejected by the simulator.
- **pinned:induced-generic-expand-soviet-8f797e00**: 0 dropped by the gate (none); 62 commands rejected by the simulator.
- **pinned:induced-soviet-rhino-rush-soviet-7d66f1a3**: 0 dropped by the gate (none); 242 commands rejected by the simulator.
- **selector**: 0 dropped by the gate (none); 37 commands rejected by the simulator.

## Inference cost (arm side)

- **pinned:generic-expand** (no model): 0 in / 0 out tokens and $0.0000 per match, of which $0.0000 on failed requests.
- **pinned:induced-generic-expand-soviet-8f797e00** (no model): 0 in / 0 out tokens and $0.0000 per match, of which $0.0000 on failed requests.
- **pinned:induced-soviet-rhino-rush-soviet-7d66f1a3** (no model): 0 in / 0 out tokens and $0.0000 per match, of which $0.0000 on failed requests.
- **selector** (no model): 0 in / 0 out tokens and $0.0000 per match, of which $0.0000 on failed requests.

## Hidden-information leakage

- **pinned:generic-expand**: 0 validator `fog.*` rejections.
- **pinned:induced-generic-expand-soviet-8f797e00**: 0 validator `fog.*` rejections.
- **pinned:induced-soviet-rhino-rush-soviet-7d66f1a3**: 0 validator `fog.*` rejections.
- **selector**: 0 validator `fog.*` rejections.

Probe: two lockstep simulations, hidden state of one perturbed (`SimLeakageProbe`: enemy credits and queue, wounded hidden enemies, a hidden unit in an unseen region, one announced there through the event path, and one just across a border inside the arm's weapon reach), strategist-context hash compared on every following frame until the window closes or the objects the arm can see first differ. A differing frame is one where the context changed while everything visible was still identical. Fog-violation frames are arm frames, over the whole run of both simulations, that carried an enemy object or event from a region the arm did not see: a per-frame check that finds leaks the perturbation does not exercise.

| Arm | Map | Seed | Perturbed at s | Lockstep before | Compared s | Differing frames | Fog-violation frames | Note |
|---|---|---|---|---|---|---|---|---|
| pinned:induced-generic-expand-soviet-8f797e00 | open-steppe | 1 | 90 | yes | 45 | 0/671 | 0 | stopped at 135 s: the arm saw a legitimate difference |
| pinned:induced-generic-expand-soviet-8f797e00 | open-steppe | 1 | 240 | yes | 0 | 0/1 | 0 | stopped at 240 s: the arm saw a legitimate difference |
| pinned:induced-soviet-rhino-rush-soviet-7d66f1a3 | open-steppe | 1 | 90 | yes | 45 | 0/671 | 0 | stopped at 135 s: the arm saw a legitimate difference |
| pinned:induced-soviet-rhino-rush-soviet-7d66f1a3 | open-steppe | 1 | 240 | yes | 0 | 0/1 | 0 | stopped at 240 s: the arm saw a legitimate difference |
| pinned:generic-expand | open-steppe | 1 | 90 | yes | 45 | 0/671 | 0 | stopped at 135 s: the arm saw a legitimate difference |
| pinned:generic-expand | open-steppe | 1 | 240 | yes | 0 | 0/1 | 0 | stopped at 240 s: the arm saw a legitimate difference |
| selector | open-steppe | 1 | 90 | yes | 45 | 0/671 | 0 | stopped at 135 s: the arm saw a legitimate difference |
| selector | open-steppe | 1 | 240 | yes | 0 | 0/1 | 0 | stopped at 240 s: the arm saw a legitimate difference |

