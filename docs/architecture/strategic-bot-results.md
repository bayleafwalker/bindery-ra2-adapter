# Strategic bot: final tournament results

Final evaluation of the hierarchical bot described in
[`strategic-bot.md`](strategic-bot.md). The current rerun was made on
2026-09-27 at `c80c935` (`feat/strategic-bot`).

## Read this first

- **Current rerun.** The exact documented matrix completed at `c80c935`:
  5,520 standard matches (23 opponents) and 6,720 contested matches (28
  opponents), six seeds per cell, with `--llm-fake`. The generated reports
  classify the standard selector rate (599/690, 0.868) and contested selector
  rate (705/840, 0.839) as saturated across the full matrix; compare the
  calibrated live styles and held-out opponent cells, not those aggregate
  rates. On contested held-out maps, selector was 285/336 (0.848,
  95% Wilson [0.806, 0.883]); the fake `llm` arm was 188/336 (0.560,
  [0.506, 0.612]); and `distilled` was 231/336 (0.688, [0.636, 0.735]).
  The historical tables below remain an archived pre-merge record, not the
  current benchmark. No live-LLM result is claimed.

- **Environment.** Every number here comes from the bindery region simulator
  (`src/Bindery.Ra2.Bot.Sim`), a deterministic region-graph approximation of a
  two-player RA2/YR skirmish, with the hand-authored approximate ruleset
  `bindery-sim-approx` (costs, strengths, prerequisites and a weapon-vs-armour
  table typed in by hand, not read from retail `rulesmd.ini`). It is not
  retail RA2/YR. Results are directional: they rank arms inside this
  simulator and say nothing firm about retail play.
- **No live model.** No Claude credential exists on the host that ran this.
  Every LLM arm (`llm-shadow`, `llm`, `llm+fast`, `llm-oracle`, and the
  escalations of `distilled`) ran against `FakeMessageClient`
  (`--llm-fake`): a deterministic, scripted policy (`fake-counter-v2`) that
  answers the real prompt with a valid intent draft. These rows measure the
  LLM pipeline and that scripted policy, not Claude. Token counts are
  estimated from prompt size and priced at the list rate; they are not
  measurements.
- **Oracle arms** (`selector-oracle`, `llm-oracle`) see full simulator state
  instead of their fog-limited belief. They are diagnostics, not playable
  bots.

## Setup

One command per benchmark (the script is
`arena-final/run.sh` in the session scratch directory; outputs in
`arena-final/{standard,contested}/` there: `results.json`, `report.md`,
`probes.json`, datasets):

```bash
nix shell nixpkgs#dotnet-sdk_8 -c dotnet run --project tools/Bindery.Ra2.Bot.Arena -c Release -- run \
  --arms selector,bandit,llm-shadow,llm,llm+fast,distilled,selector-oracle,llm-oracle \
  --maps all --opponents <list below> --seeds 6 --llm-fake --benchmark standard|contested \
  --no-decisions --out <dir>
```

| Dimension | Values |
|---|---|
| Arms | `selector`, `bandit`, `llm-shadow`, `llm`, `llm+fast`, `distilled`, `selector-oracle`, `llm-oracle` |
| Maps | training: `twin-valley`, `river-crossing`, `island-bridges`; held-out: `open-steppe`, `fortress-choke` |
| Opponents (both benchmarks) | independent scripted AI at every difficulty: training `ai-rush`, `ai-balanced`, `ai-turtle`, `ai-air`, held-out `ai-horde`, `ai-armor`, each `:easy`, `:medium`, `:hard` (18); pinned styles on the frozen 7f3e2c7 stack: `rush`, `turtle`, `tech`, `harass`, `balanced` (5) |
| Extra opponents (contested only) | the five pinned styles on the live stack: `live-balanced`, `live-rush`, `live-tech`, `live-turtle`, `live-harass` (the set the contested benchmark is calibrated on) |
| Seeds | 6 per cell; the arm plays Allied on odd seeds and Soviet on even seeds, so each rate has an equal faction mix |
| Matches | standard 8 arms × 5 maps × 23 opponents × 6 seeds = 5,520 (1,305 s); contested 8 × 5 × 28 × 6 = 6,720 (2,492 s) |
| Benchmarks | `standard`: fair economy, noiseless combat. `contested`: Allied side starts with 20,000 credits against 10,000 (the fixture favours Soviet), ±25% seeded combat noise |
| Distilled arm | trained on the belief `llm` arm's primary decisions from **training maps against training opponents only** in the same run: 5,736 examples (standard), 9,470 (contested). Held-out maps and held-out opponents never enter the dataset |

A first pass with 5 seeds (4,600 standard matches) was discarded because an odd
seed count gives the arm Allied three times in five; its rates agree with the
6-seed ones within their intervals.

