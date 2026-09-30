# Does a model's live playbook choice beat the selector where the benchmark can rank arms? (pre-registration, 2026-09-30)
**Status: registered before any match of this test.** Motivated by the correction to the induced-playbook test
(`docs/results/2026-09-30-induced-playbook-test/RESULTS.md`): every Luna result so far (24/24 training, 20/20
held-out) ran on the `standard` benchmark, where the selector scores 75-100% and win-rate comparisons carry little
information.

Decision it informs: whether spending on model-driven playbook choice is justified, meaning a choice policy induced
from model decisions, or a stronger or paid model. If the model cannot beat the selector where the selector is beatable,
neither is.

## Cells, chosen by a selector-only calibration on training data
`--benchmark contested`, training maps (twin-valley, river-crossing, island-bridges), the live bot styles live-balanced,
live-rush, live-tech, live-turtle, live-harass, seeds 1 and 3 (the arm plays Allied): 30 paired cells.
The calibration (`docs/results/2026-09-30-induced-playbook-test/diagnostic/calibration/`, selector only, seeds 1-8)
gave the selector 20/60 as Allied against live-* (inside the 30-70% band) and 103/108 as Soviet (saturated), so only
the Allied live-* region can rank arms. No held-out map or opponent is used.

## Arms and run
Build: origin/main c50b769 plus docs, run from a5d56b1 (this document and the forwarder copy). Model via OpenCode Go and the local forwarder
(`ocgo_proxy.py`, copied beside the results).
```
arena run --arms llm-t1,selector --maps training --benchmark contested \
  --opponents live-balanced,live-rush,live-tech,live-turtle,live-harass --seed-list 1,3 \
  --llm-endpoint http://127.0.0.1:8032/v1 --llm-model gpt-6-luna --extended-metrics --interleave --resume --out run
```
Pilot first: the live-balanced slice (6 Luna matches: 3 maps x seeds 1, 3), resumed into the same `run/`. Continue
only if the LLM failure rate (failed requests / requests, the report's "LLM delivery" column) is under 10% and the
projected list-price cost of the 30 Luna matches is at most $2.5. If the pilot aborts, no test is made and the
abort is reported. The $2.5 cap applies to the whole Luna run. Every played cell counts in the rule, including
cells with failed or late proposals (the selector fallback plays through those gaps).

## Decision rule (Luna vs selector, 30 paired cells; better = Luna wins and the selector loses)
A live model changes winner on roughly 17% of cells between repeat runs: 5 of 30 in the worker-fast measurement
(`docs/experiments/2026-09-29-tier-heldout-preregistration.md`), a proxy for Luna's noise, which has not been measured.
The threshold is 5. Stated risk: with no true difference and D of about 5-10 discordant cells, better - worse has an
SD of about sqrt(D), roughly 2.2-3.2, so a +/-5 threshold calls a false difference about 10-25% of the time. That is
accepted for a decision about where to spend, not for a claim of superiority. The selector arm is deterministic on
these seeded cells and known in advance (10/30 in the calibration, seeds 1 and 3), so "better" is capped at 20.
Amended 2026-09-30 ~22:05 during the pilot, before any result was read: definitions only, and the rule is unchanged.
1. better - worse >= 5: **model choice adds value where it can be measured.** Next build: induce a choice POLICY
   (enemy composition and game state -> playbook) from these decisions, not a single playbook. A stronger model is
   worth a registered run on these cells.
2. worse - better >= 5: **Luna is worse than the selector here.** No further model spend on playbook choice. The
   selector stays the tier-3 default.
3. Otherwise: **parity.** Model choice is not shown to matter on this benchmark. The secondary pilots below decide
   whether any other model is worth a registered run.

## Secondary (reported, decides nothing)
- Stronger or other models on the 6 cells where the selector does worst (calibration 0/12: live-balanced and
  live-harass x 3 maps x seed 1): `deepseek-v4-pro` (the forwarder drops `response_format` for it because it answers
  400 to a JSON schema; it follows the prompt's JSON instruction, 6 s) and `deepseek-v4-flash` (~15 s). Other
  OpenCode Go models were probed (`ocgo_model_probe.py`) and excluded:
  - too slow to play: kimi-k3 ~33 s, qwen3.7-plus 25-33 s, qwen3.8-max 45 s and a timeout, mimo-v2.6-pro 17-40 s
  - no valid JSON: glm-5.3, minimax-m3
  - protocol unsupported: grok-4.7
- Per-opponent wins, Luna's playbook choices per opponent, lateness and failure rates.
- Cost bound: stop the secondary pilots if total list-price cost at the forwarder exceeds $4.

The run is expected to take ~1.5-2.5 h and end around midnight to 00:30. It is resumable (`--resume`); the machine
must stay on until then.
