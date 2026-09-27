# Bindery region sim arena report

Results are from the bindery region simulator with the approximate `bindery-sim-approx` rules, not retail RA2; they are directional.
The arm is a full `BotRuntime`. Opponents named `ai-*` are the independent scripted AI (`Bindery.Ra2.Bot.Sim.Opponents`, no shared planner code; `:easy`/`:medium`/`:hard`, default hard); the other opponents are pinned-playbook styles running a frozen copy of the bot's stack as of commit 7f3e2c7 (`Bindery.Ra2.Bot.Baseline`), a stationary benchmark; `live-<style>` runs a pinned style on the live stack. The arm plays Allied on odd seeds and Soviet on even seeds. Match limit 1200 s (a timeout is won on final asset value).
Opponents are split as well as maps. Held-out opponents (`ai-horde`, `ai-armor`, independent scripted styles) are never used for the selector's default playbook, bandit learning, distillation datasets or tuning; see "Held-out opponents" below. They are not untouched: their difficulty and the contested benchmark's handicaps were calibrated against the selector's win rate on all five maps, held-out maps included, so the selector's own held-out win rate is near one half partly by construction. Every other opponent is a training opponent: the selector's default playbook was chosen from a style-versus-style matrix against the pinned-playbook styles (training maps only), so win rates against training opponents are partly in-sample.
Benchmark: contested (opponent income ×1, Allied income ×1, Allied starting credits 20000, opponent starting credits default, combat noise ±0.25).
Labels in this run: `distilled-from:llm arm in this run (training maps) (13992 examples)`, `llm-fake`, `oracle`.

Matches: 8960

## Win rate (arm × split)

Distinct games drop repeats of an identical game (same arm faction, decision log and outcome on the same map): against a differently named opponent whose style had not diverged when the match ended, or on another seed that changed nothing; the distinct interval is the one to read. The arm plays Allied on odd seeds and Soviet on even seeds, and starts west on seeds 1-2, 5-6, ... and east on 3-4, 7-8, ...; the fixture is asymmetric, so the faction and side columns show the mix behind each rate.

| Arm | Split | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) | Distinct games | Distinct wins | 95% interval, distinct | As Allied | As Soviet | As west | As east | Eliminations won | Timeouts |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| bandit | heldout | 375 | 73 | 0 | 448 | 0.837 | [0.800, 0.868] | 381 | 308 | [0.766, 0.845] | 162/224 | 213/224 | 188/224 | 187/224 | 356 | 51 |
| bandit | training | 571 | 101 | 0 | 672 | 0.850 | [0.821, 0.875] | 575 | 474 | [0.791, 0.853] | 265/336 | 306/336 | 285/336 | 286/336 | 514 | 103 |
| distilled | heldout | 317 | 131 | 0 | 448 | 0.708 | [0.664, 0.748] | 293 | 170 | [0.523, 0.635] | 131/224 | 186/224 | 160/224 | 157/224 | 289 | 51 |
| distilled | training | 475 | 197 | 0 | 672 | 0.707 | [0.671, 0.740] | 457 | 272 | [0.550, 0.639] | 237/336 | 238/336 | 239/336 | 236/336 | 406 | 158 |
| llm | heldout | 276 | 172 | 0 | 448 | 0.616 | [0.570, 0.660] | 314 | 151 | [0.426, 0.536] | 103/224 | 173/224 | 143/224 | 133/224 | 244 | 61 |
| llm | training | 452 | 219 | 1 | 672 | 0.673 | [0.636, 0.707] | 470 | 259 | [0.506, 0.595] | 211/336 | 241/336 | 230/336 | 222/336 | 356 | 184 |
| llm+fast | heldout | 287 | 161 | 0 | 448 | 0.641 | [0.595, 0.684] | 315 | 161 | [0.456, 0.566] | 106/224 | 181/224 | 146/224 | 141/224 | 249 | 76 |
| llm+fast | training | 451 | 217 | 4 | 672 | 0.671 | [0.635, 0.706] | 471 | 261 | [0.509, 0.598] | 215/336 | 236/336 | 228/336 | 223/336 | 359 | 176 |
| llm-oracle | heldout | 308 | 140 | 0 | 448 | 0.688 | [0.643, 0.729] | 309 | 174 | [0.507, 0.617] | 144/224 | 164/224 | 157/224 | 151/224 | 242 | 131 |
| llm-oracle | training | 471 | 201 | 0 | 672 | 0.701 | [0.665, 0.734] | 471 | 273 | [0.535, 0.623] | 232/336 | 239/336 | 233/336 | 238/336 | 382 | 203 |
| llm-shadow | heldout | 388 | 60 | 0 | 448 | 0.866 | [0.831, 0.895] | 300 | 248 | [0.780, 0.865] | 174/224 | 214/224 | 194/224 | 194/224 | 363 | 39 |
| llm-shadow | training | 580 | 92 | 0 | 672 | 0.863 | [0.835, 0.887] | 444 | 364 | [0.781, 0.853] | 275/336 | 305/336 | 290/336 | 290/336 | 499 | 113 |
| selector | heldout | 388 | 60 | 0 | 448 | 0.866 | [0.831, 0.895] | 299 | 247 | [0.779, 0.865] | 174/224 | 214/224 | 194/224 | 194/224 | 363 | 39 |
| selector | training | 580 | 92 | 0 | 672 | 0.863 | [0.835, 0.887] | 443 | 363 | [0.781, 0.852] | 275/336 | 305/336 | 290/336 | 290/336 | 499 | 113 |
| selector-oracle | heldout | 393 | 55 | 0 | 448 | 0.877 | [0.844, 0.904] | 296 | 244 | [0.777, 0.863] | 176/224 | 217/224 | 195/224 | 198/224 | 372 | 36 |
| selector-oracle | training | 609 | 61 | 2 | 672 | 0.906 | [0.882, 0.926] | 446 | 387 | [0.833, 0.896] | 308/336 | 301/336 | 303/336 | 306/336 | 528 | 124 |

Benchmark check: the baseline `selector` scored 0.864 over 1120 matches, outside the 30–70% band: the benchmark is saturated and win-rate comparisons carry little information (use `--benchmark contested`).

## Win rate by opponent style