**Match duplication.** The frozen pinned styles and the three `ai-air`
difficulties often lose before they have made a decision that differs from one
another, so the arm plays byte-identical games against them. Rates below count
every match; the "distinct" column counts each identical game once. Read the
distinct interval when the two differ.

Intervals are 95% Wilson score intervals. Paired tables pair matches by
opponent, map and seed, and give a seeded bootstrap interval, an exact sign test
and Holm-adjusted p over each arm's metrics.

## Headline: contested benchmark

### Win rate by arm and map split (all 28 opponents)

| Arm | Training maps | 95% CI | Distinct games (wins/games) | Held-out maps | 95% CI | Distinct games |
|---|---|---|---|---|---|---|
| selector | 437/504 (0.867) | [0.835, 0.894] | 288/355 [0.767, 0.849] | 263/336 (0.783) | [0.736, 0.824] | 162/234 [0.630, 0.748] |
| bandit | 444/504 (0.881) | [0.850, 0.906] | 444/504 [0.850, 0.906] | 257/336 (0.765) | [0.717, 0.807] | 257/336 [0.717, 0.807] |
| llm-shadow | 437/504 (0.867) | [0.835, 0.894] | 288/355 [0.767, 0.849] | 263/336 (0.783) | [0.736, 0.824] | 162/234 [0.630, 0.748] |
| llm (fake) | 401/504 (0.796) | [0.758, 0.829] | 256/358 [0.666, 0.759] | 245/336 (0.729) | [0.679, 0.774] | 149/240 [0.558, 0.680] |
| llm+fast (fake) | 414/504 (0.821) | [0.786, 0.852] | 267/356 [0.703, 0.792] | 255/336 (0.759) | [0.710, 0.802] | 152/233 [0.589, 0.711] |
| distilled | 411/504 (0.815) | [0.779, 0.847] | 266/356 [0.700, 0.790] | 264/336 (0.786) | [0.739, 0.826] | 164/236 [0.633, 0.750] |
| selector-oracle | 450/504 (0.893) | [0.863, 0.917] | 304/358 [0.808, 0.883] | 294/336 (0.875) | [0.835, 0.906] | 204/246 [0.777, 0.871] |
| llm-oracle (fake) | 444/504 (0.881) | [0.850, 0.906] | 301/361 [0.792, 0.869] | 284/336 (0.845) | [0.803, 0.880] | 191/243 [0.730, 0.833] |

Draws in the contested run: 8 of 6,720 (bandit 2, llm+fast 2, selector, llm-shadow, distilled, llm-oracle 1 each); rates count them as non-wins. The report's saturation check fires
(selector 0.834 over all 840 matches, above the 30–70% band) because the
training `ai-*` and frozen pinned opponents lose nearly every contested game.
The contested setting was calibrated on the live styles, and against those
the selector wins 84/150 (0.560), inside the band. Compare arms on the
live-style and held-out-opponent columns below.

### Win rate by opponent group (all maps)

| Arm | Live styles (calibrated) | Held-out `ai-*` | Training `ai-*` | Frozen pinned |
|---|---|---|---|---|
| selector | 84/150 0.560 [0.480, 0.637] | 123/180 0.683 [0.612, 0.747] | 343/360 0.953 [0.926, 0.970] | 150/150 1.000 [0.975, 1.000] |
| bandit | 87/150 0.580 [0.500, 0.656] | 118/180 0.656 [0.584, 0.721] | 346/360 0.961 [0.936, 0.977] | 150/150 1.000 [0.975, 1.000] |
| llm-shadow | 84/150 0.560 [0.480, 0.637] | 123/180 0.683 [0.612, 0.747] | 343/360 0.953 [0.926, 0.970] | 150/150 1.000 [0.975, 1.000] |
| llm (fake) | 58/150 0.387 [0.312, 0.467] | 111/180 0.617 [0.544, 0.685] | 327/360 0.908 [0.874, 0.934] | 150/150 1.000 [0.975, 1.000] |
| llm+fast (fake) | 75/150 0.500 [0.421, 0.579] | 113/180 0.628 [0.555, 0.695] | 331/360 0.919 [0.887, 0.943] | 150/150 1.000 [0.975, 1.000] |
| distilled | 64/150 0.427 [0.350, 0.507] | 131/180 0.728 [0.659, 0.788] | 330/360 0.917 [0.884, 0.941] | 150/150 1.000 [0.975, 1.000] |
| selector-oracle | 88/150 0.587 [0.507, 0.662] | 152/180 0.844 [0.784, 0.890] | 354/360 0.983 [0.964, 0.992] | 150/150 1.000 [0.975, 1.000] |
| llm-oracle (fake) | 81/150 0.540 [0.460, 0.618] | 145/180 0.806 [0.742, 0.857] | 352/360 0.978 [0.957, 0.989] | 150/150 1.000 [0.975, 1.000] |

