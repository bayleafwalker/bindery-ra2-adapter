# Bindery region sim arena report

Results are from the bindery region simulator with the approximate `bindery-sim-approx` rules, not retail RA2; they are directional.
The arm is a full `BotRuntime`. Opponents named `ai-*` are the independent scripted AI (`Bindery.Ra2.Bot.Sim.Opponents`, no shared planner code; `:easy`/`:medium`/`:hard`, default hard); the other opponents are pinned-playbook styles running a frozen copy of the bot's stack as of commit 7f3e2c7 (`Bindery.Ra2.Bot.Baseline`), a stationary benchmark; `live-<style>` runs a pinned style on the live stack. The arm plays Allied on odd seeds and Soviet on even seeds. Match limit 1200 s (a timeout is won on final asset value).
Opponents are split as well as maps. Held-out opponents (`ai-horde`, `ai-armor`, independent scripted styles) are never used for the selector's default playbook, bandit learning, distillation datasets or tuning; see "Held-out opponents" below. They are not untouched: their difficulty and the contested benchmark's handicaps were calibrated against the selector's win rate on all five maps, held-out maps included, so the selector's own held-out win rate is near one half partly by construction. Every other opponent is a training opponent: the selector's default playbook was chosen from a style-versus-style matrix against the pinned-playbook styles (training maps only), so win rates against training opponents are partly in-sample.
Benchmark: standard (opponent income ×1, Allied income ×1, Allied starting credits default, opponent starting credits default, combat noise ±0).
Labels in this run: `distilled-from:llm arm in this run (training maps) (9419 examples)`, `llm-fake`, `oracle`.

Matches: 7360

## Win rate (arm × split)

Distinct games drop repeats of an identical game (same arm faction, decision log and outcome on the same map): against a differently named opponent whose style had not diverged when the match ended, or on another seed that changed nothing; the distinct interval is the one to read. The arm plays Allied on odd seeds and Soviet on even seeds, and starts west on seeds 1-2, 5-6, ... and east on 3-4, 7-8, ...; the fixture is asymmetric, so the faction and side columns show the mix behind each rate.

| Arm | Split | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) | Distinct games | Distinct wins | 95% interval, distinct | As Allied | As Soviet | As west | As east | Eliminations won | Timeouts |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| bandit | heldout | 328 | 40 | 0 | 368 | 0.891 | [0.855, 0.919] | 303 | 263 | [0.825, 0.902] | 148/184 | 180/184 | 166/184 | 162/184 | 317 | 16 |
| bandit | training | 491 | 61 | 0 | 552 | 0.889 | [0.861, 0.913] | 452 | 391 | [0.830, 0.893] | 223/276 | 268/276 | 243/276 | 248/276 | 438 | 84 |
| distilled | heldout | 285 | 83 | 0 | 368 | 0.774 | [0.729, 0.814] | 241 | 158 | [0.594, 0.713] | 126/184 | 159/184 | 143/184 | 142/184 | 259 | 49 |
| distilled | training | 456 | 96 | 0 | 552 | 0.826 | [0.792, 0.855] | 357 | 261 | [0.683, 0.774] | 212/276 | 244/276 | 232/276 | 224/276 | 365 | 136 |
| llm | heldout | 236 | 132 | 0 | 368 | 0.641 | [0.591, 0.689] | 255 | 123 | [0.422, 0.543] | 90/184 | 146/184 | 116/184 | 120/184 | 217 | 54 |
| llm | training | 406 | 145 | 1 | 552 | 0.736 | [0.697, 0.771] | 367 | 223 | [0.557, 0.656] | 165/276 | 241/276 | 206/276 | 200/276 | 340 | 127 |
| llm+fast | heldout | 244 | 124 | 0 | 368 | 0.663 | [0.613, 0.709] | 254 | 130 | [0.451, 0.573] | 93/184 | 151/184 | 122/184 | 122/184 | 221 | 52 |
| llm+fast | training | 413 | 139 | 0 | 552 | 0.748 | [0.710, 0.783] | 368 | 230 | [0.574, 0.673] | 169/276 | 244/276 | 201/276 | 212/276 | 338 | 132 |
| llm-oracle | heldout | 274 | 94 | 0 | 368 | 0.745 | [0.698, 0.786] | 238 | 145 | [0.546, 0.669] | 120/184 | 154/184 | 136/184 | 138/184 | 230 | 82 |
| llm-oracle | training | 438 | 113 | 1 | 552 | 0.793 | [0.758, 0.825] | 355 | 242 | [0.632, 0.728] | 183/276 | 255/276 | 219/276 | 219/276 | 349 | 149 |
| llm-shadow | heldout | 329 | 39 | 0 | 368 | 0.894 | [0.858, 0.922] | 235 | 196 | [0.781, 0.876] | 156/184 | 173/184 | 168/184 | 161/184 | 319 | 26 |
| llm-shadow | training | 500 | 52 | 0 | 552 | 0.906 | [0.879, 0.927] | 348 | 296 | [0.809, 0.884] | 236/276 | 264/276 | 249/276 | 251/276 | 453 | 85 |
| selector | heldout | 329 | 39 | 0 | 368 | 0.894 | [0.858, 0.922] | 234 | 195 | [0.780, 0.876] | 156/184 | 173/184 | 168/184 | 161/184 | 319 | 26 |
| selector | training | 500 | 52 | 0 | 552 | 0.906 | [0.879, 0.927] | 345 | 293 | [0.808, 0.883] | 236/276 | 264/276 | 249/276 | 251/276 | 453 | 85 |
| selector-oracle | heldout | 328 | 40 | 0 | 368 | 0.891 | [0.855, 0.919] | 216 | 176 | [0.758, 0.861] | 152/184 | 176/184 | 165/184 | 163/184 | 313 | 29 |
| selector-oracle | training | 505 | 47 | 0 | 552 | 0.915 | [0.889, 0.935] | 326 | 281 | [0.820, 0.895] | 231/276 | 274/276 | 254/276 | 251/276 | 467 | 81 |

Benchmark check: the baseline `selector` scored 0.901 over 920 matches, outside the 30–70% band: the benchmark is saturated and win-rate comparisons carry little information (use `--benchmark contested`).

## Win rate by opponent style