| Arm | Split | ai-air:easy | ai-air:hard | ai-air:medium | ai-armor:easy | ai-armor:hard | ai-armor:medium | ai-balanced:easy | ai-balanced:hard | ai-balanced:medium | ai-horde:easy | ai-horde:hard | ai-horde:medium | ai-rush:easy | ai-rush:hard | ai-rush:medium | ai-turtle:easy | ai-turtle:hard | ai-turtle:medium | balanced | harass | live-balanced | live-harass | live-rush | live-tech | live-turtle | rush | tech | turtle |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| bandit | heldout | 16/16 | 16/16 | 16/16 | 12/16 | 12/16 | 12/16 | 16/16 | 16/16 | 16/16 | 13/16 | 8/16 | 16/16 | 16/16 | 15/16 | 16/16 | 13/16 | 12/16 | 13/16 | 16/16 | 16/16 | 8/16 | 8/16 | 8/16 | 8/16 | 9/16 | 16/16 | 16/16 | 16/16 |
| bandit | training | 24/24 | 24/24 | 24/24 | 22/24 | 10/24 | 15/24 | 24/24 | 24/24 | 24/24 | 19/24 | 15/24 | 17/24 | 23/24 | 23/24 | 24/24 | 23/24 | 24/24 | 24/24 | 24/24 | 24/24 | 10/24 | 9/24 | 18/24 | 18/24 | 13/24 | 24/24 | 24/24 | 24/24 |
| distilled | heldout | 16/16 | 16/16 | 16/16 | 1/16 | 0/16 | 0/16 | 16/16 | 16/16 | 16/16 | 10/16 | 5/16 | 7/16 | 16/16 | 15/16 | 16/16 | 10/16 | 8/16 | 11/16 | 16/16 | 16/16 | 8/16 | 7/16 | 8/16 | 12/16 | 7/16 | 16/16 | 16/16 | 16/16 |
| distilled | training | 24/24 | 24/24 | 24/24 | 4/24 | 5/24 | 2/24 | 24/24 | 24/24 | 24/24 | 15/24 | 6/24 | 13/24 | 24/24 | 21/24 | 24/24 | 13/24 | 14/24 | 16/24 | 24/24 | 24/24 | 8/24 | 9/24 | 14/24 | 15/24 | 8/24 | 24/24 | 24/24 | 24/24 |
| llm | heldout | 16/16 | 16/16 | 16/16 | 1/16 | 0/16 | 2/16 | 15/16 | 10/16 | 15/16 | 8/16 | 6/16 | 4/16 | 8/16 | 11/16 | 8/16 | 6/16 | 8/16 | 2/16 | 16/16 | 16/16 | 8/16 | 7/16 | 9/16 | 12/16 | 8/16 | 16/16 | 16/16 | 16/16 |
| llm | training | 24/24 | 24/24 | 24/24 | 3/24 | 7/24 | 8/24 | 24/24 | 24/24 | 24/24 | 17/24 | 11/24 | 14/24 | 15/24 | 9/24 | 12/24 | 11/24 | 14/24 | 13/24 | 24/24 | 24/24 | 12/24 | 7/24 | 13/24 | 16/24 | 6/24 | 24/24 | 24/24 | 24/24 |
| llm+fast | heldout | 16/16 | 16/16 | 16/16 | 2/16 | 0/16 | 5/16 | 15/16 | 10/16 | 15/16 | 8/16 | 6/16 | 5/16 | 8/16 | 7/16 | 8/16 | 6/16 | 9/16 | 8/16 | 16/16 | 16/16 | 9/16 | 8/16 | 9/16 | 13/16 | 8/16 | 16/16 | 16/16 | 16/16 |
| llm+fast | training | 24/24 | 24/24 | 24/24 | 7/24 | 5/24 | 9/24 | 24/24 | 24/24 | 24/24 | 17/24 | 16/24 | 5/24 | 15/24 | 13/24 | 12/24 | 11/24 | 12/24 | 11/24 | 24/24 | 24/24 | 12/24 | 7/24 | 13/24 | 16/24 | 6/24 | 24/24 | 24/24 | 24/24 |
| llm-oracle | heldout | 16/16 | 16/16 | 16/16 | 4/16 | 9/16 | 12/16 | 16/16 | 16/16 | 16/16 | 9/16 | 4/16 | 7/16 | 16/16 | 7/16 | 8/16 | 6/16 | 4/16 | 3/16 | 16/16 | 16/16 | 7/16 | 7/16 | 9/16 | 11/16 | 9/16 | 16/16 | 16/16 | 16/16 |
| llm-oracle | training | 24/24 | 24/24 | 24/24 | 11/24 | 9/24 | 17/24 | 24/24 | 24/24 | 24/24 | 13/24 | 14/24 | 10/24 | 23/24 | 13/24 | 18/24 | 5/24 | 5/24 | 4/24 | 24/24 | 24/24 | 15/24 | 15/24 | 12/24 | 17/24 | 6/24 | 24/24 | 24/24 | 24/24 |
| llm-shadow | heldout | 16/16 | 16/16 | 16/16 | 12/16 | 11/16 | 14/16 | 16/16 | 16/16 | 16/16 | 13/16 | 10/16 | 14/16 | 16/16 | 16/16 | 16/16 | 14/16 | 16/16 | 15/16 | 16/16 | 16/16 | 8/16 | 8/16 | 8/16 | 12/16 | 9/16 | 16/16 | 16/16 | 16/16 |
| llm-shadow | training | 24/24 | 24/24 | 24/24 | 22/24 | 14/24 | 13/24 | 24/24 | 24/24 | 24/24 | 22/24 | 13/24 | 16/24 | 24/24 | 21/24 | 24/24 | 24/24 | 24/24 | 24/24 | 24/24 | 24/24 | 12/24 | 12/24 | 18/24 | 17/24 | 16/24 | 24/24 | 24/24 | 24/24 |
| selector | heldout | 16/16 | 16/16 | 16/16 | 12/16 | 11/16 | 14/16 | 16/16 | 16/16 | 16/16 | 13/16 | 10/16 | 14/16 | 16/16 | 16/16 | 16/16 | 14/16 | 16/16 | 15/16 | 16/16 | 16/16 | 8/16 | 8/16 | 8/16 | 12/16 | 9/16 | 16/16 | 16/16 | 16/16 |
| selector | training | 24/24 | 24/24 | 24/24 | 22/24 | 14/24 | 13/24 | 24/24 | 24/24 | 24/24 | 22/24 | 13/24 | 16/24 | 24/24 | 21/24 | 24/24 | 24/24 | 24/24 | 24/24 | 24/24 | 24/24 | 12/24 | 12/24 | 18/24 | 17/24 | 16/24 | 24/24 | 24/24 | 24/24 |
| selector-oracle | heldout | 16/16 | 16/16 | 16/16 | 12/16 | 8/16 | 12/16 | 16/16 | 16/16 | 16/16 | 12/16 | 13/16 | 11/16 | 16/16 | 16/16 | 16/16 | 14/16 | 13/16 | 14/16 | 16/16 | 16/16 | 11/16 | 11/16 | 13/16 | 15/16 | 10/16 | 16/16 | 16/16 | 16/16 |
| selector-oracle | training | 24/24 | 24/24 | 24/24 | 19/24 | 11/24 | 20/24 | 24/24 | 24/24 | 24/24 | 24/24 | 16/24 | 17/24 | 24/24 | 24/24 | 24/24 | 24/24 | 24/24 | 24/24 | 24/24 | 24/24 | 15/24 | 19/24 | 19/24 | 22/24 | 19/24 | 24/24 | 24/24 | 24/24 |

Identical games (same arm decision log and outcome against differently named opponents; counted once in the distinct columns and paired tables):