### Held-out opponents by map split (the fully out-of-sample cell is the last column)

| Arm | Held-out opponents, training maps | Held-out opponents, held-out maps |
|---|---|---|
| selector | 82/108 0.759 [0.671, 0.830] | 41/72 0.569 [0.454, 0.677] |
| bandit | 77/108 0.713 [0.621, 0.790] | 41/72 0.569 [0.454, 0.677] |
| llm-shadow | 82/108 0.759 [0.671, 0.830] | 41/72 0.569 [0.454, 0.677] |
| llm (fake) | 72/108 0.667 [0.573, 0.748] | 39/72 0.542 [0.427, 0.652] |
| llm+fast (fake) | 72/108 0.667 [0.573, 0.748] | 41/72 0.569 [0.454, 0.677] |
| distilled | 81/108 0.750 [0.661, 0.822] | 50/72 0.694 [0.580, 0.789] |
| selector-oracle | 83/108 0.769 [0.681, 0.838] | 69/72 0.958 [0.885, 0.986] |
| llm-oracle (fake) | 78/108 0.722 [0.631, 0.798] | 67/72 0.931 [0.848, 0.970] |

The held-out opponents are out of sample for learning, distillation and
tuning. They are not out of sample for calibration: their difficulty was set
against the selector on all five maps.

### Per-opponent breakdown (wins / 30 matches; in brackets training-map wins of 18 + held-out-map wins of 12)

| Opponent | selector | bandit | llm-shadow | llm | llm+fast | distilled | selector-oracle | llm-oracle |
|---|---|---|---|---|---|---|---|---|
| ai-air:easy | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 29 (18+11) | 30 (18+12) |
| ai-air:medium | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 29 (18+11) | 29 (18+11) |
| ai-air:hard | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 29 (18+11) | 29 (18+11) |
| ai-balanced:easy | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) |
| ai-balanced:medium | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) |
| ai-balanced:hard | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) |
| ai-rush:easy | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) |
| ai-rush:medium | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) |
| ai-rush:hard | 29 (17+12) | 30 (18+12) | 29 (17+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) |
| ai-turtle:easy | 25 (18+7) | 26 (18+8) | 25 (18+7) | 21 (15+6) | 21 (14+7) | 23 (16+7) | 30 (18+12) | 28 (16+12) |
| ai-turtle:medium | 25 (18+7) | 25 (18+7) | 25 (18+7) | 18 (13+5) | 20 (13+7) | 18 (11+7) | 30 (18+12) | 29 (17+12) |
| ai-turtle:hard | 24 (16+8) | 25 (18+7) | 24 (16+8) | 18 (12+6) | 20 (13+7) | 19 (12+7) | 27 (15+12) | 27 (16+11) |
| **ai-horde:easy** (held out) | 23 (15+8) | 30 (18+12) | 23 (15+8) | 27 (15+12) | 28 (16+12) | 29 (17+12) | 27 (15+12) | 27 (15+12) |
| **ai-horde:medium** (held out) | 18 (12+6) | 15 (11+4) | 18 (12+6) | 21 (15+6) | 20 (14+6) | 25 (16+9) | 27 (15+12) | 28 (16+12) |
| **ai-horde:hard** (held out) | 19 (12+7) | 18 (12+6) | 19 (12+7) | 14 (10+4) | 21 (12+9) | 20 (12+8) | 22 (12+10) | 20 (8+12) |
| **ai-armor:easy** (held out) | 23 (15+8) | 22 (15+7) | 23 (15+8) | 19 (11+8) | 18 (11+7) | 22 (13+9) | 26 (15+11) | 27 (15+12) |
| **ai-armor:medium** (held out) | 20 (14+6) | 18 (10+8) | 20 (14+6) | 15 (10+5) | 12 (9+3) | 16 (11+5) | 26 (14+12) | 21 (12+9) |
| **ai-armor:hard** (held out) | 20 (14+6) | 15 (11+4) | 20 (14+6) | 15 (11+4) | 14 (10+4) | 19 (12+7) | 24 (12+12) | 22 (12+10) |
| balanced, harass, rush, tech, turtle (frozen, each) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) | 30 (18+12) |
| live-balanced | 19 (11+8) | 18 (12+6) | 19 (11+8) | 13 (8+5) | 15 (8+7) | 15 (8+7) | 19 (14+5) | 17 (12+5) |
| live-rush | 19 (13+6) | 22 (15+7) | 19 (13+6) | 17 (11+6) | 22 (15+7) | 18 (11+7) | 20 (16+4) | 18 (14+4) |
| live-tech | 19 (10+9) | 22 (16+6) | 19 (10+9) | 11 (8+3) | 15 (11+4) | 14 (10+4) | 22 (12+10) | 15 (12+3) |
| live-turtle | 11 (8+3) | 10 (8+2) | 11 (8+3) | 5 (3+2) | 8 (6+2) | 7 (5+2) | 15 (13+2) | 17 (15+2) |
| live-harass | 16 (10+6) | 15 (10+5) | 16 (10+6) | 12 (7+5) | 15 (10+5) | 10 (5+5) | 12 (9+3) | 14 (12+2) |