| Arm | Split | ai-air:easy | ai-air:hard | ai-air:medium | ai-armor:easy | ai-armor:hard | ai-armor:medium | ai-balanced:easy | ai-balanced:hard | ai-balanced:medium | ai-horde:easy | ai-horde:hard | ai-horde:medium | ai-rush:easy | ai-rush:hard | ai-rush:medium | ai-turtle:easy | ai-turtle:hard | ai-turtle:medium | balanced | harass | rush | tech | turtle |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| bandit | heldout | 16/16 | 16/16 | 16/16 | 13/16 | 12/16 | 12/16 | 16/16 | 16/16 | 16/16 | 12/16 | 8/16 | 12/16 | 16/16 | 9/16 | 16/16 | 12/16 | 15/16 | 15/16 | 16/16 | 16/16 | 16/16 | 16/16 | 16/16 |
| bandit | training | 24/24 | 24/24 | 24/24 | 22/24 | 12/24 | 13/24 | 24/24 | 21/24 | 24/24 | 20/24 | 21/24 | 16/24 | 22/24 | 19/24 | 23/24 | 16/24 | 24/24 | 22/24 | 24/24 | 24/24 | 24/24 | 24/24 | 24/24 |
| distilled | heldout | 16/16 | 16/16 | 16/16 | 0/16 | 1/16 | 3/16 | 16/16 | 16/16 | 16/16 | 9/16 | 8/16 | 9/16 | 16/16 | 16/16 | 16/16 | 9/16 | 11/16 | 11/16 | 16/16 | 16/16 | 16/16 | 16/16 | 16/16 |
| distilled | training | 24/24 | 24/24 | 24/24 | 4/24 | 2/24 | 4/24 | 24/24 | 24/24 | 24/24 | 18/24 | 13/24 | 19/24 | 24/24 | 22/24 | 24/24 | 20/24 | 22/24 | 20/24 | 24/24 | 24/24 | 24/24 | 24/24 | 24/24 |
| llm | heldout | 16/16 | 16/16 | 16/16 | 2/16 | 0/16 | 3/16 | 16/16 | 9/16 | 16/16 | 5/16 | 9/16 | 6/16 | 9/16 | 12/16 | 8/16 | 5/16 | 4/16 | 4/16 | 16/16 | 16/16 | 16/16 | 16/16 | 16/16 |
| llm | training | 24/24 | 24/24 | 24/24 | 1/24 | 4/24 | 6/24 | 24/24 | 24/24 | 24/24 | 11/24 | 16/24 | 15/24 | 21/24 | 14/24 | 12/24 | 13/24 | 14/24 | 15/24 | 24/24 | 24/24 | 24/24 | 24/24 | 24/24 |
| llm+fast | heldout | 16/16 | 16/16 | 16/16 | 2/16 | 3/16 | 7/16 | 16/16 | 9/16 | 16/16 | 8/16 | 6/16 | 4/16 | 9/16 | 8/16 | 8/16 | 7/16 | 5/16 | 8/16 | 16/16 | 16/16 | 16/16 | 16/16 | 16/16 |
| llm+fast | training | 24/24 | 24/24 | 24/24 | 2/24 | 5/24 | 7/24 | 24/24 | 23/24 | 24/24 | 14/24 | 18/24 | 12/24 | 21/24 | 15/24 | 12/24 | 18/24 | 14/24 | 12/24 | 24/24 | 24/24 | 24/24 | 24/24 | 24/24 |
| llm-oracle | heldout | 16/16 | 16/16 | 16/16 | 7/16 | 4/16 | 11/16 | 16/16 | 16/16 | 16/16 | 10/16 | 5/16 | 9/16 | 16/16 | 8/16 | 9/16 | 8/16 | 4/16 | 7/16 | 16/16 | 16/16 | 16/16 | 16/16 | 16/16 |
| llm-oracle | training | 24/24 | 24/24 | 24/24 | 3/24 | 7/24 | 14/24 | 24/24 | 24/24 | 24/24 | 15/24 | 21/24 | 16/24 | 24/24 | 12/24 | 16/24 | 15/24 | 16/24 | 15/24 | 24/24 | 24/24 | 24/24 | 24/24 | 24/24 |
| llm-shadow | heldout | 16/16 | 16/16 | 16/16 | 12/16 | 8/16 | 10/16 | 16/16 | 16/16 | 16/16 | 12/16 | 8/16 | 12/16 | 16/16 | 16/16 | 16/16 | 14/16 | 15/16 | 14/16 | 16/16 | 16/16 | 16/16 | 16/16 | 16/16 |
| llm-shadow | training | 24/24 | 24/24 | 24/24 | 20/24 | 10/24 | 9/24 | 24/24 | 24/24 | 24/24 | 19/24 | 22/24 | 15/24 | 24/24 | 22/24 | 24/24 | 24/24 | 23/24 | 24/24 | 24/24 | 24/24 | 24/24 | 24/24 | 24/24 |
| selector | heldout | 16/16 | 16/16 | 16/16 | 12/16 | 8/16 | 10/16 | 16/16 | 16/16 | 16/16 | 12/16 | 8/16 | 12/16 | 16/16 | 16/16 | 16/16 | 14/16 | 15/16 | 14/16 | 16/16 | 16/16 | 16/16 | 16/16 | 16/16 |
| selector | training | 24/24 | 24/24 | 24/24 | 20/24 | 10/24 | 9/24 | 24/24 | 24/24 | 24/24 | 19/24 | 22/24 | 15/24 | 24/24 | 22/24 | 24/24 | 24/24 | 23/24 | 24/24 | 24/24 | 24/24 | 24/24 | 24/24 | 24/24 |
| selector-oracle | heldout | 16/16 | 16/16 | 16/16 | 12/16 | 8/16 | 8/16 | 16/16 | 16/16 | 16/16 | 12/16 | 13/16 | 12/16 | 16/16 | 16/16 | 16/16 | 13/16 | 13/16 | 13/16 | 16/16 | 16/16 | 16/16 | 16/16 | 16/16 |
| selector-oracle | training | 24/24 | 24/24 | 24/24 | 15/24 | 14/24 | 15/24 | 24/24 | 24/24 | 24/24 | 23/24 | 20/24 | 20/24 | 24/24 | 24/24 | 24/24 | 19/24 | 21/24 | 22/24 | 24/24 | 24/24 | 24/24 | 24/24 | 24/24 |

Identical games (same arm decision log and outcome against differently named opponents; counted once in the distinct columns and paired tables):