- bandit: ai-air:easy = ai-air:hard = ai-air:medium on 20 map × seed cells
- bandit: ai-balanced:hard = ai-balanced:medium on 19 map × seed cells
- bandit: ai-turtle:hard = ai-turtle:medium on 20 map × seed cells
- bandit: balanced = harass = rush = tech = turtle on 19 map × seed cells
- bandit: balanced = rush = tech = turtle on 1 map × seed cell
- distilled: ai-air:easy = ai-air:hard = ai-air:medium on 39 map × seed cells
- distilled: ai-balanced:easy = ai-balanced:medium on 20 map × seed cells
- distilled: ai-balanced:hard = ai-balanced:medium on 20 map × seed cells
- distilled: ai-balanced:hard = ai-rush:easy on 20 map × seed cells
- distilled: ai-horde:hard = ai-horde:medium on 4 map × seed cells
- distilled: ai-turtle:easy = ai-turtle:medium on 4 map × seed cells
- distilled: ai-turtle:hard = ai-turtle:medium on 10 map × seed cells
- distilled: balanced = harass = rush = tech = turtle on 39 map × seed cells
- distilled: live-balanced = live-harass on 20 map × seed cells
- distilled: live-balanced = live-rush on 2 map × seed cells
- distilled: live-balanced = live-rush = live-tech on 14 map × seed cells
- llm: ai-air:easy = ai-air:hard = ai-air:medium on 40 map × seed cells
- llm: ai-balanced:easy = ai-balanced:medium on 19 map × seed cells
- llm: ai-balanced:hard = ai-balanced:medium on 20 map × seed cells
- llm: ai-balanced:hard = ai-rush:easy on 2 map × seed cells
- llm: ai-horde:hard = ai-horde:medium on 2 map × seed cells
- llm: ai-horde:hard = ai-horde:medium = ai-rush:hard on 1 map × seed cell
- llm: ai-turtle:easy = ai-turtle:medium on 1 map × seed cell
- llm: ai-turtle:hard = ai-turtle:medium on 2 map × seed cells
- llm: balanced = harass = rush = tech = turtle on 40 map × seed cells
- llm: live-balanced = live-harass on 16 map × seed cells
- llm: live-balanced = live-harass = live-rush = live-tech on 1 map × seed cell
- llm: live-balanced = live-harass = live-tech on 1 map × seed cell
- llm: live-balanced = live-rush on 1 map × seed cell
- llm: live-balanced = live-rush = live-tech on 13 map × seed cells
- llm+fast: ai-air:easy = ai-air:hard = ai-air:medium on 40 map × seed cells
- llm+fast: ai-balanced:easy = ai-balanced:medium on 19 map × seed cells
- llm+fast: ai-balanced:hard = ai-balanced:medium on 20 map × seed cells
- llm+fast: ai-balanced:hard = ai-rush:easy on 2 map × seed cells
- llm+fast: ai-horde:hard = ai-horde:medium on 2 map × seed cells
- llm+fast: ai-horde:hard = ai-horde:medium = ai-rush:hard on 1 map × seed cell
- llm+fast: ai-turtle:easy = ai-turtle:medium on 1 map × seed cell
- llm+fast: ai-turtle:hard = ai-turtle:medium on 3 map × seed cells
- llm+fast: balanced = harass = rush = tech = turtle on 40 map × seed cells
- llm+fast: live-balanced = live-harass on 16 map × seed cells
- llm+fast: live-balanced = live-harass = live-rush = live-tech on 1 map × seed cell
- llm+fast: live-balanced = live-rush = live-tech on 13 map × seed cells
- llm-oracle: ai-air:easy = ai-air:hard = ai-air:medium on 37 map × seed cells
- llm-oracle: ai-balanced:easy = ai-balanced:medium on 20 map × seed cells
- llm-oracle: ai-balanced:hard = ai-balanced:medium on 20 map × seed cells
- llm-oracle: ai-balanced:hard = ai-rush:easy on 17 map × seed cells
- llm-oracle: ai-turtle:easy = ai-turtle:medium on 2 map × seed cells
- llm-oracle: balanced = harass = rush = tech = turtle on 39 map × seed cells
- llm-oracle: live-balanced = live-harass on 6 map × seed cells
- llm-oracle: live-balanced = live-harass = live-rush = live-tech on 7 map × seed cells
- llm-oracle: live-balanced = live-rush = live-tech on 5 map × seed cells
- llm-shadow: ai-air:easy = ai-air:hard = ai-air:medium on 40 map × seed cells
- llm-shadow: ai-balanced:easy = ai-balanced:medium on 20 map × seed cells
- llm-shadow: ai-balanced:hard = ai-balanced:medium on 20 map × seed cells
- llm-shadow: ai-balanced:hard = ai-rush:easy on 20 map × seed cells
- llm-shadow: ai-turtle:easy = ai-turtle:medium on 15 map × seed cells
- llm-shadow: ai-turtle:hard = ai-turtle:medium on 20 map × seed cells
- llm-shadow: balanced = harass = rush = tech = turtle on 40 map × seed cells
- llm-shadow: live-balanced = live-harass on 20 map × seed cells
- llm-shadow: live-balanced = live-rush on 19 map × seed cells
- llm-shadow: live-balanced = live-rush = live-tech on 1 map × seed cell
- selector: ai-air:easy = ai-air:hard = ai-air:medium on 39 map × seed cells
- selector: ai-balanced:easy = ai-balanced:medium on 20 map × seed cells
- selector: ai-balanced:hard = ai-balanced:medium on 20 map × seed cells
- selector: ai-balanced:hard = ai-rush:easy on 20 map × seed cells
- selector: ai-turtle:easy = ai-turtle:medium on 15 map × seed cells
- selector: ai-turtle:hard = ai-turtle:medium on 20 map × seed cells
- selector: balanced = harass = rush = tech = turtle on 39 map × seed cells
- selector: live-balanced = live-harass on 20 map × seed cells
- selector: live-balanced = live-rush on 19 map × seed cells
- selector: live-balanced = live-rush = live-tech on 1 map × seed cell
- selector-oracle: ai-air:easy = ai-air:hard = ai-air:medium on 35 map × seed cells
- selector-oracle: ai-balanced:easy = ai-balanced:medium on 20 map × seed cells
- selector-oracle: ai-balanced:hard = ai-balanced:medium on 20 map × seed cells
- selector-oracle: ai-balanced:hard = ai-rush:easy on 19 map × seed cells
- selector-oracle: ai-turtle:easy = ai-turtle:medium on 13 map × seed cells
- selector-oracle: ai-turtle:hard = ai-turtle:medium on 20 map × seed cells
- selector-oracle: balanced = harass = rush = tech = turtle on 37 map × seed cells
- selector-oracle: live-balanced = live-harass on 13 map × seed cells
- selector-oracle: live-balanced = live-rush on 13 map × seed cells
- selector-oracle: live-balanced = live-rush = live-tech on 3 map × seed cells

## Held-out opponents

Opponent split × map split. Held-out opponents never inform the selector's defaults, bandit learning (the arena abandons the bandit's episode instead of crediting it, as it does on held-out maps), distillation datasets or teacher runs, or tuning (the tuner refuses them); their difficulty was calibrated against the selector on all maps, so read the selector's own held-out rate as partly calibrated and other arms' rates relative to it.

| Arm | Opponents | Maps | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) |
|---|---|---|---|---|---|---|---|---|
| bandit | heldout | heldout | 73 | 23 | 0 | 96 | 0.760 | [0.666, 0.835] |
| bandit | heldout | training | 98 | 46 | 0 | 144 | 0.681 | [0.601, 0.751] |
| bandit | training | heldout | 302 | 50 | 0 | 352 | 0.858 | [0.818, 0.891] |
| bandit | training | training | 473 | 55 | 0 | 528 | 0.896 | [0.867, 0.919] |
| distilled | heldout | heldout | 23 | 73 | 0 | 96 | 0.240 | [0.165, 0.334] |
| distilled | heldout | training | 45 | 99 | 0 | 144 | 0.313 | [0.242, 0.392] |
| distilled | training | heldout | 294 | 58 | 0 | 352 | 0.835 | [0.793, 0.870] |
| distilled | training | training | 430 | 98 | 0 | 528 | 0.814 | [0.779, 0.845] |
| llm | heldout | heldout | 21 | 75 | 0 | 96 | 0.219 | [0.148, 0.311] |
| llm | heldout | training | 60 | 84 | 0 | 144 | 0.417 | [0.339, 0.498] |
| llm | training | heldout | 255 | 97 | 0 | 352 | 0.724 | [0.676, 0.768] |
| llm | training | training | 392 | 135 | 1 | 528 | 0.742 | [0.703, 0.778] |
| llm+fast | heldout | heldout | 26 | 70 | 0 | 96 | 0.271 | [0.192, 0.367] |
| llm+fast | heldout | training | 59 | 84 | 1 | 144 | 0.410 | [0.333, 0.491] |
| llm+fast | training | heldout | 261 | 91 | 0 | 352 | 0.741 | [0.693, 0.784] |
| llm+fast | training | training | 392 | 133 | 3 | 528 | 0.742 | [0.703, 0.778] |
| llm-oracle | heldout | heldout | 45 | 51 | 0 | 96 | 0.469 | [0.372, 0.568] |
| llm-oracle | heldout | training | 74 | 70 | 0 | 144 | 0.514 | [0.433, 0.594] |
| llm-oracle | training | heldout | 263 | 89 | 0 | 352 | 0.747 | [0.699, 0.790] |
| llm-oracle | training | training | 397 | 131 | 0 | 528 | 0.752 | [0.713, 0.787] |
| llm-shadow | heldout | heldout | 74 | 22 | 0 | 96 | 0.771 | [0.677, 0.844] |
| llm-shadow | heldout | training | 100 | 44 | 0 | 144 | 0.694 | [0.615, 0.764] |
| llm-shadow | training | heldout | 314 | 38 | 0 | 352 | 0.892 | [0.855, 0.920] |
| llm-shadow | training | training | 480 | 48 | 0 | 528 | 0.909 | [0.882, 0.931] |
| selector | heldout | heldout | 74 | 22 | 0 | 96 | 0.771 | [0.677, 0.844] |
| selector | heldout | training | 100 | 44 | 0 | 144 | 0.694 | [0.615, 0.764] |
| selector | training | heldout | 314 | 38 | 0 | 352 | 0.892 | [0.855, 0.920] |
| selector | training | training | 480 | 48 | 0 | 528 | 0.909 | [0.882, 0.931] |
| selector-oracle | heldout | heldout | 68 | 28 | 0 | 96 | 0.708 | [0.611, 0.790] |
| selector-oracle | heldout | training | 107 | 35 | 2 | 144 | 0.743 | [0.666, 0.807] |
| selector-oracle | training | heldout | 325 | 27 | 0 | 352 | 0.923 | [0.891, 0.947] |
| selector-oracle | training | training | 502 | 26 | 0 | 528 | 0.951 | [0.929, 0.966] |
| bandit | heldout | all | 171 | 69 | 0 | 240 | 0.713 | [0.652, 0.766] |
| distilled | heldout | all | 68 | 172 | 0 | 240 | 0.283 | [0.230, 0.343] |
| llm | heldout | all | 81 | 159 | 0 | 240 | 0.338 | [0.281, 0.399] |
| llm+fast | heldout | all | 85 | 154 | 1 | 240 | 0.354 | [0.296, 0.417] |
| llm-oracle | heldout | all | 119 | 121 | 0 | 240 | 0.496 | [0.433, 0.559] |
| llm-shadow | heldout | all | 174 | 66 | 0 | 240 | 0.725 | [0.665, 0.778] |
| selector | heldout | all | 174 | 66 | 0 | 240 | 0.725 | [0.665, 0.778] |
| selector-oracle | heldout | all | 175 | 63 | 2 | 240 | 0.729 | [0.670, 0.781] |