Difficulty barely matters for the scripted AI in this simulator: `ai-air`,
`ai-balanced` and `ai-rush` lose at every level, and `ai-air`'s three levels
often give identical games.

### Paired against the selector (contested, all opponents; identical games collapsed)

| Arm | Pairs | Score difference (win 1, draw ½) [95% CI] | Better / worse / tied | Holm p | Other significant shifts (Holm p < 0.05) |
|---|---|---|---|---|---|
| bandit | 840 | +0.002 [−0.018, +0.023] | 41 / 39 / 760 | 1.0 | peak army +232; first attack +10.6 s; activations +5.6 per 10 min |
| llm-shadow | 589 | 0 [0, 0] | 0 / 0 / 589 | 1.0 | none: the shadow never acts, so it plays the selector's games exactly |
| llm (fake) | 603 | −0.089 [−0.124, −0.056] | 27 / 81 / 495 | < 0.0001 | activations +2.8 per 10 min |
| llm+fast (fake) | 601 | −0.051 [−0.082, −0.020] | 34 / 64 / 503 | 0.016 | activations +2.8 per 10 min |
| distilled | 598 | −0.037 [−0.067, −0.008] | 32 / 55 / 511 | 0.089 | activations +0.7 per 10 min |

On held-out opponents only (180 pairs per arm, `report.md` "Held-out
opponents"), no arm's score differs from the selector's at Holm p < 0.05;
`distilled` destroys more value there (+3,214 [1,387, 5,191], Holm p 0.0008).

### Economy, production, combat (contested, arm side, per-match means over 840 matches)

| Arm | Duration s | Units built | Buildings built | Peak army value | Production idle fraction | Avg credits | Final assets (arm / opp) | Trade efficiency (destroyed/lost) | First attack s (matches) |
|---|---|---|---|---|---|---|---|---|---|
| selector | 474 | 51.7 | 7.1 | 4734 | 0.039 | 9179 | 17832 / 11730 | 1.54 | 133 (827/840) |
| bandit | 497 | 49.4 | 8.8 | 4966 | 0.042 | 8653 | 18635 / 11903 | 1.68 | 144 (833/840) |
| llm-shadow | 474 | 51.7 | 7.1 | 4734 | 0.039 | 9179 | 17832 / 11730 | 1.54 | 133 (827/840) |
| llm (fake) | 508 | 52.7 | 7.4 | 4706 | 0.039 | 9266 | 17528 / 12604 | 1.36 | 130 (822/840) |
| llm+fast (fake) | 502 | 51.7 | 7.5 | 4619 | 0.040 | 9238 | 17769 / 12338 | 1.43 | 131 (827/840) |
| distilled | 493 | 51.9 | 7.3 | 4952 | 0.037 | 9332 | 17978 / 11787 | 1.54 | 131 (822/840) |
| selector-oracle | 510 | 54.4 | 7.4 | 6558 | 0.042 | 9121 | 20589 / 11080 | 2.21 | 210 (815/840) |
| llm-oracle (fake) | 521 | 51.9 | 7.7 | 6123 | 0.039 | 9275 | 20341 / 11380 | 2.14 | 195 (802/840) |

### Strategy layer, cost and safety (contested)

| Arm | Proposals | Invalid plans | Mean lateness s | Late-discarded | Activations /10 min | Posture flips /10 min | USD per match (estimated, fake client) |
|---|---|---|---|---|---|---|---|
| selector | 22502 | 0 | 0.00 | 0 | 2.82 | 0.84 | 0 |
| bandit | 25202 | 0 | 0.00 | 0 | 7.81 | 4.42 | 0 |
| llm-shadow | 22502 (+21262 shadow) | 0 | 0.00 | 0 | 2.82 | 0.84 | 0.764 |
| llm (fake) | 25709 | 0 | 3.76 | 822 (0.032) | 5.46 | 0.98 | 0.872 |
| llm+fast (fake) | 86589 | 0 | 3.93 | 2909 (0.034) | 5.45 | 1.13 | 1.269 |
| distilled | 23729 | 0 | 0.01 | 2 (0.000) | 3.44 | 0.93 | 0.0026 |
| selector-oracle | 24013 | 0 | 0.00 | 0 | 2.86 | 0.81 | 0 |
| llm-oracle (fake) | 25993 | 0 | 3.76 | 672 (0.026) | 4.72 | 1.00 | 0.881 |

- Shadow agreement (`llm-shadow`: the shadow named the primary's playbook):
  6,511 of 21,262 (0.306).
- Distillation: 23,623 decisions, 72 escalated to the (fake) LLM (0.003),
  $0.0026 per match against the teacher's $0.8718.
- Command gate: 0 commands dropped for a missing lease in every arm.
- Hidden-information leakage: 0 validator `fog.*` rejections in every arm.
  The perturbation probe (two lockstep simulations, hidden state of one
  perturbed at 90 s and 240 s) gave 0 differing strategist-context frames for
  every belief arm in both benchmarks (for example selector 0/1,260, bandit
  0/1,029 contested). It does not apply to oracle arms, which see everything
  by design.

## Standard benchmark

Same arms, the 23 opponents above, fair economy and noiseless combat; 690
matches per arm, no draws except 2 for `llm-oracle`.

| Arm | Training maps | Held-out maps | Held-out `ai-*` (all maps) | Training `ai-*` | Frozen pinned |
|---|---|---|---|---|---|
| selector | 369/414 0.891 [0.858, 0.918] | 225/276 0.815 [0.765, 0.857] | 114/180 0.633 [0.561, 0.700] | 330/360 0.917 [0.884, 0.941] | 150/150 |
| bandit | 362/414 0.874 [0.839, 0.903] | 225/276 0.815 [0.765, 0.857] | 108/180 0.600 [0.527, 0.669] | 329/360 0.914 [0.880, 0.939] | 150/150 |
| llm-shadow | 369/414 0.891 [0.858, 0.918] | 225/276 0.815 [0.765, 0.857] | 114/180 0.633 [0.561, 0.700] | 330/360 0.917 [0.884, 0.941] | 150/150 |
| llm (fake) | 349/414 0.843 [0.805, 0.875] | 224/276 0.812 [0.761, 0.853] | 108/180 0.600 [0.527, 0.669] | 315/360 0.875 [0.837, 0.905] | 150/150 |
| llm+fast (fake) | 354/414 0.855 [0.818, 0.886] | 225/276 0.815 [0.765, 0.857] | 107/180 0.594 [0.521, 0.663] | 322/360 0.894 [0.858, 0.922] | 150/150 |
| distilled | 360/414 0.870 [0.834, 0.899] | 227/276 0.822 [0.773, 0.863] | 113/180 0.628 [0.555, 0.695] | 324/360 0.900 [0.865, 0.927] | 150/150 |
| selector-oracle | 356/414 0.860 [0.823, 0.890] | 255/276 0.924 [0.886, 0.950] | 130/180 0.722 [0.653, 0.782] | 331/360 0.919 [0.887, 0.943] | 150/150 |
| llm-oracle (fake) | 357/414 0.862 [0.826, 0.892] | 257/276 0.931 [0.895, 0.955] | 128/180 0.711 [0.641, 0.772] | 336/360 0.933 [0.903, 0.955] | 150/150 |

Held-out opponents on held-out maps (72 matches each): selector 37 (0.514
[0.401, 0.626]), bandit 38, llm 39, llm+fast 38, distilled 40, selector-oracle
64 (0.889 [0.796, 0.943]), llm-oracle 61.

Paired against the selector (identical games collapsed): `llm` −0.044
[−0.073, −0.015] (13/33, Holm p 0.027); `llm+fast` −0.031 [−0.059, −0.002]
(Holm p 0.29); `distilled` −0.013 [−0.040, 0.013]; `bandit` −0.010 [−0.026,
0.007]; `llm-shadow` identical. Distillation: 187 of 16,721 decisions escalated
(0.011), $0.0082 per match against the teacher's $0.7635. Leakage: 0 everywhere.

Faction mix (standard, all opponents): the selector wins 254/345 as Allied
(0.736) and 340/345 as Soviet (0.986). `selector-oracle` wins 289/345 as
Allied and 322/345 as Soviet. In the contested run the belief selector's
faction gap is similar (307/420 Allied against 393/420 Soviet), while the
oracle arm's gap disappears (373/420 against 371/420).

## Is perception the bottleneck? (belief against oracle)

Same jobs, the only difference being fog-limited belief frames against full
state. Oracle − belief win rate:

| Slice | selector, standard | selector, contested | llm (fake), standard | llm (fake), contested |
|---|---|---|---|---|
| All | +0.025 | +0.052 | +0.059 | +0.098 |
| Training maps | −0.031 | +0.026 | +0.019 | +0.085 |
| Held-out maps | +0.109 | +0.092 | +0.120 | +0.116 |
| Held-out `ai-*` | +0.089 | +0.161 | +0.111 | +0.189 |
| Live styles (contested) | n/a | +0.027 | n/a | +0.153 |
| Training `ai-*` | +0.003 | +0.031 | +0.058 | +0.069 |

Paired (identical games collapsed): contested selector belief − oracle score
−0.070 [−0.105, −0.033] (40 better / 83 worse, Holm p 0.001); `llm` −0.134
[−0.170, −0.097] (33 / 115, Holm p < 0.0001). Standard: selector −0.032
[−0.073, 0.004] (Holm p 0.64, not significant); `llm` −0.090 [−0.130, −0.054]
(Holm p < 0.0001).

Interpretation:

- **Partly, and it depends on where you look.** Perception is worth about a
  tenth of a win per match on held-out maps (+0.09 to +0.12 in every
  arm and benchmark). Against the held-out opponents it is worth +0.09 to
  +0.19. On the training maps against the training opponents, where the
  playbooks, the selector default and the strength work were developed, the
  selector's gap is small (−0.03 to +0.03). Against the calibrated live styles
  it is +0.027, inside the noise (selector 84/150 against 88/150). So for the
  deterministic selector on the benchmark it was tuned for, perception is
  not the bottleneck. On unfamiliar maps and opponents it is a real one.
- **How it shows.** Belief arms attack earlier and smaller. In the contested
  run the belief selector's first attack comes 80 s before the oracle's
  (134 s against 214 s) with a 2,305 smaller peak army, and it loses more
  value per value destroyed (trade share −0.042). One explanation fits these
  numbers: under fog the enemy army estimate reads low early, so the attack
  conditions pass before the army is large enough. This is an inference from
  the metrics, not a traced mechanism.
- **The fake LLM policy suffers more** (−0.134 against −0.070 contested),
  because its parameters come straight from the enemy estimate
  (`fake-counter-v2`). A live model given the same estimate and its
  confidence might discount it, or might not. Only a live run can tell.
- **Faction.** The belief selector's Allied weakness (0.73 against 0.94 as
  Soviet, contested) vanishes with the oracle (0.89 against 0.88). Most of
  the Allied deficit here is perceptual, not a fixture imbalance.
- Oracle play is not uniformly better. Against `ai-air` on held-out maps the
  oracle arms lose some games the belief arms win (standard 9/12 against
  12/12). Against `live-harass` the contested selector-oracle wins 12/30
  where the belief selector wins 16/30.

Where to spend effort next: scouting and enemy-strength estimation feeding the
attack decision (confidence-weighted, or a prior for unscouted army), checked
on held-out maps. More strategist cleverness on the training setup is not
where the gap is.

## What the strength and tuning work changed

Measured when each change landed (selector arm, simulator, commit messages
and `strategic-bot.md`):

| Change | Commit | Measured effect |
|---|---|---|
| Barracks-first opening, economy reserve for the critical chain, build the best available combat role instead of idling | ed955d2 | Training maps, 20 seeds, 9 opponents: 395/540 (0.731 [0.693, 0.767]) → 540/540 (1.000 [0.993, 1.000]); `ai-rush` 9/60 → 60/60, `balanced` 28 → 60, `ai-turtle` 42 → 60; no opponent regressed; time to first attack 459–485 s → 131 s. Results nearly deterministic per (faction, map) cell, so the effective sample is closer to 6 configurations per opponent |
| Focus fire on damage removed per kill-second | 90b7f20 | Win rate saturated both ways (540/540). Trade efficiency against `ai-balanced` 3.15 → 9.41, `ai-rush` 1.79 → 9.41, `ai-turtle` 2.10 → 9.41; match length 375–444 s → 282 s |
| Economy reserve no longer deadlocks the barracks-first opening | b6ada01 | Correctness fix (a reserve the base could never pay stalled all building); no separate win-rate measurement |
| Refineries come with a free harvester, as in RA2 | 6e3a960 | Contested selector against the live styles 56/100 (0.56), still inside the 30–70% band (was 73/150 = 0.49 before) |
| Training scripted styles diverge before the bot's first attack | 413bb21 | The selector had played byte-identical games against `ai-rush`, `ai-balanced`, `ai-turtle` on 5 of 6 training cells; they now differ (test-enforced). In this tournament `ai-turtle` is the only training `ai-*` that still beats the bot at all (contested selector 74/90) |
| CMA-ES parameter tuning | eeb31ec | 24 generations × 12 samples, 22,770 training matches: fitness never separated from the untuned bot (best − default ≤ 0.0008 per generation). Held-out 360/360 both; paired difference 0.0001 [−0.0001, 0.0003]; head-to-head 40/80 against a 40/80 control. **Not adopted** (`Data/tuned-parameters.json` has `adopted: false`); the bot plays the authored parameters |

The first two changes moved the selector from 73% to saturation on the
standard benchmark of the time. That saturation is why the contested
benchmark and the held-out opponents exist. The tuner ran on the saturated
benchmark and could not rank parameters. It has not been re-run on the
contested benchmark.

## Known limitations

1. **No live model result exists.** Every LLM row is the scripted fake
   policy. Whether Claude beats, matches or trails the selector is unmeasured.
   So is the vocabulary tier adoption (`vocabulary-adoption.json` stays at
   `Parameters` because fake evidence never counts), and so are latency and
   cost under a real API. The token counts and dollar figures are estimates.
2. **Simulator, not RA2.** Region-level movement and combat, an approximate
   hand-typed ruleset, 5 authored 2-player maps, no retail maps, no naval
   play, no multiplayer beyond 1v1. Nothing here has run against retail
   RA2/YR.
3. **Most opponents are beaten outright.** The frozen pinned styles and the
   training `ai-air`, `ai-balanced`, `ai-rush` lose every or nearly every
   game in both benchmarks, and difficulty levels barely change the scripted
   AI. Informative comparisons rest on the live styles (150 matches per arm)
   and the held-out opponents (180 per arm), so arm differences under about
   0.1 are not resolved there.
4. **Held-out opponents are not calibration-blind.** Their difficulty and the
   contested handicaps were set against the selector's win rate on all maps.
5. **Faction asymmetry.** The fixture favours Soviet (belief selector 0.73
   Allied against 0.94 Soviet, contested). Rates are reported with equal
   faction mixes but still average over it.
6. **Duplicate games.** Opponents that lose before diverging produce identical
   games; the distinct-game columns and collapsed pairs correct for it, but
   the nominal match counts overstate the sample.
7. **Tuning not adopted.** It found nothing on a saturated benchmark and has
   not been re-run on the contested one.
8. **Bandit learning.** It learns within a run, on training maps against
   training opponents only. Its held-out rows show a policy trained during
   that same run, not a frozen artifact.
9. **Distilled arm on a fake teacher.** It imitates the fake LLM policy (5,736
   and 9,470 examples), so it inherits that policy's weaknesses. Its low
   escalation rate (0.3–1.1%) and near-zero cost show the mechanism works,
   not that it distils Claude.
10. **Retail telemetry gaps.** `bindery.ra2.bot-observation/v1` carries no
    production queue state, ore fields, superweapon timers, visible-region
    set or house allegiance, so the retail bot will play with less information
    than the simulator bot measured here. Frames say so (`QueuesKnown`,
    `CreditsKnown`, `PowerKnown` false where unreported) rather than inventing
    values. With queues unreported the planner orders into a queue at most
    once per build time and counts what its own orders still owe
    (`OperationalPlan.UnreportedProductionDebt`, cost times the unbuilt share
    of the build time) against both its credits and the budget ledger; this is
    an estimate that assumes each order starts building at once, so a stalled
    or refused order is counted as paid off early. Credits and power are held
    at the last sample (zero before the first one arrives). Allegiance is not
    in v1: whoever constructs `Ra2ObservationAssembler` must pass the
    non-hostile owners (allies, neutral and civilian houses) from the game
    setup, or every non-self owner is treated as an enemy.
11. **Retail transport missing.** No native `IRa2TelemetrySource` or
    `IRa2CommandTransport` exists in this repo. The ra2yrcpp fork must provide
    them (see below). `Ra2BotHost` is tested only against recorded telemetry
    and a fake transport.
12. **Retail rules and maps.** A retail roster needs an operator-run
    `RulesmdImporter` over their own `rulesmd.ini`. The retail host also needs
    an operator-authored `MapInfo` JSON per map (regions, links, ore fields,
    `MapInfoLoader`), and none exist.
13. **Chrono Divide** integration is not started.
14. **Reproducibility.** Arena runs replay bit-exactly on the platform that
    ran them. The tuner's floating-point search is reproducible per platform
    only (platform math library).

## Running the live LLM arms

Nothing in the code changes; only the credential and the flag do.

1. Credential, either:
   - `export ANTHROPIC_API_KEY=sk-ant-...`, or
   - `ant auth login`. The Anthropic C# SDK 12.50.0 that `AnthropicMessageClient`
     uses auto-discovers the resulting profile (`ANTHROPIC_PROFILE` selects a
     non-default one; `ANTHROPIC_CONFIG_DIR` overrides the config location).
2. Check it with the live smoke test (skipped unless enabled):

   ```bash
   BINDERY_BOT_LIVE_LLM=1 nix shell nixpkgs#dotnet-sdk_8 -c dotnet test tests/Bindery.Ra2.Bot.Tests -c Release \
     --filter FullyQualifiedName~LiveClaudeSmokeTests
   ```

3. Run the arena without `--llm-fake`. LLM arms then run sequentially (rate
   limits), and each arm's first match runs alone: if no credential resolves
   the arm is skipped with the reason in `probes.json` and `report.md`.
   A small first run (contested defaults: the five live styles plus the two
   held-out opponents; 4 arms × 5 maps × 7 opponents × 2 seeds = 280
   matches, of which `llm`, `llm-oracle` and `distilled` call the model,
   plus the distilled teacher's `llm` matches; roughly $0.9 per `llm` match
   at the estimated token volume):

   ```bash
   nix shell nixpkgs#dotnet-sdk_8 -c dotnet run --project tools/Bindery.Ra2.Bot.Arena -c Release -- run \
     --arms selector,llm,llm-oracle,distilled --maps all --benchmark contested --seeds 2 \
     --out artifacts/arena-live
   ```

   The final tournament's exact live equivalent is the setup command above
   without `--llm-fake` (6,720 contested matches; five arms, `llm-shadow`,
   `llm`, `llm+fast`, `llm-oracle` and `distilled`, call the model in 840
   matches each; budget accordingly). `--arms tiers` runs the vocabulary tiers and writes
   `vocabulary-adoption.json`; a live result is the only evidence that can
   widen the adopted tier (`--write-adoption
   src/Bindery.Ra2.Bot.Claude/Data/vocabulary-adoption.json`).
4. Models: `claude-opus-5` for the strategist, `claude-haiku-4-5` for the
   `llm+fast` refine loop (`ClaudeStrategistOptions`), effort `low`, 25 s
   request timeout.

## The retail RA2/YR path

The bot is engine-agnostic and reaches retail RA2/YR only through the
adapter (`src/Bindery.Ra2.Adapter/Bot`). `Ra2BotHost` runs the loop
`IRa2TelemetrySource` → `Ra2Normalizer` → `Ra2ObservationAssembler` →
`BotRuntime.Tick` → `Ra2CommandSink` → `IRa2CommandTransport`, flushing each
frame's commands before the next frame. The ra2yrcpp fork must provide two
things:

**1. A telemetry source (`IRa2TelemetrySource`) whose normalized events carry
`bindery.ra2.bot-observation/v1`** (`Ra2BotTelemetryContract`):

| Event | Required fields |
|---|---|
| `game.unit.created`, `game.building.placed` | `frame` (int), `owner` (player id), `id` (uint entity id), `type` (string, rules id), `x`, `y` (cell), `health`, `maxHealth`; for entities not owned by the controlled player also `visible` (bool). Only `visible: true` enemies enter a frame: this is the fog boundary, and the fork must compute it from the controlled player's shroud, never send hidden units as visible |
| `game.unit.destroyed`, `game.building.destroyed`, `game.unit.killed` | `frame`, `id`; optional `killer` (player id) and `visible`. An enemy death is reported only if that enemy is currently observed; it counts as our kill only when `killer` is the controlled player |
| `game.economy.credits` | `frame`, `owner`, `credits` (only the controlled player's sample is used) |
| `game.economy.power` | `frame`, `owner`, `produced`, `drained` |
| `game.participant.defeated` | `frame`, `owner` |
| `game.lifecycle.ended` | `frame` (ends the host loop) |

A named field that is missing is reported in `MissingFieldsReport` and the
entity or event is dropped, never defaulted. Queue, ore, superweapon-timer and
visible-region data are not in v1. Adding them is a v2 contract, and the bot
already has the frame fields (`ObservationFrame.Superweapons`,
`OreRemaining`, production queues) to receive them.

**2. A command transport (`IRa2CommandTransport.SendAsync`) that executes
`bindery.ra2.bot-command/v1` envelopes** (`Ra2CommandEnvelope`:
`schemaVersion`, `kind`, `controller`, `fields`):

| `kind` | `fields` |
|---|---|
| `produce`, `cancel_production` | `typeId`, `queue` (queue kind name) |
| `place_building` | `typeId`, `cell` {`x`, `y`} |
| `sell` | `building` (entity id) |
| `move`, `attack_move` | `units` (entity ids), `destination` {`x`, `y`} |
| `attack` | `units`, `target` (entity id) |
| `stop` | `units` |
| `deploy` | `unit` |
| `repair` | `unit`, `depot` (entity id or null) |
| `harvest` | `harvester`, `ore` {`x`, `y`} |
| `set_rally_point` | `factory`, `cell` {`x`, `y`} |
| `launch_superweapon` | `building`, `target` {`x`, `y`} |

Entity ids must be the same ids the telemetry reports. The transport should
execute envelopes in the order received, and a command the game refuses must
not crash the loop (the bot tolerates rejected commands, as it does the
simulator's).

The operator also supplies, outside this repo: the imported rules JSON
(`RulesmdImporter` over their `rulesmd.ini`, then
`PlaybookRosterAdapter.Adapt(...)` for the playbooks) and a `MapInfo` JSON for
each map (`MapInfoLoader`). No retail game file may enter the repo
(`ci/verify-no-assets.ps1`).