- bandit: ai-air:easy = ai-air:hard = ai-air:medium on 20 map × seed cells
- bandit: ai-balanced:hard = ai-balanced:medium on 19 map × seed cells
- bandit: ai-turtle:hard = ai-turtle:medium on 19 map × seed cells
- bandit: balanced = harass = rush = tech = turtle on 19 map × seed cells
- bandit: balanced = rush = tech = turtle on 1 map × seed cell
- distilled: ai-air:easy = ai-air:hard = ai-air:medium on 38 map × seed cells
- distilled: ai-balanced:easy = ai-balanced:medium on 20 map × seed cells
- distilled: ai-balanced:hard = ai-balanced:medium on 20 map × seed cells
- distilled: ai-balanced:hard = ai-rush:easy on 20 map × seed cells
- distilled: ai-horde:hard = ai-horde:medium on 4 map × seed cells
- distilled: ai-turtle:easy = ai-turtle:medium on 4 map × seed cells
- distilled: ai-turtle:hard = ai-turtle:medium on 9 map × seed cells
- distilled: balanced = harass = rush = tech = turtle on 39 map × seed cells
- llm: ai-air:easy = ai-air:hard = ai-air:medium on 40 map × seed cells
- llm: ai-balanced:easy = ai-balanced:medium on 20 map × seed cells
- llm: ai-balanced:hard = ai-balanced:medium on 20 map × seed cells
- llm: ai-balanced:hard = ai-rush:easy on 10 map × seed cells
- llm: ai-turtle:easy = ai-turtle:medium on 3 map × seed cells
- llm: ai-turtle:hard = ai-turtle:medium on 5 map × seed cells
- llm: balanced = harass = rush = tech = turtle on 40 map × seed cells
- llm+fast: ai-air:easy = ai-air:hard = ai-air:medium on 40 map × seed cells
- llm+fast: ai-balanced:easy = ai-balanced:medium on 20 map × seed cells
- llm+fast: ai-balanced:hard = ai-balanced:medium on 20 map × seed cells
- llm+fast: ai-balanced:hard = ai-rush:easy on 10 map × seed cells
- llm+fast: ai-turtle:easy = ai-turtle:medium on 3 map × seed cells
- llm+fast: ai-turtle:hard = ai-turtle:medium on 5 map × seed cells
- llm+fast: balanced = harass = rush = tech = turtle on 40 map × seed cells
- llm-oracle: ai-air:easy = ai-air:hard = ai-air:medium on 37 map × seed cells
- llm-oracle: ai-balanced:easy = ai-balanced:medium on 16 map × seed cells
- llm-oracle: ai-balanced:hard = ai-balanced:medium on 20 map × seed cells
- llm-oracle: ai-balanced:hard = ai-rush:easy on 20 map × seed cells
- llm-oracle: ai-turtle:easy = ai-turtle:medium on 2 map × seed cells
- llm-oracle: ai-turtle:hard = ai-turtle:medium on 9 map × seed cells
- llm-oracle: balanced = harass = rush = tech = turtle on 36 map × seed cells
- llm-shadow: ai-air:easy = ai-air:hard = ai-air:medium on 40 map × seed cells
- llm-shadow: ai-balanced:easy = ai-balanced:medium on 20 map × seed cells
- llm-shadow: ai-balanced:hard = ai-balanced:medium on 20 map × seed cells
- llm-shadow: ai-balanced:hard = ai-rush:easy on 20 map × seed cells
- llm-shadow: ai-turtle:easy = ai-turtle:medium on 16 map × seed cells
- llm-shadow: ai-turtle:hard = ai-turtle:medium on 20 map × seed cells
- llm-shadow: balanced = harass = rush = tech = turtle on 40 map × seed cells
- selector: ai-air:easy = ai-air:hard = ai-air:medium on 38 map × seed cells
- selector: ai-balanced:easy = ai-balanced:medium on 20 map × seed cells
- selector: ai-balanced:hard = ai-balanced:medium on 20 map × seed cells
- selector: ai-balanced:hard = ai-rush:easy on 20 map × seed cells
- selector: ai-turtle:easy = ai-turtle:medium on 16 map × seed cells
- selector: ai-turtle:hard = ai-turtle:medium on 20 map × seed cells
- selector: balanced = harass = rush = tech = turtle on 39 map × seed cells
- selector-oracle: ai-air:easy = ai-air:hard = ai-air:medium on 32 map × seed cells
- selector-oracle: ai-balanced:easy = ai-balanced:medium on 16 map × seed cells
- selector-oracle: ai-balanced:hard = ai-balanced:medium on 19 map × seed cells
- selector-oracle: ai-balanced:hard = ai-rush:easy on 15 map × seed cells
- selector-oracle: ai-turtle:easy = ai-turtle:medium on 12 map × seed cells
- selector-oracle: ai-turtle:hard = ai-turtle:medium on 20 map × seed cells
- selector-oracle: balanced = harass = rush = tech = turtle on 35 map × seed cells

## Held-out opponents

Opponent split × map split. Held-out opponents never inform the selector's defaults, bandit learning (the arena abandons the bandit's episode instead of crediting it, as it does on held-out maps), distillation datasets or teacher runs, or tuning (the tuner refuses them); their difficulty was calibrated against the selector on all maps, so read the selector's own held-out rate as partly calibrated and other arms' rates relative to it.

| Arm | Opponents | Maps | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) |
|---|---|---|---|---|---|---|---|---|
| bandit | heldout | heldout | 69 | 27 | 0 | 96 | 0.719 | [0.622, 0.799] |
| bandit | heldout | training | 104 | 40 | 0 | 144 | 0.722 | [0.644, 0.789] |
| bandit | training | heldout | 259 | 13 | 0 | 272 | 0.952 | [0.920, 0.972] |
| bandit | training | training | 387 | 21 | 0 | 408 | 0.949 | [0.923, 0.966] |
| distilled | heldout | heldout | 30 | 66 | 0 | 96 | 0.313 | [0.229, 0.411] |
| distilled | heldout | training | 60 | 84 | 0 | 144 | 0.417 | [0.339, 0.498] |
| distilled | training | heldout | 255 | 17 | 0 | 272 | 0.938 | [0.902, 0.961] |
| distilled | training | training | 396 | 12 | 0 | 408 | 0.971 | [0.949, 0.983] |
| llm | heldout | heldout | 25 | 71 | 0 | 96 | 0.260 | [0.183, 0.356] |
| llm | heldout | training | 53 | 91 | 0 | 144 | 0.368 | [0.294, 0.449] |
| llm | training | heldout | 211 | 61 | 0 | 272 | 0.776 | [0.723, 0.821] |
| llm | training | training | 353 | 54 | 1 | 408 | 0.865 | [0.829, 0.895] |
| llm+fast | heldout | heldout | 30 | 66 | 0 | 96 | 0.313 | [0.229, 0.411] |
| llm+fast | heldout | training | 58 | 86 | 0 | 144 | 0.403 | [0.326, 0.484] |
| llm+fast | training | heldout | 214 | 58 | 0 | 272 | 0.787 | [0.734, 0.831] |
| llm+fast | training | training | 355 | 53 | 0 | 408 | 0.870 | [0.834, 0.899] |
| llm-oracle | heldout | heldout | 46 | 50 | 0 | 96 | 0.479 | [0.382, 0.578] |
| llm-oracle | heldout | training | 76 | 67 | 1 | 144 | 0.528 | [0.447, 0.608] |
| llm-oracle | training | heldout | 228 | 44 | 0 | 272 | 0.838 | [0.790, 0.877] |
| llm-oracle | training | training | 362 | 46 | 0 | 408 | 0.887 | [0.853, 0.914] |
| llm-shadow | heldout | heldout | 62 | 34 | 0 | 96 | 0.646 | [0.546, 0.734] |
| llm-shadow | heldout | training | 95 | 49 | 0 | 144 | 0.660 | [0.579, 0.732] |
| llm-shadow | training | heldout | 267 | 5 | 0 | 272 | 0.982 | [0.958, 0.992] |
| llm-shadow | training | training | 405 | 3 | 0 | 408 | 0.993 | [0.979, 0.997] |
| selector | heldout | heldout | 62 | 34 | 0 | 96 | 0.646 | [0.546, 0.734] |
| selector | heldout | training | 95 | 49 | 0 | 144 | 0.660 | [0.579, 0.732] |
| selector | training | heldout | 267 | 5 | 0 | 272 | 0.982 | [0.958, 0.992] |
| selector | training | training | 405 | 3 | 0 | 408 | 0.993 | [0.979, 0.997] |
| selector-oracle | heldout | heldout | 65 | 31 | 0 | 96 | 0.677 | [0.578, 0.762] |
| selector-oracle | heldout | training | 107 | 37 | 0 | 144 | 0.743 | [0.666, 0.807] |
| selector-oracle | training | heldout | 263 | 9 | 0 | 272 | 0.967 | [0.938, 0.982] |
| selector-oracle | training | training | 398 | 10 | 0 | 408 | 0.975 | [0.955, 0.987] |
| bandit | heldout | all | 173 | 67 | 0 | 240 | 0.721 | [0.661, 0.774] |
| distilled | heldout | all | 90 | 150 | 0 | 240 | 0.375 | [0.316, 0.438] |
| llm | heldout | all | 78 | 162 | 0 | 240 | 0.325 | [0.269, 0.387] |
| llm+fast | heldout | all | 88 | 152 | 0 | 240 | 0.367 | [0.308, 0.429] |
| llm-oracle | heldout | all | 122 | 117 | 1 | 240 | 0.508 | [0.445, 0.571] |
| llm-shadow | heldout | all | 157 | 83 | 0 | 240 | 0.654 | [0.592, 0.711] |
| selector | heldout | all | 157 | 83 | 0 | 240 | 0.654 | [0.592, 0.711] |
| selector-oracle | heldout | all | 172 | 68 | 0 | 240 | 0.717 | [0.657, 0.770] |