### Paired differences vs selector, held-out opponents only

Pairs share opponent, map and seed. Difference = arm − baseline, mean with a 95% seeded bootstrap interval; better/worse/tied count pairs by the metric's direction (n/d: no better direction, counted as higher/lower); p is the exact two-sided sign test over untied pairs, Holm-adjusted over the arm's metrics. `*` marks Holm p < 0.05.

### bandit vs selector (240 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 240 | 0.725 | 0.713 | -0.013 [-0.063, 0.042] | 18 / 21 / 201 | 0.7493 | 1.0000 |
| final asset margin | 240 | 3704 | 5395 | 1691 [-267, 3870] | 70 / 117 / 53 | 0.0007 | 0.0080 * |
| value destroyed | 240 | 12670 | 14063 | 1392 [670, 2195] | 107 / 69 / 64 | 0.0051 | 0.0514 |
| value lost | 240 | 15314 | 15069 | -245 [-1678, 1084] | 80 / 101 / 59 | 0.1369 | 0.6845 |
| trade share | 240 | 0.569 | 0.609 | 0.04 [0.015, 0.066] | 108 / 78 / 54 | 0.0332 | 0.2655 |
| peak army value | 240 | 3618 | 3854 | 236 [112, 373] | 100 / 54 / 86 | 0.0003 | 0.0031 * |
| units built | 240 | 64.308 | 74.488 | 10.179 [7.196, 13.15] | 113 / 40 / 87 | <0.0001 | <0.0001 * |
| production idle fraction | 240 | 0.01 | 0.006 | -0.004 [-0.008, -0.001] | 66 / 95 / 79 | 0.0270 | 0.2432 |
| average credits (float) | 240 | 10345 | 10116 | -228 [-382, -77.031] | 124 / 65 / 51 | <0.0001 | 0.0003 * |
| first attack s | 240 | 158 | 158 | 0 [0, 0] | 0 / 0 / 240 (n/d) | 1.0000 | 1.0000 |
| duration s | 240 | 827 | 794 | -33.492 [-64.529, -4.271] | 50 / 71 / 119 (n/d) | 0.0686 | 0.4116 |
| activations /10 min | 240 | 2.912 | 6.246 | 3.334 [2.614, 4] | 122 / 91 / 27 (n/d) | 0.0396 | 0.2770 |
| invalid plan rate | 240 | 0 | 0 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| USD per match | 240 | 0 | 0 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |

### distilled vs selector (240 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 240 | 0.725 | 0.283 | -0.442 [-0.517, -0.371] | 11 / 117 / 112 | <0.0001 | <0.0001 * |
| final asset margin | 240 | 3704 | -15756 | -19460 [-22723, -16259] | 47 / 193 / 0 | <0.0001 | <0.0001 * |
| value destroyed | 240 | 12670 | 8792 | -3878 [-4903, -2786] | 62 / 178 / 0 | <0.0001 | <0.0001 * |
| value lost | 240 | 15314 | 25660 | 10346 [7965, 12570] | 86 / 149 / 5 | <0.0001 | 0.0003 * |
| trade share | 240 | 0.569 | 0.325 | -0.244 [-0.286, -0.199] | 65 / 175 / 0 | <0.0001 | <0.0001 * |
| peak army value | 240 | 3618 | 3126 | -491 [-692, -299] | 89 / 140 / 11 | 0.0009 | 0.0055 * |
| units built | 240 | 64.308 | 58.104 | -6.204 [-10.533, -2.179] | 69 / 170 / 1 | <0.0001 | <0.0001 * |
| production idle fraction | 240 | 0.01 | 0.008 | -0.002 [-0.006, 0.003] | 122 / 96 / 22 | 0.0902 | 0.3607 |
| average credits (float) | 240 | 10345 | 11727 | 1382 [1064, 1720] | 74 / 166 / 0 | <0.0001 | <0.0001 * |
| first attack s | 240 | 158 | 158 | 0 [0, 0] | 0 / 0 / 240 (n/d) | 1.0000 | 1.0000 |
| duration s | 240 | 827 | 982 | 155 [98.65, 207] | 104 / 89 / 47 (n/d) | 0.3136 | 0.9407 |
| activations /10 min | 240 | 2.912 | 4.038 | 1.126 [0.822, 1.448] | 172 / 62 / 6 (n/d) | <0.0001 | <0.0001 * |
| invalid plan rate | 240 | 0 | 0 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| USD per match | 240 | 0 | 0.003 | 0.003 [0.001, 0.005] | 0 / 10 / 230 | 0.0020 | 0.0098 * |

### llm vs selector (240 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 240 | 0.725 | 0.338 | -0.388 [-0.463, -0.313] | 14 / 107 / 119 | <0.0001 | <0.0001 * |
| final asset margin | 240 | 3704 | -14120 | -17824 [-21423, -14272] | 61 / 179 / 0 | <0.0001 | <0.0001 * |
| value destroyed | 240 | 12670 | 9552 | -3119 [-4191, -2001] | 75 / 160 / 5 | <0.0001 | <0.0001 * |
| value lost | 240 | 15314 | 24905 | 9591 [7294, 12002] | 77 / 161 / 2 | <0.0001 | <0.0001 * |
| trade share | 240 | 0.569 | 0.358 | -0.211 [-0.256, -0.166] | 62 / 177 / 1 | <0.0001 | <0.0001 * |
| peak army value | 240 | 3618 | 3131 | -486 [-692, -285] | 94 / 137 / 9 | 0.0056 | 0.0224 * |
| units built | 240 | 64.308 | 61.946 | -2.363 [-6.083, 1.188] | 83 / 152 / 5 | <0.0001 | <0.0001 * |
| production idle fraction | 240 | 0.01 | 0.012 | 0.002 [-0.004, 0.009] | 120 / 95 / 25 | 0.1015 | 0.2029 |
| average credits (float) | 240 | 10345 | 11158 | 813 [515, 1108] | 85 / 155 / 0 | <0.0001 | <0.0001 * |
| first attack s | 240 | 158 | 136 | -21.467 [-27.092, -16.275] | 0 / 56 / 184 (n/d) | <0.0001 | <0.0001 * |
| duration s | 240 | 827 | 978 | 151 [101, 198] | 108 / 74 / 58 (n/d) | 0.0142 | 0.0426 * |
| activations /10 min | 240 | 2.912 | 6.152 | 3.24 [2.996, 3.481] | 225 / 13 / 2 (n/d) | <0.0001 | <0.0001 * |
| invalid plan rate | 240 | 0 | 0 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| USD per match | 240 | 0 | 2.216 | 2.216 [2.116, 2.315] | 0 / 240 / 0 | <0.0001 | <0.0001 * |

### llm+fast vs selector (240 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 240 | 0.725 | 0.356 | -0.369 [-0.446, -0.296] | 14 / 103 / 123 | <0.0001 | <0.0001 * |
| final asset margin | 240 | 3704 | -12894 | -16598 [-20094, -13165] | 70 / 169 / 1 | <0.0001 | <0.0001 * |
| value destroyed | 240 | 12670 | 9964 | -2707 [-3791, -1567] | 84 / 151 / 5 | <0.0001 | 0.0001 * |
| value lost | 240 | 15314 | 24498 | 9184 [6997, 11388] | 85 / 153 / 2 | <0.0001 | <0.0001 * |
| trade share | 240 | 0.569 | 0.369 | -0.2 [-0.244, -0.158] | 73 / 166 / 1 | <0.0001 | <0.0001 * |
| peak army value | 240 | 3618 | 3159 | -459 [-661, -265] | 98 / 125 / 17 | 0.0814 | 0.2443 |
| units built | 240 | 64.308 | 64.696 | 0.388 [-3.271, 3.733] | 91 / 143 / 6 | 0.0008 | 0.0041 * |
| production idle fraction | 240 | 0.01 | 0.009 | -0.001 [-0.006, 0.003] | 118 / 95 / 27 | 0.1315 | 0.2630 |
| average credits (float) | 240 | 10345 | 11058 | 714 [438, 995] | 89 / 151 / 0 | <0.0001 | 0.0005 * |
| first attack s | 240 | 158 | 138 | -19.683 [-24.875, -14.921] | 0 / 56 / 184 (n/d) | <0.0001 | <0.0001 * |
| duration s | 240 | 827 | 991 | 163 [119, 207] | 111 / 68 / 61 (n/d) | 0.0016 | 0.0065 * |
| activations /10 min | 240 | 2.912 | 6.613 | 3.701 [3.469, 3.931] | 237 / 2 / 1 (n/d) | <0.0001 | <0.0001 * |
| invalid plan rate | 240 | 0 | 0 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| USD per match | 240 | 0 | 3.447 | 3.447 [3.3, 3.595] | 0 / 240 / 0 | <0.0001 | <0.0001 * |

### llm-shadow vs selector (240 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 240 | 0.725 | 0.725 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| final asset margin | 240 | 3704 | 3704 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| value destroyed | 240 | 12670 | 12670 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| value lost | 240 | 15314 | 15314 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| trade share | 240 | 0.569 | 0.569 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| peak army value | 240 | 3618 | 3618 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| units built | 240 | 64.308 | 64.308 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| production idle fraction | 240 | 0.01 | 0.01 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| average credits (float) | 240 | 10345 | 10345 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| first attack s | 240 | 158 | 158 | 0 [0, 0] | 0 / 0 / 240 (n/d) | 1.0000 | 1.0000 |
| duration s | 240 | 827 | 827 | 0 [0, 0] | 0 / 0 / 240 (n/d) | 1.0000 | 1.0000 |
| activations /10 min | 240 | 2.912 | 2.912 | 0 [0, 0] | 0 / 0 / 240 (n/d) | 1.0000 | 1.0000 |
| invalid plan rate | 240 | 0 | 0 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| USD per match | 240 | 0 | 1.713 | 1.713 [1.607, 1.825] | 0 / 240 / 0 | <0.0001 | <0.0001 * |

## Paired differences vs selector

Pairs share opponent, map and seed. Difference = arm − baseline, mean with a 95% seeded bootstrap interval; better/worse/tied count pairs by the metric's direction (n/d: no better direction, counted as higher/lower); p is the exact two-sided sign test over untied pairs, Holm-adjusted over the arm's metrics. `*` marks Holm p < 0.05.

### bandit vs selector (958 pairs; 162 identical to another opponent's pair collapsed)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 958 | 0.841 | 0.818 | -0.023 [-0.041, -0.005] | 26 / 48 / 884 | 0.0141 | 0.0986 |
| final asset margin | 958 | 9623 | 9323 | -300 [-1032, 430] | 183 / 240 / 535 | 0.0064 | 0.0576 |
| value destroyed | 958 | 10376 | 10858 | 482 [154, 812] | 215 / 157 / 586 | 0.0031 | 0.0338 * |
| value lost | 958 | 6698 | 7068 | 370 [-58.038, 810] | 169 / 184 / 605 | 0.4562 | 1.0000 |
| trade share | 958 | 0.753 | 0.757 | 0.004 [-0.005, 0.012] | 229 / 172 / 557 | 0.0051 | 0.0510 |
| peak army value | 958 | 3396 | 3637 | 242 [137, 355] | 216 / 163 / 579 | 0.0075 | 0.0598 |
| units built | 958 | 40.936 | 45.559 | 4.623 [3.563, 5.727] | 217 / 145 / 596 | 0.0002 | 0.0022 * |
| production idle fraction | 958 | 0.011 | 0.01 | -0.001 [-0.003, 0.001] | 191 / 225 / 542 | 0.1056 | 0.6334 |
| average credits (float) | 958 | 12100 | 11893 | -207 [-273, -141] | 293 / 200 / 465 | <0.0001 | 0.0004 * |
| first attack s | 901 | 142 | 143 | 1.811 [0.365, 3.463] | 7 / 5 / 889 (n/d) | 0.7744 | 1.0000 |
| duration s | 958 | 523 | 528 | 5.1 [-5.228, 15.482] | 159 / 185 / 614 (n/d) | 0.1776 | 0.8880 |
| activations /10 min | 958 | 2.401 | 5.153 | 2.752 [2.467, 3.02] | 493 / 151 / 314 (n/d) | <0.0001 | <0.0001 * |
| invalid plan rate | 958 | 0 | 0 | 0 [0, 0] | 0 / 0 / 958 | 1.0000 | 1.0000 |
| USD per match | 958 | 0 | 0 | 0 [0, 0] | 0 / 0 / 958 | 1.0000 | 1.0000 |