### Paired differences vs selector, held-out opponents only

Pairs share opponent, map and seed. Difference = arm − baseline, mean with a 95% seeded bootstrap interval; better/worse/tied count pairs by the metric's direction (n/d: no better direction, counted as higher/lower); p is the exact two-sided sign test over untied pairs, Holm-adjusted over the arm's metrics. `*` marks Holm p < 0.05.

### bandit vs selector (240 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 240 | 0.654 | 0.721 | 0.067 [0.017, 0.121] | 28 / 12 / 200 | 0.0166 | 0.1825 |
| final asset margin | 240 | 4824 | 6431 | 1606 [-649, 3997] | 112 / 99 / 29 | 0.4088 | 1.0000 |
| value destroyed | 240 | 12058 | 11932 | -126 [-908, 680] | 83 / 111 / 46 | 0.0523 | 0.4378 |
| value lost | 240 | 13750 | 13211 | -539 [-1987, 855] | 109 / 89 / 42 | 0.1768 | 1.0000 |
| trade share | 240 | 0.577 | 0.61 | 0.033 [0.009, 0.059] | 111 / 99 / 30 | 0.4479 | 1.0000 |
| peak army value | 240 | 3593 | 3704 | 111 [-183, 419] | 82 / 111 / 47 | 0.0436 | 0.4357 |
| units built | 240 | 57.704 | 56.046 | -1.658 [-3.479, 0.029] | 81 / 99 / 60 | 0.2050 | 1.0000 |
| production idle fraction | 240 | 0.011 | 0.007 | -0.005 [-0.009, -0] | 108 / 80 / 52 | 0.0486 | 0.4378 |
| average credits (float) | 240 | 6179 | 6383 | 204 [63.255, 349] | 88 / 126 / 26 | 0.0113 | 0.1351 |
| first attack s | 240 | 149 | 162 | 12.596 [4.742, 22.013] | 8 / 0 / 232 (n/d) | 0.0078 | 0.1016 |
| duration s | 240 | 825 | 783 | -42.35 [-72.404, -12.621] | 81 / 84 / 75 (n/d) | 0.8763 | 1.0000 |
| activations /10 min | 240 | 2.799 | 6.279 | 3.48 [2.797, 4.096] | 156 / 78 / 6 (n/d) | <0.0001 | <0.0001 * |
| invalid plan rate | 240 | 0 | 0 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| USD per match | 240 | 0 | 0 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |

### distilled vs selector (240 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 240 | 0.654 | 0.375 | -0.279 [-0.35, -0.204] | 17 / 84 / 139 | <0.0001 | <0.0001 * |
| final asset margin | 240 | 4824 | -10600 | -15424 [-18859, -12145] | 53 / 186 / 1 | <0.0001 | <0.0001 * |
| value destroyed | 240 | 12058 | 8247 | -3810 [-4768, -2879] | 47 / 191 / 2 | <0.0001 | <0.0001 * |
| value lost | 240 | 13750 | 22068 | 8318 [6178, 10428] | 89 / 145 / 6 | 0.0003 | 0.0015 * |
| trade share | 240 | 0.577 | 0.377 | -0.2 [-0.239, -0.157] | 68 / 172 / 0 | <0.0001 | <0.0001 * |
| peak army value | 240 | 3593 | 3013 | -580 [-783, -382] | 74 / 152 / 14 | <0.0001 | <0.0001 * |
| units built | 240 | 57.704 | 53.071 | -4.633 [-8.067, -1.267] | 65 / 173 / 2 | <0.0001 | <0.0001 * |
| production idle fraction | 240 | 0.011 | 0.018 | 0.007 [-0.001, 0.016] | 120 / 95 / 25 | 0.1015 | 0.4058 |
| average credits (float) | 240 | 6179 | 7440 | 1262 [898, 1619] | 71 / 169 / 0 | <0.0001 | <0.0001 * |
| first attack s | 240 | 149 | 149 | 0 [0, 0] | 0 / 0 / 240 (n/d) | 1.0000 | 1.0000 |
| duration s | 240 | 825 | 996 | 171 [116, 223] | 106 / 86 / 48 (n/d) | 0.1701 | 0.5104 |
| activations /10 min | 240 | 2.799 | 3.646 | 0.848 [0.576, 1.114] | 168 / 66 / 6 (n/d) | <0.0001 | <0.0001 * |
| invalid plan rate | 240 | 0 | 0 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| USD per match | 240 | 0 | 0.015 | 0.015 [0.011, 0.019] | 0 / 48 / 192 | <0.0001 | <0.0001 * |

### llm vs selector (240 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 240 | 0.654 | 0.325 | -0.329 [-0.4, -0.258] | 10 / 89 / 141 | <0.0001 | <0.0001 * |
| final asset margin | 240 | 4824 | -12787 | -17611 [-21338, -14025] | 66 / 174 / 0 | <0.0001 | <0.0001 * |
| value destroyed | 240 | 12058 | 9028 | -3030 [-4166, -1917] | 84 / 153 / 3 | <0.0001 | <0.0001 * |
| value lost | 240 | 13750 | 24403 | 10653 [8530, 12898] | 66 / 172 / 2 | <0.0001 | <0.0001 * |
| trade share | 240 | 0.577 | 0.337 | -0.24 [-0.28, -0.2] | 48 / 192 / 0 | <0.0001 | <0.0001 * |
| peak army value | 240 | 3593 | 3070 | -523 [-728, -326] | 91 / 140 / 9 | 0.0015 | 0.0050 * |
| units built | 240 | 57.704 | 58.325 | 0.621 [-2.246, 3.492] | 107 / 122 / 11 | 0.3549 | 0.7098 |
| production idle fraction | 240 | 0.011 | 0.014 | 0.003 [-0.004, 0.01] | 136 / 87 / 17 | 0.0013 | 0.0050 * |
| average credits (float) | 240 | 6179 | 6985 | 806 [533, 1098] | 86 / 154 / 0 | <0.0001 | <0.0001 * |
| first attack s | 240 | 149 | 134 | -15.317 [-20.238, -10.875] | 0 / 40 / 200 (n/d) | <0.0001 | <0.0001 * |
| duration s | 240 | 825 | 1001 | 176 [127, 224] | 126 / 74 / 40 (n/d) | 0.0003 | 0.0014 * |
| activations /10 min | 240 | 2.799 | 6.086 | 3.287 [3.056, 3.51] | 229 / 11 / 0 (n/d) | <0.0001 | <0.0001 * |
| invalid plan rate | 240 | 0 | 0 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| USD per match | 240 | 0 | 2.233 | 2.233 [2.154, 2.311] | 0 / 240 / 0 | <0.0001 | <0.0001 * |