### distilled vs selector (767 pairs; 353 identical to another opponent's pair collapsed)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 767 | 0.828 | 0.598 | -0.229 [-0.262, -0.197] | 13 / 189 / 565 | <0.0001 | <0.0001 * |
| final asset margin | 767 | 7807 | -1813 | -9620 [-11000, -8264] | 180 / 410 / 177 | <0.0001 | <0.0001 * |
| value destroyed | 767 | 11119 | 9130 | -1989 [-2434, -1525] | 222 / 332 / 213 | <0.0001 | <0.0001 * |
| value lost | 767 | 7977 | 13276 | 5299 [4398, 6208] | 160 / 346 / 261 | <0.0001 | <0.0001 * |
| trade share | 767 | 0.721 | 0.583 | -0.138 [-0.157, -0.119] | 150 / 404 / 213 | <0.0001 | <0.0001 * |
| peak army value | 767 | 3437 | 2927 | -511 [-604, -414] | 139 / 458 / 170 | <0.0001 | <0.0001 * |
| units built | 767 | 46.172 | 43.939 | -2.233 [-3.734, -0.532] | 173 / 424 / 170 | <0.0001 | <0.0001 * |
| production idle fraction | 767 | 0.011 | 0.012 | 0.001 [-0.002, 0.004] | 256 / 315 / 196 | 0.0151 | 0.0454 * |
| average credits (float) | 767 | 11040 | 11551 | 511 [373, 652] | 198 / 437 / 132 | <0.0001 | <0.0001 * |
| first attack s | 728 | 144 | 143 | -0.61 [-1.245, 0.093] | 1 / 11 / 716 (n/d) | 0.0063 | 0.0254 * |
| duration s | 767 | 575 | 690 | 115 [91.897, 138] | 235 / 275 / 257 (n/d) | 0.0841 | 0.1682 |
| activations /10 min | 767 | 2.462 | 3.289 | 0.827 [0.677, 0.992] | 475 / 121 / 171 (n/d) | <0.0001 | <0.0001 * |
| invalid plan rate | 767 | 0 | 0 | 0 [0, 0] | 0 / 0 / 767 | 1.0000 | 1.0000 |
| USD per match | 767 | 0 | 0.004 | 0.004 [0.002, 0.006] | 0 / 33 / 734 | <0.0001 | <0.0001 * |

### llm vs selector (803 pairs; 317 identical to another opponent's pair collapsed)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 803 | 0.832 | 0.534 | -0.298 [-0.336, -0.262] | 31 / 271 / 501 | <0.0001 | <0.0001 * |
| final asset margin | 803 | 8122 | -3271 | -11393 [-13026, -9834] | 243 / 543 / 17 | <0.0001 | <0.0001 * |
| value destroyed | 803 | 11024 | 9466 | -1558 [-2184, -954] | 333 / 405 / 65 | 0.0089 | 0.0357 * |
| value lost | 803 | 7715 | 15757 | 8042 [7068, 9037] | 162 / 532 / 109 | <0.0001 | <0.0001 * |
| trade share | 803 | 0.727 | 0.507 | -0.22 [-0.243, -0.198] | 151 / 566 / 86 | <0.0001 | <0.0001 * |
| peak army value | 803 | 3428 | 2858 | -570 [-686, -455] | 218 / 543 / 42 | <0.0001 | <0.0001 * |
| units built | 803 | 45.319 | 49.476 | 4.157 [2.481, 5.949] | 350 / 405 / 48 | 0.0493 | 0.1479 |
| production idle fraction | 803 | 0.011 | 0.013 | 0.001 [-0.001, 0.004] | 401 / 364 / 38 | 0.1930 | 0.3861 |
| average credits (float) | 803 | 11173 | 11420 | 247 [91.782, 412] | 342 / 460 / 1 | <0.0001 | 0.0002 * |
| first attack s | 760 | 143 | 133 | -10.263 [-12.862, -7.889] | 1 / 94 / 665 (n/d) | <0.0001 | <0.0001 * |
| duration s | 803 | 566 | 758 | 193 [166, 218] | 395 / 307 / 101 (n/d) | 0.0010 | 0.0051 * |
| activations /10 min | 803 | 2.438 | 5.707 | 3.268 [3.125, 3.415] | 779 / 20 / 4 (n/d) | <0.0001 | <0.0001 * |
| invalid plan rate | 803 | 0 | 0 | 0 [0, 0] | 0 / 0 / 803 | 1.0000 | 1.0000 |
| USD per match | 803 | 0 | 1.687 | 1.687 [1.619, 1.753] | 0 / 803 / 0 | <0.0001 | <0.0001 * |

### llm+fast vs selector (805 pairs; 315 identical to another opponent's pair collapsed)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 805 | 0.831 | 0.55 | -0.281 [-0.319, -0.245] | 30 / 258 / 517 | <0.0001 | <0.0001 * |
| final asset margin | 805 | 8138 | -2678 | -10816 [-12402, -9234] | 255 / 529 / 21 | <0.0001 | <0.0001 * |
| value destroyed | 805 | 11025 | 9751 | -1274 [-1874, -660] | 352 / 385 / 68 | 0.2385 | 0.7154 |
| value lost | 805 | 7722 | 15568 | 7846 [6889, 8794] | 167 / 531 / 107 | <0.0001 | <0.0001 * |
| trade share | 805 | 0.726 | 0.512 | -0.213 [-0.237, -0.191] | 170 / 549 / 86 | <0.0001 | <0.0001 * |
| peak army value | 805 | 3431 | 2918 | -512 [-628, -401] | 228 / 533 / 44 | <0.0001 | <0.0001 * |
| units built | 805 | 45.317 | 50.569 | 5.252 [3.552, 7.006] | 371 / 388 / 46 | 0.5614 | 1.0000 |
| production idle fraction | 805 | 0.011 | 0.012 | 0 [-0.002, 0.003] | 422 / 339 / 44 | 0.0029 | 0.0117 * |
| average credits (float) | 805 | 11184 | 11442 | 257 [106, 421] | 345 / 459 / 1 | <0.0001 | 0.0003 * |
| first attack s | 760 | 143 | 133 | -9.979 [-12.321, -7.893] | 3 / 92 / 665 (n/d) | <0.0001 | <0.0001 * |
| duration s | 805 | 565 | 762 | 197 [171, 222] | 417 / 282 / 106 (n/d) | <0.0001 | <0.0001 * |
| activations /10 min | 805 | 2.438 | 5.954 | 3.516 [3.376, 3.664] | 792 / 12 / 1 (n/d) | <0.0001 | <0.0001 * |
| invalid plan rate | 805 | 0 | 0 | 0 [0, 0] | 0 / 0 / 805 | 1.0000 | 1.0000 |
| USD per match | 805 | 0 | 2.605 | 2.605 [2.504, 2.704] | 0 / 805 / 0 | <0.0001 | <0.0001 * |

### llm-shadow vs selector (744 pairs; 376 identical to another opponent's pair collapsed)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 744 | 0.823 | 0.823 | 0 [0, 0] | 0 / 0 / 744 | 1.0000 | 1.0000 |
| final asset margin | 744 | 7524 | 7524 | 0 [0, 0] | 0 / 0 / 744 | 1.0000 | 1.0000 |
| value destroyed | 744 | 11012 | 11012 | 0 [0, 0] | 0 / 0 / 744 | 1.0000 | 1.0000 |
| value lost | 744 | 8054 | 8054 | 0 [0, 0] | 0 / 0 / 744 | 1.0000 | 1.0000 |
| trade share | 744 | 0.719 | 0.719 | 0 [0, 0] | 0 / 0 / 744 | 1.0000 | 1.0000 |
| peak army value | 744 | 3420 | 3420 | 0 [0, 0] | 0 / 0 / 744 | 1.0000 | 1.0000 |
| units built | 744 | 46 | 46 | 0 [0, 0] | 0 / 0 / 744 | 1.0000 | 1.0000 |
| production idle fraction | 744 | 0.011 | 0.011 | 0 [0, 0] | 0 / 0 / 744 | 1.0000 | 1.0000 |
| average credits (float) | 744 | 11049 | 11049 | 0 [0, 0] | 0 / 0 / 744 | 1.0000 | 1.0000 |
| first attack s | 707 | 145 | 145 | 0 [0, 0] | 0 / 0 / 707 (n/d) | 1.0000 | 1.0000 |
| duration s | 744 | 576 | 576 | 0 [0, 0] | 0 / 0 / 744 (n/d) | 1.0000 | 1.0000 |
| activations /10 min | 744 | 2.483 | 2.483 | 0 [0, 0] | 0 / 0 / 744 (n/d) | 1.0000 | 1.0000 |
| invalid plan rate | 744 | 0 | 0 | 0 [0, 0] | 0 / 0 / 744 | 1.0000 | 1.0000 |
| USD per match | 744 | 0 | 1.157 | 1.157 [1.099, 1.216] | 0 / 744 / 0 | <0.0001 | <0.0001 * |

## Perception bottleneck (belief − oracle)

Pairs share opponent, map and seed. Difference = arm − baseline, mean with a 95% seeded bootstrap interval; better/worse/tied count pairs by the metric's direction (n/d: no better direction, counted as higher/lower); p is the exact two-sided sign test over untied pairs, Holm-adjusted over the arm's metrics. `*` marks Holm p < 0.05.

### llm vs llm-oracle (812 pairs; 308 identical to another opponent's pair collapsed)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 812 | 0.587 | 0.525 | -0.062 [-0.1, -0.022] | 103 / 153 / 556 | 0.0021 | 0.0149 * |
| final asset margin | 812 | 43.227 | -3228 | -3271 [-4742, -1769] | 345 / 382 / 85 | 0.1818 | 0.6597 |
| value destroyed | 812 | 10946 | 9350 | -1596 [-2164, -1032] | 236 / 443 / 133 | <0.0001 | <0.0001 * |
| value lost | 812 | 13580 | 15816 | 2236 [1255, 3161] | 331 / 355 / 126 | 0.3799 | 0.7598 |
| trade share | 812 | 0.569 | 0.497 | -0.072 [-0.09, -0.052] | 252 / 456 / 104 | <0.0001 | <0.0001 * |
| peak army value | 812 | 3184 | 2849 | -335 [-429, -233] | 269 / 401 / 142 | <0.0001 | <0.0001 * |
| units built | 812 | 56.803 | 49.245 | -7.558 [-9.303, -5.921] | 245 / 432 / 135 | <0.0001 | <0.0001 * |
| production idle fraction | 812 | 0.016 | 0.013 | -0.003 [-0.007, 0.001] | 374 / 336 / 102 | 0.1649 | 0.6597 |
| average credits (float) | 812 | 10851 | 11516 | 665 [529, 798] | 283 / 502 / 27 | <0.0001 | <0.0001 * |
| first attack s | 790 | 174 | 137 | -37.456 [-44.106, -31.368] | 54 / 688 / 48 (n/d) | <0.0001 | <0.0001 * |
| duration s | 812 | 779 | 759 | -19.882 [-42.059, 0.452] | 270 / 322 / 220 (n/d) | 0.0360 | 0.1799 |
| activations /10 min | 812 | 5.325 | 5.695 | 0.37 [0.158, 0.559] | 424 / 348 / 40 (n/d) | 0.0069 | 0.0415 * |
| invalid plan rate | 812 | 0 | 0 | 0 [0, 0] | 0 / 0 / 812 | 1.0000 | 1.0000 |
| USD per match | 812 | 1.754 | 1.688 | -0.066 [-0.123, -0.014] | 514 / 298 / 0 | <0.0001 | <0.0001 * |

### selector vs selector-oracle (757 pairs; 363 identical to another opponent's pair collapsed)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 757 | 0.855 | 0.816 | -0.038 [-0.066, -0.011] | 44 / 73 / 640 | 0.0093 | 0.0374 * |
| final asset margin | 757 | 9559 | 7426 | -2133 [-3298, -972] | 295 / 406 / 56 | <0.0001 | 0.0002 * |
| value destroyed | 757 | 12154 | 10933 | -1221 [-1797, -664] | 311 / 277 / 169 | 0.1735 | 0.5205 |
| value lost | 757 | 7618 | 8116 | 498 [-161, 1151] | 252 / 357 / 148 | <0.0001 | 0.0001 * |
| trade share | 757 | 0.755 | 0.712 | -0.044 [-0.059, -0.029] | 241 / 403 / 113 | <0.0001 | <0.0001 * |
| peak army value | 757 | 3578 | 3404 | -174 [-312, -55.35] | 396 / 255 / 106 | <0.0001 | <0.0001 * |
| units built | 757 | 46.684 | 45.855 | -0.83 [-2.407, 0.721] | 482 / 169 / 106 | <0.0001 | <0.0001 * |
| production idle fraction | 757 | 0.012 | 0.012 | -0 [-0.003, 0.002] | 515 / 198 / 44 | <0.0001 | <0.0001 * |
| average credits (float) | 757 | 10931 | 11083 | 152 [23.05, 270] | 455 / 286 / 16 | <0.0001 | <0.0001 * |
| first attack s | 708 | 225 | 145 | -80.273 [-85.873, -74.723] | 88 / 619 / 1 (n/d) | <0.0001 | <0.0001 * |
| duration s | 757 | 592 | 575 | -17.221 [-37.526, 2.388] | 474 / 171 / 112 (n/d) | <0.0001 | <0.0001 * |
| activations /10 min | 757 | 2.409 | 2.494 | 0.084 [-0.025, 0.197] | 236 / 495 / 26 (n/d) | <0.0001 | <0.0001 * |
| invalid plan rate | 757 | 0 | 0 | 0 [0, 0] | 0 / 0 / 757 | 1.0000 | 1.0000 |
| USD per match | 757 | 0 | 0 | 0 [0, 0] | 0 / 0 / 757 | 1.0000 | 1.0000 |

## Economy, production and combat (arm side, per-match averages)

Production idle: seconds with a factory, nothing queued and credits for the cheapest item, over match seconds. Trade efficiency: enemy value destroyed in combat / own value lost in combat (pooled over matches).

Time to first attack: first second a combat unit stood in a region holding an enemy structure (mean over matches where it happened; count in brackets).

| Arm | Duration s | Units built | Buildings built | Peak army value | Idle fraction | Avg credits | Final assets (arm / opp) | Trade efficiency | First attack s |
|---|---|---|---|---|---|---|---|---|---|
| bandit | 494 | 43.4 | 6.3 | 3534 | 0.010 | 11210 | 23675 / 15703 | 1.73 (11733400/6786300) | 140 (1063/1120) |
| distilled | 566 | 37.3 | 7.4 | 2890 | 0.012 | 11767 | 21678 / 20069 | 0.92 (9680550/10525550) | 138 (1065/1120) |
| llm | 625 | 41.7 | 8.5 | 2804 | 0.012 | 11606 | 21593 / 21767 | 0.77 (9976550/12977400) | 134 (1105/1120) |
| llm+fast | 628 | 42.6 | 8.6 | 2848 | 0.012 | 11611 | 21790 / 21621 | 0.79 (10201250/12843800) | 135 (1105/1120) |
| llm-oracle | 639 | 47.1 | 8.5 | 3050 | 0.015 | 11118 | 22817 / 20646 | 1.01 (11285750/11157200) | 169 (1104/1120) |
| llm-shadow | 490 | 39.5 | 6.4 | 3327 | 0.011 | 11387 | 23973 / 15761 | 1.75 (11254450/6432700) | 139 (1067/1120) |
| selector | 490 | 39.5 | 6.4 | 3327 | 0.011 | 11387 | 23973 / 15761 | 1.75 (11254450/6432700) | 139 (1067/1120) |
| selector-oracle | 501 | 39.9 | 6.9 | 3422 | 0.011 | 11287 | 24713 / 14968 | 2.01 (12168350/6045200) | 223 (1109/1120) |

## Strategy layer (arm side)

Proposals, invalid plans and lateness are the arm's primary strategist's: rejected / proposals; seconds from a proposal's snapshot to its validation; late-discard count / proposals. Fallback and emergency proposals (immediate and always valid) are counted apart so they cannot flatter the strategist under test. Shadow columns are the shadow strategist's own lateness and would-be invalid plans. Churn: activations and posture flips per 10 game minutes.

Shadow agreement: shadow proposals naming the same playbook as the primary proposal for the same request / shadow proposals whose request got a primary proposal.