### llm+fast vs selector (240 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 240 | 0.654 | 0.367 | -0.288 [-0.354, -0.217] | 10 / 79 / 151 | <0.0001 | <0.0001 * |
| final asset margin | 240 | 4824 | -9486 | -14310 [-18203, -10402] | 71 / 169 / 0 | <0.0001 | <0.0001 * |
| value destroyed | 240 | 12058 | 9068 | -2989 [-4078, -1898] | 90 / 148 / 2 | 0.0002 | 0.0012 * |
| value lost | 240 | 13750 | 22900 | 9151 [7063, 11220] | 72 / 167 / 1 | <0.0001 | <0.0001 * |
| trade share | 240 | 0.577 | 0.358 | -0.219 [-0.259, -0.178] | 48 / 192 / 0 | <0.0001 | <0.0001 * |
| peak army value | 240 | 3593 | 3112 | -481 [-685, -275] | 87 / 143 / 10 | 0.0003 | 0.0013 * |
| units built | 240 | 57.704 | 58.817 | 1.113 [-1.613, 3.717] | 107 / 126 / 7 | 0.2382 | 0.4765 |
| production idle fraction | 240 | 0.011 | 0.011 | 0 [-0.005, 0.007] | 126 / 97 / 17 | 0.0605 | 0.1816 |
| average credits (float) | 240 | 6179 | 7067 | 889 [566, 1209] | 86 / 154 / 0 | <0.0001 | <0.0001 * |
| first attack s | 240 | 149 | 135 | -13.833 [-18.283, -9.775] | 0 / 40 / 200 (n/d) | <0.0001 | <0.0001 * |
| duration s | 240 | 825 | 1005 | 180 [134, 226] | 122 / 75 / 43 (n/d) | 0.0010 | 0.0040 * |
| activations /10 min | 240 | 2.799 | 6.469 | 3.67 [3.445, 3.898] | 236 / 4 / 0 (n/d) | <0.0001 | <0.0001 * |
| invalid plan rate | 240 | 0 | 0 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| USD per match | 240 | 0 | 3.476 | 3.476 [3.35, 3.594] | 0 / 240 / 0 | <0.0001 | <0.0001 * |

### llm-shadow vs selector (240 pairs)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 240 | 0.654 | 0.654 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| final asset margin | 240 | 4824 | 4824 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| value destroyed | 240 | 12058 | 12058 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| value lost | 240 | 13750 | 13750 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| trade share | 240 | 0.577 | 0.577 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| peak army value | 240 | 3593 | 3593 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| units built | 240 | 57.704 | 57.704 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| production idle fraction | 240 | 0.011 | 0.011 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| average credits (float) | 240 | 6179 | 6179 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| first attack s | 240 | 149 | 149 | 0 [0, 0] | 0 / 0 / 240 (n/d) | 1.0000 | 1.0000 |
| duration s | 240 | 825 | 825 | 0 [0, 0] | 0 / 0 / 240 (n/d) | 1.0000 | 1.0000 |
| activations /10 min | 240 | 2.799 | 2.799 | 0 [0, 0] | 0 / 0 / 240 (n/d) | 1.0000 | 1.0000 |
| invalid plan rate | 240 | 0 | 0 | 0 [0, 0] | 0 / 0 / 240 | 1.0000 | 1.0000 |
| USD per match | 240 | 0 | 1.705 | 1.705 [1.603, 1.813] | 0 / 240 / 0 | <0.0001 | <0.0001 * |

## Paired differences vs selector

Pairs share opponent, map and seed. Difference = arm − baseline, mean with a 95% seeded bootstrap interval; better/worse/tied count pairs by the metric's direction (n/d: no better direction, counted as higher/lower); p is the exact two-sided sign test over untied pairs, Holm-adjusted over the arm's metrics. `*` marks Holm p < 0.05.

### bandit vs selector (760 pairs; 160 identical to another opponent's pair collapsed)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 760 | 0.88 | 0.867 | -0.013 [-0.036, 0.009] | 32 / 42 / 686 | 0.2954 | 1.0000 |
| final asset margin | 760 | 9026 | 8630 | -396 [-1365, 546] | 259 / 276 / 225 | 0.4891 | 1.0000 |
| value destroyed | 760 | 9901 | 10097 | 196 [-208, 601] | 231 / 211 / 318 | 0.3662 | 1.0000 |
| value lost | 760 | 5337 | 6350 | 1013 [411, 1646] | 170 / 231 / 359 | 0.0027 | 0.0296 * |
| trade share | 760 | 0.817 | 0.797 | -0.021 [-0.035, -0.008] | 201 / 244 / 315 | 0.0464 | 0.3709 |
| peak army value | 760 | 3460 | 3530 | 69.868 [-57.5, 206] | 183 / 286 / 291 | <0.0001 | <0.0001 * |
| units built | 760 | 37.667 | 38.986 | 1.318 [0.462, 2.159] | 225 / 239 / 296 | 0.5462 | 1.0000 |
| production idle fraction | 760 | 0.011 | 0.009 | -0.002 [-0.004, 0] | 283 / 310 / 167 | 0.2857 | 1.0000 |
| average credits (float) | 760 | 6644 | 6611 | -33.612 [-99.528, 30.688] | 259 / 366 / 135 | <0.0001 | 0.0003 * |
| first attack s | 760 | 135 | 139 | 3.978 [1.497, 7.416] | 8 / 0 / 752 (n/d) | 0.0078 | 0.0781 |
| duration s | 760 | 514 | 536 | 21.914 [6.933, 38.082] | 255 / 309 / 196 (n/d) | 0.0255 | 0.2299 |
| activations /10 min | 760 | 2.268 | 6.557 | 4.289 [3.979, 4.585] | 641 / 100 / 19 (n/d) | <0.0001 | <0.0001 * |
| invalid plan rate | 760 | 0 | 0 | 0 [0, 0] | 0 / 0 / 760 | 1.0000 | 1.0000 |
| USD per match | 760 | 0 | 0 | 0 [0, 0] | 0 / 0 / 760 | 1.0000 | 1.0000 |

### distilled vs selector (602 pairs; 318 identical to another opponent's pair collapsed)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 602 | 0.849 | 0.703 | -0.146 [-0.181, -0.113] | 18 / 106 / 478 | <0.0001 | <0.0001 * |
| final asset margin | 602 | 8823 | 644 | -8179 [-9776, -6503] | 145 / 332 / 125 | <0.0001 | <0.0001 * |
| value destroyed | 602 | 10528 | 8491 | -2036 [-2548, -1515] | 149 / 285 / 168 | <0.0001 | <0.0001 * |
| value lost | 602 | 6703 | 11661 | 4958 [3974, 5970] | 118 / 290 / 194 | <0.0001 | <0.0001 * |
| trade share | 602 | 0.773 | 0.635 | -0.138 [-0.157, -0.118] | 117 / 326 / 159 | <0.0001 | <0.0001 * |
| peak army value | 602 | 3469 | 2978 | -492 [-592, -381] | 106 / 388 / 108 | <0.0001 | <0.0001 * |
| units built | 602 | 42.89 | 42.37 | -0.52 [-2.164, 1.218] | 134 / 371 / 97 | <0.0001 | <0.0001 * |
| production idle fraction | 602 | 0.012 | 0.017 | 0.005 [0, 0.01] | 201 / 259 / 142 | 0.0078 | 0.0312 * |
| average credits (float) | 602 | 6582 | 7273 | 691 [526, 861] | 139 / 379 / 84 | <0.0001 | <0.0001 * |
| first attack s | 602 | 135 | 135 | 0 [0, 0] | 0 / 0 / 602 (n/d) | 1.0000 | 1.0000 |
| duration s | 602 | 570 | 715 | 145 [116, 173] | 198 / 231 / 173 (n/d) | 0.1223 | 0.3668 |
| activations /10 min | 602 | 2.331 | 3.011 | 0.68 [0.549, 0.801] | 390 / 94 / 118 (n/d) | <0.0001 | <0.0001 * |
| invalid plan rate | 602 | 0 | 0 | 0 [0, 0] | 0 / 0 / 602 | 1.0000 | 1.0000 |
| USD per match | 602 | 0 | 0.006 | 0.006 [0.004, 0.008] | 0 / 49 / 553 | <0.0001 | <0.0001 * |

### llm vs selector (622 pairs; 298 identical to another opponent's pair collapsed)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 622 | 0.854 | 0.557 | -0.297 [-0.334, -0.257] | 11 / 196 / 415 | <0.0001 | <0.0001 * |
| final asset margin | 622 | 8891 | -2923 | -11814 [-13519, -10003] | 179 / 424 / 19 | <0.0001 | <0.0001 * |
| value destroyed | 622 | 10444 | 8489 | -1955 [-2578, -1279] | 250 / 316 / 56 | 0.0062 | 0.0250 * |
| value lost | 622 | 6493 | 14893 | 8400 [7281, 9529] | 91 / 426 / 105 | <0.0001 | <0.0001 * |
| trade share | 622 | 0.779 | 0.539 | -0.239 [-0.263, -0.214] | 78 / 460 / 84 | <0.0001 | <0.0001 * |
| peak army value | 622 | 3457 | 2882 | -575 [-687, -461] | 156 / 442 / 24 | <0.0001 | <0.0001 * |
| units built | 622 | 42.206 | 46.326 | 4.121 [2.516, 5.651] | 276 / 322 / 24 | 0.0657 | 0.1764 |
| production idle fraction | 622 | 0.012 | 0.013 | 0.001 [-0.003, 0.005] | 320 / 273 / 29 | 0.0588 | 0.1764 |
| average credits (float) | 622 | 6587 | 7032 | 445 [300, 610] | 231 / 391 / 0 | <0.0001 | <0.0001 * |
| first attack s | 622 | 135 | 129 | -5.91 [-7.867, -4.124] | 0 / 40 / 582 (n/d) | <0.0001 | <0.0001 * |
| duration s | 622 | 562 | 768 | 206 [180, 233] | 318 / 245 / 59 (n/d) | 0.0024 | 0.0119 * |
| activations /10 min | 622 | 2.316 | 5.318 | 3.003 [2.879, 3.124] | 607 / 14 / 1 (n/d) | <0.0001 | <0.0001 * |
| invalid plan rate | 622 | 0 | 0 | 0 [0, 0] | 0 / 0 / 622 | 1.0000 | 1.0000 |
| USD per match | 622 | 0 | 1.672 | 1.672 [1.599, 1.745] | 0 / 622 / 0 | <0.0001 | <0.0001 * |

### llm+fast vs selector (623 pairs; 297 identical to another opponent's pair collapsed)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 623 | 0.854 | 0.579 | -0.274 [-0.313, -0.236] | 13 / 184 / 426 | <0.0001 | <0.0001 * |
| final asset margin | 623 | 8909 | -1890 | -10799 [-12615, -8939] | 180 / 424 / 19 | <0.0001 | <0.0001 * |
| value destroyed | 623 | 10447 | 8412 | -2035 [-2664, -1385] | 249 / 316 / 58 | 0.0054 | 0.0218 * |
| value lost | 623 | 6487 | 14381 | 7894 [6863, 8909] | 96 / 423 / 104 | <0.0001 | <0.0001 * |
| trade share | 623 | 0.779 | 0.54 | -0.238 [-0.262, -0.212] | 77 / 462 / 84 | <0.0001 | <0.0001 * |
| peak army value | 623 | 3457 | 2877 | -580 [-695, -463] | 150 / 450 / 23 | <0.0001 | <0.0001 * |
| units built | 623 | 42.193 | 46.213 | 4.021 [2.642, 5.522] | 282 / 321 / 20 | 0.1217 | 0.3650 |
| production idle fraction | 623 | 0.012 | 0.015 | 0.004 [-0.001, 0.008] | 305 / 291 / 27 | 0.5944 | 1.0000 |
| average credits (float) | 623 | 6587 | 7072 | 486 [331, 644] | 228 / 395 / 0 | <0.0001 | <0.0001 * |
| first attack s | 623 | 135 | 130 | -5.329 [-7.067, -3.689] | 0 / 40 / 583 (n/d) | <0.0001 | <0.0001 * |
| duration s | 623 | 562 | 766 | 205 [179, 232] | 318 / 244 / 61 (n/d) | 0.0020 | 0.0102 * |
| activations /10 min | 623 | 2.314 | 5.532 | 3.217 [3.099, 3.346] | 613 / 9 / 1 (n/d) | <0.0001 | <0.0001 * |
| invalid plan rate | 623 | 0 | 0 | 0 [0, 0] | 0 / 0 / 623 | 1.0000 | 1.0000 |
| USD per match | 623 | 0 | 2.588 | 2.588 [2.473, 2.703] | 0 / 623 / 0 | <0.0001 | <0.0001 * |