| Arm | Proposals | Invalid plans | Mean lateness s | Late-discarded | Fallback proposals | Failed requests | Shadow proposals | Shadow invalid | Shadow mean lateness s | Shadow agreement | Activations /10 min | Posture flips /10 min |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| bandit | 29659 | 0/29659 (0.000) | 0.00 | 0/29659 (0.000) | 3606 | 0 | 0 | n/a | n/a | n/a | 4.99 | 1.11 |
| distilled | 35404 | 0/35404 (0.000) | 0.01 | 0/35404 (0.000) | 268 | 0 | 0 | n/a | n/a | n/a | 3.71 | 1.68 |
| llm | 38713 | 0/38713 (0.000) | 4.00 | 72/38713 (0.002) | 3235 | 268 | 0 | n/a | n/a | n/a | 5.99 | 2.20 |
| llm+fast | 140832 | 0/140832 (0.000) | 4.00 | 1165/140832 (0.008) | 3515 | 1021 | 0 | n/a | n/a | n/a | 6.31 | 2.38 |
| llm-oracle | 38952 | 0/38952 (0.000) | 4.00 | 43/38952 (0.001) | 3157 | 159 | 0 | n/a | n/a | n/a | 5.39 | 1.98 |
| llm-shadow | 29528 | 0/29528 (0.000) | 0.00 | 0/29528 (0.000) | 0 | 0 | 28692 | 12/28692 (0.000) | 4.00 | 5096/28692 (0.178) | 2.83 | 1.23 |
| selector | 29528 | 0/29528 (0.000) | 0.00 | 0/29528 (0.000) | 0 | 0 | 0 | n/a | n/a | n/a | 2.83 | 1.23 |
| selector-oracle | 30242 | 0/30242 (0.000) | 0.00 | 0/30242 (0.000) | 0 | 0 | 0 | n/a | n/a | n/a | 2.55 | 1.15 |

## Command gate and simulator rejections (arm side, totals)

- **bandit**: 0 dropped by the gate (none); 2295 commands rejected by the simulator.
- **distilled**: 0 dropped by the gate (none); 2780 commands rejected by the simulator.
- **llm**: 0 dropped by the gate (none); 5567 commands rejected by the simulator.
- **llm+fast**: 0 dropped by the gate (none); 4636 commands rejected by the simulator.
- **llm-oracle**: 0 dropped by the gate (none); 56850 commands rejected by the simulator.
- **llm-shadow**: 0 dropped by the gate (none); 1985 commands rejected by the simulator.
- **selector**: 0 dropped by the gate (none); 1985 commands rejected by the simulator.
- **selector-oracle**: 0 dropped by the gate (none); 39725 commands rejected by the simulator.

## Inference cost (arm side)

- **bandit** (no model): 0 in / 0 out tokens and $0.0000 per match, of which $0.0000 on failed requests.
- **distilled** (claude-opus-5): 472 in / 14 out tokens and $0.0027 per match, of which $0.0000 on failed requests (fake client: tokens estimated from prompt size, priced at the list rate; not a measurement).
- **llm** (claude-opus-5): 232316 in / 7806 out tokens and $1.3567 per match, of which $0.0000 on failed requests (fake client: tokens estimated from prompt size, priced at the list rate; not a measurement).
- **llm+fast** (claude-opus-5): 850954 in / 27553 out tokens and $2.1001 per match, of which $0.0000 on failed requests; served by claude-haiku-4-5 x101244, claude-opus-5 x39588 (fake client: tokens estimated from prompt size, priced at the list rate; not a measurement).
- **llm-oracle** (claude-opus-5): 242771 in / 7859 out tokens and $1.4104 per match, of which $0.0000 on failed requests (fake client: tokens estimated from prompt size, priced at the list rate; not a measurement).
- **llm-shadow** (claude-opus-5): 163680 in / 5774 out tokens and $0.9628 per match, of which $0.0000 on failed requests (fake client: tokens estimated from prompt size, priced at the list rate; not a measurement).
- **selector** (no model): 0 in / 0 out tokens and $0.0000 per match, of which $0.0000 on failed requests.
- **selector-oracle** (no model): 0 in / 0 out tokens and $0.0000 per match, of which $0.0000 on failed requests.

## Distillation

Decisions: primary requests the distilled strategist answered. Escalations: those it handed to the LLM (out of distribution, low confidence, or no trained playbook for the faction). Cost per match includes the escalations' tokens; the teacher column is the matching `llm` arm in this run.

| Arm | Decisions | Escalations (rate) | USD per match | Teacher USD per match |
|---|---|---|---|---|
| distilled | 35404 | 71/35404 (0.002) | $0.0027 | $1.3567 |

## Hidden-information leakage

- **bandit**: 0 validator `fog.*` rejections.
- **distilled**: 0 validator `fog.*` rejections.
- **llm**: 0 validator `fog.*` rejections.
- **llm+fast**: 0 validator `fog.*` rejections.
- **llm-oracle**: 0 validator `fog.*` rejections.
- **llm-shadow**: 0 validator `fog.*` rejections; shadow strategist: 0.
- **selector**: 0 validator `fog.*` rejections.
- **selector-oracle**: 0 validator `fog.*` rejections.

Probe: two lockstep simulations, hidden state of one perturbed (`SimLeakageProbe`: enemy credits and queue, wounded hidden enemies, a hidden unit in an unseen region, one announced there through the event path, and one just across a border inside the arm's weapon reach), strategist-context hash compared on every following frame until the window closes or the objects the arm can see first differ. A differing frame is one where the context changed while everything visible was still identical. Fog-violation frames are arm frames, over the whole run of both simulations, that carried an enemy object or event from a region the arm did not see: a per-frame check that finds leaks the perturbation does not exercise.

| Arm | Map | Seed | Perturbed at s | Lockstep before | Compared s | Differing frames | Fog-violation frames | Note |
|---|---|---|---|---|---|---|---|---|
| selector | twin-valley | 1 | 90 | yes | 44 | 0/659 | 0 | stopped at 134 s: the arm saw a legitimate difference |
| selector | twin-valley | 1 | 240 | yes | 0 | 0/3 | 0 | stopped at 240 s: the arm saw a legitimate difference |
| bandit | twin-valley | 1 | 90 | yes | 44 | 659/659 | 0 | stopped at 134 s: the arm saw a legitimate difference |
| bandit | twin-valley | 1 | 240 | yes | 0 | 0/3 | 0 | stopped at 240 s: the arm saw a legitimate difference |
| llm-shadow | twin-valley | 1 | 90 | yes | 44 | 0/659 | 0 | stopped at 134 s: the arm saw a legitimate difference |
| llm-shadow | twin-valley | 1 | 240 | yes | 0 | 0/3 | 0 | stopped at 240 s: the arm saw a legitimate difference |
| llm | twin-valley | 1 | 90 | yes | 44 | 0/659 | 0 | stopped at 134 s: the arm saw a legitimate difference |
| llm | twin-valley | 1 | 240 | yes | 0 | 0/4 | 0 | stopped at 240 s: the arm saw a legitimate difference |
| llm+fast | twin-valley | 1 | 90 | yes | 44 | 0/659 | 0 | stopped at 134 s: the arm saw a legitimate difference |
| llm+fast | twin-valley | 1 | 240 | yes | 0 | 0/4 | 0 | stopped at 240 s: the arm saw a legitimate difference |
| selector-oracle | twin-valley | 1 | 90 | no | 0 | 0/0 | 0 | not applicable: oracle frames carry hidden state by design |
| selector-oracle | twin-valley | 1 | 240 | no | 0 | 0/0 | 0 | not applicable: oracle frames carry hidden state by design |
| llm-oracle | twin-valley | 1 | 90 | no | 0 | 0/0 | 0 | not applicable: oracle frames carry hidden state by design |
| llm-oracle | twin-valley | 1 | 240 | no | 0 | 0/0 | 0 | not applicable: oracle frames carry hidden state by design |
| distilled | twin-valley | 1 | 90 | yes | 44 | 0/659 | 0 | stopped at 134 s: the arm saw a legitimate difference |
| distilled | twin-valley | 1 | 240 | yes | 0 | 0/3 | 0 | stopped at 240 s: the arm saw a legitimate difference |