### llm-shadow vs selector (583 pairs; 337 identical to another opponent's pair collapsed)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 583 | 0.844 | 0.844 | 0 [0, 0] | 0 / 0 / 583 | 1.0000 | 1.0000 |
| final asset margin | 583 | 8408 | 8408 | 0 [0, 0] | 0 / 0 / 583 | 1.0000 | 1.0000 |
| value destroyed | 583 | 10358 | 10358 | 0 [0, 0] | 0 / 0 / 583 | 1.0000 | 1.0000 |
| value lost | 583 | 6729 | 6729 | 0 [0, 0] | 0 / 0 / 583 | 1.0000 | 1.0000 |
| trade share | 583 | 0.773 | 0.773 | 0 [0, 0] | 0 / 0 / 583 | 1.0000 | 1.0000 |
| peak army value | 583 | 3445 | 3445 | 0 [0, 0] | 0 / 0 / 583 | 1.0000 | 1.0000 |
| units built | 583 | 42.467 | 42.467 | 0 [0, 0] | 0 / 0 / 583 | 1.0000 | 1.0000 |
| production idle fraction | 583 | 0.012 | 0.012 | 0 [0, 0] | 0 / 0 / 583 | 1.0000 | 1.0000 |
| average credits (float) | 583 | 6571 | 6571 | 0 [0, 0] | 0 / 0 / 583 | 1.0000 | 1.0000 |
| first attack s | 583 | 136 | 136 | 0 [0, 0] | 0 / 0 / 583 (n/d) | 1.0000 | 1.0000 |
| duration s | 583 | 568 | 568 | 0 [0, 0] | 0 / 0 / 583 (n/d) | 1.0000 | 1.0000 |
| activations /10 min | 583 | 2.328 | 2.328 | 0 [0, 0] | 0 / 0 / 583 (n/d) | 1.0000 | 1.0000 |
| invalid plan rate | 583 | 0 | 0 | 0 [0, 0] | 0 / 0 / 583 | 1.0000 | 1.0000 |
| USD per match | 583 | 0 | 1.129 | 1.129 [1.059, 1.194] | 0 / 583 / 0 | <0.0001 | <0.0001 * |

## Perception bottleneck (belief − oracle)

Pairs share opponent, map and seed. Difference = arm − baseline, mean with a 95% seeded bootstrap interval; better/worse/tied count pairs by the metric's direction (n/d: no better direction, counted as higher/lower); p is the exact two-sided sign test over untied pairs, Holm-adjusted over the arm's metrics. `*` marks Holm p < 0.05.

### llm vs llm-oracle (625 pairs; 295 identical to another opponent's pair collapsed)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 625 | 0.668 | 0.556 | -0.112 [-0.147, -0.078] | 32 / 102 / 491 | <0.0001 | <0.0001 * |
| final asset margin | 625 | 716 | -2913 | -3629 [-5174, -2104] | 249 / 292 / 84 | 0.0709 | 0.2835 |
| value destroyed | 625 | 9547 | 8464 | -1082 [-1631, -564] | 169 / 328 / 128 | <0.0001 | <0.0001 * |
| value lost | 625 | 11803 | 14856 | 3053 [2118, 4039] | 223 / 278 / 124 | 0.0158 | 0.0788 |
| trade share | 625 | 0.607 | 0.538 | -0.069 [-0.088, -0.051] | 187 / 337 / 101 | <0.0001 | <0.0001 * |
| peak army value | 625 | 2979 | 2878 | -100 [-183, -18.72] | 235 / 253 / 137 | 0.4416 | 0.8832 |
| units built | 625 | 50.682 | 46.288 | -4.394 [-5.741, -3.054] | 207 / 293 / 125 | 0.0001 | 0.0008 * |
| production idle fraction | 625 | 0.024 | 0.013 | -0.011 [-0.016, -0.005] | 328 / 226 / 71 | <0.0001 | 0.0001 * |
| average credits (float) | 625 | 6534 | 7019 | 485 [383, 589] | 204 / 394 / 27 | <0.0001 | <0.0001 * |
| first attack s | 625 | 160 | 129 | -30.171 [-32.306, -28.213] | 6 / 590 / 29 (n/d) | <0.0001 | <0.0001 * |
| duration s | 625 | 756 | 770 | 14.277 [-4.213, 33.723] | 248 / 222 / 155 (n/d) | 0.2488 | 0.7464 |
| activations /10 min | 625 | 4.712 | 5.307 | 0.595 [0.439, 0.752] | 343 / 238 / 44 (n/d) | <0.0001 | 0.0001 * |
| invalid plan rate | 625 | 0 | 0 | 0 [0, 0] | 0 / 0 / 625 | 1.0000 | 1.0000 |
| USD per match | 625 | 1.655 | 1.676 | 0.021 [-0.026, 0.069] | 407 / 218 / 0 | <0.0001 | <0.0001 * |

### selector vs selector-oracle (585 pairs; 335 identical to another opponent's pair collapsed)

| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |
|---|---|---|---|---|---|---|---|
| score (win 1, draw ½) | 585 | 0.855 | 0.844 | -0.01 [-0.038, 0.015] | 28 / 34 / 523 | 0.5258 | 1.0000 |
| final asset margin | 585 | 9341 | 8465 | -876 [-1995, 168] | 213 / 315 / 57 | <0.0001 | <0.0001 * |
| value destroyed | 585 | 10442 | 10386 | -55.897 [-520, 377] | 212 / 199 / 174 | 0.5540 | 1.0000 |
| value lost | 585 | 6096 | 6796 | 700 [26.667, 1400] | 178 / 271 / 136 | <0.0001 | <0.0001 * |
| trade share | 585 | 0.788 | 0.771 | -0.018 [-0.033, -0.003] | 203 / 274 / 108 | 0.0013 | 0.0066 * |
| peak army value | 585 | 3300 | 3446 | 146 [55.897, 234] | 353 / 148 / 84 | <0.0001 | <0.0001 * |
| units built | 585 | 40.969 | 42.715 | 1.745 [0.674, 2.803] | 391 / 97 / 97 | <0.0001 | <0.0001 * |
| production idle fraction | 585 | 0.015 | 0.012 | -0.003 [-0.007, 0.001] | 417 / 130 / 38 | <0.0001 | <0.0001 * |
| average credits (float) | 585 | 6693 | 6571 | -122 [-198, -46.528] | 385 / 187 / 13 | <0.0001 | <0.0001 * |
| first attack s | 585 | 214 | 136 | -78.59 [-81.769, -75.222] | 40 / 545 / 0 (n/d) | <0.0001 | <0.0001 * |
| duration s | 585 | 555 | 573 | 17.728 [1.34, 35.547] | 386 / 116 / 83 (n/d) | <0.0001 | <0.0001 * |
| activations /10 min | 585 | 2.216 | 2.355 | 0.138 [0.032, 0.249] | 178 / 382 / 25 (n/d) | <0.0001 | <0.0001 * |
| invalid plan rate | 585 | 0 | 0 | 0 [0, 0] | 0 / 0 / 585 | 1.0000 | 1.0000 |
| USD per match | 585 | 0 | 0 | 0 [0, 0] | 0 / 0 / 585 | 1.0000 | 1.0000 |

## Economy, production and combat (arm side, per-match averages)

Production idle: seconds with a factory, nothing queued and credits for the cheapest item, over match seconds. Trade efficiency: enemy value destroyed in combat / own value lost in combat (pooled over matches).

Time to first attack: first second a combat unit stood in a region holding an enemy structure (mean over matches where it happened; count in brackets).

| Arm | Duration s | Units built | Buildings built | Peak army value | Idle fraction | Avg credits | Final assets (arm / opp) | Trade efficiency | First attack s |
|---|---|---|---|---|---|---|---|---|---|
| bandit | 494 | 37.6 | 5.3 | 3424 | 0.010 | 6676 | 20042 / 11243 | 1.86 (8991450/4842100) | 136 (920/920) |
| distilled | 574 | 35.7 | 6.6 | 2957 | 0.015 | 7184 | 18918 / 15285 | 1.06 (7553400/7137400) | 133 (920/920) |
| llm | 614 | 38.4 | 7.4 | 2829 | 0.012 | 7046 | 17846 / 17115 | 0.80 (7533650/9372900) | 129 (920/920) |
| llm+fast | 613 | 38.4 | 7.4 | 2828 | 0.015 | 7077 | 18352 / 16919 | 0.83 (7491800/9062200) | 129 (920/920) |
| llm-oracle | 603 | 41.3 | 7.3 | 2891 | 0.020 | 6716 | 18893 / 15681 | 1.10 (8232800/7454400) | 157 (920/920) |
| llm-shadow | 476 | 36.5 | 5.6 | 3366 | 0.011 | 6704 | 20020 / 10915 | 2.17 (8824450/4073600) | 133 (920/920) |
| selector | 476 | 36.5 | 5.6 | 3366 | 0.011 | 6704 | 20020 / 10915 | 2.17 (8824450/4073600) | 133 (920/920) |
| selector-oracle | 462 | 35.0 | 5.7 | 3221 | 0.014 | 6798 | 19971 / 10490 | 2.39 (8793050/3672700) | 214 (920/920) |

## Strategy layer (arm side)

Proposals, invalid plans and lateness are the arm's primary strategist's: rejected / proposals; seconds from a proposal's snapshot to its validation; late-discard count / proposals. Fallback and emergency proposals (immediate and always valid) are counted apart so they cannot flatter the strategist under test. Shadow columns are the shadow strategist's own lateness and would-be invalid plans. Churn: activations and posture flips per 10 game minutes.

Shadow agreement: shadow proposals naming the same playbook as the primary proposal for the same request / shadow proposals whose request got a primary proposal.

| Arm | Proposals | Invalid plans | Mean lateness s | Late-discarded | Fallback proposals | Failed requests | Shadow proposals | Shadow invalid | Shadow mean lateness s | Shadow agreement | Activations /10 min | Posture flips /10 min |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| bandit | 24074 | 0/24074 (0.000) | 0.00 | 0/24074 (0.000) | 3754 | 0 | 0 | n/a | n/a | n/a | 6.37 | 2.06 |
| distilled | 28446 | 0/28446 (0.000) | 0.01 | 1/28446 (0.000) | 133 | 6 | 0 | n/a | n/a | n/a | 3.33 | 1.26 |
| llm | 30496 | 0/30496 (0.000) | 4.00 | 60/30496 (0.002) | 2149 | 139 | 0 | n/a | n/a | n/a | 5.49 | 1.55 |
| llm+fast | 112689 | 0/112689 (0.000) | 4.00 | 641/112689 (0.006) | 2342 | 703 | 0 | n/a | n/a | n/a | 5.78 | 1.77 |
| llm-oracle | 29460 | 0/29460 (0.000) | 4.00 | 20/29460 (0.001) | 1941 | 77 | 0 | n/a | n/a | n/a | 4.66 | 1.25 |
| llm-shadow | 23121 | 0/23121 (0.000) | 0.00 | 0/23121 (0.000) | 0 | 0 | 22640 | 13/22640 (0.001) | 4.00 | 3569/22640 (0.158) | 2.69 | 1.04 |
| selector | 23121 | 0/23121 (0.000) | 0.00 | 0/23121 (0.000) | 0 | 0 | 0 | n/a | n/a | n/a | 2.69 | 1.04 |
| selector-oracle | 22341 | 0/22341 (0.000) | 0.00 | 0/22341 (0.000) | 0 | 0 | 0 | n/a | n/a | n/a | 2.27 | 0.75 |

## Command gate and simulator rejections (arm side, totals)

- **bandit**: 0 dropped by the gate (none); 2877 commands rejected by the simulator.
- **distilled**: 0 dropped by the gate (none); 4831 commands rejected by the simulator.
- **llm**: 0 dropped by the gate (none); 4670 commands rejected by the simulator.
- **llm+fast**: 0 dropped by the gate (none); 4210 commands rejected by the simulator.
- **llm-oracle**: 0 dropped by the gate (none); 53925 commands rejected by the simulator.
- **llm-shadow**: 0 dropped by the gate (none); 2003 commands rejected by the simulator.
- **selector**: 0 dropped by the gate (none); 2003 commands rejected by the simulator.
- **selector-oracle**: 0 dropped by the gate (none); 30938 commands rejected by the simulator.

## Inference cost (arm side)

- **bandit** (no model): 0 in / 0 out tokens and $0.0000 per match, of which $0.0000 on failed requests.
- **distilled** (claude-opus-5): 672 in / 20 out tokens and $0.0039 per match, of which $0.0000 on failed requests (fake client: tokens estimated from prompt size, priced at the list rate; not a measurement).
- **llm** (claude-opus-5): 222738 in / 7472 out tokens and $1.3005 per match, of which $0.0000 on failed requests (fake client: tokens estimated from prompt size, priced at the list rate; not a measurement).
- **llm+fast** (claude-opus-5): 828042 in / 26920 out tokens and $2.0194 per match, of which $0.0000 on failed requests; served by claude-haiku-4-5 x81724, claude-opus-5 x30965 (fake client: tokens estimated from prompt size, priced at the list rate; not a measurement).
- **llm-oracle** (claude-opus-5): 222439 in / 7219 out tokens and $1.2927 per match, of which $0.0000 on failed requests (fake client: tokens estimated from prompt size, priced at the list rate; not a measurement).
- **llm-shadow** (claude-opus-5): 156805 in / 5531 out tokens and $0.9223 per match, of which $0.0000 on failed requests (fake client: tokens estimated from prompt size, priced at the list rate; not a measurement).
- **selector** (no model): 0 in / 0 out tokens and $0.0000 per match, of which $0.0000 on failed requests.
- **selector-oracle** (no model): 0 in / 0 out tokens and $0.0000 per match, of which $0.0000 on failed requests.

## Distillation

Decisions: primary requests the distilled strategist answered. Escalations: those it handed to the LLM (out of distribution, low confidence, or no trained playbook for the faction). Cost per match includes the escalations' tokens; the teacher column is the matching `llm` arm in this run.

| Arm | Decisions | Escalations (rate) | USD per match | Teacher USD per match |
|---|---|---|---|---|
| distilled | 28457 | 94/28457 (0.003) | $0.0039 | $1.3005 |

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

