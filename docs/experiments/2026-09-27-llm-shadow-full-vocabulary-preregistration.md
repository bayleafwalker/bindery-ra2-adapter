# Full-vocabulary LLM shadow pass: pre-registration (2026-09-27, before the run)
Question: with the local model, does the LLM strategist at the Full vocabulary tier add something the selector does
not (variety of plans, use of its wider authority, explained choices), while staying valid and on time? Strength is
not measured: in a shadow arm the selector plays every match and the LLM only proposes, so no win rate is claimed.

Arm: `llm-shadow-t3` (selector primary, `claude-strategic` shadow at `VocabularyTier.Full`).
Model: local endpoint only, `--llm-endpoint http://127.0.0.1:8020/v1 --llm-model worker-fast`.
Cell (training only; held-out maps and opponents are not used): maps `training` (twin-valley, river-crossing,
island-bridges) x opponents `live-balanced`, `ai-rush:hard` x 4 seeds, contested benchmark = 24 matches.
Decision logs are written (no `--no-decisions`). Metrics: `docs/experiments/shadow_metrics.py <out> llm-shadow-t3`.

## Metric (defined before any LLM result is seen)
Over accepted shadow proposals, pooled across the cell:
- **Variety (N1)**: playbook entropy per faction, normalised by log2 of the playbooks the faction uses, averaged
  over factions; compared with the same statistic for the selector's primary proposals in the same matches.
- **Full-tier authority use (N2)**: of the shadow proposals that chose the same playbook as the selector's latest
  primary proposal, the share that set at least one Full-tier field (posture, budget, composition, attack, abort or
  replan conditions) differently. Parameters, objectives and regions are reported but do not count.
- **Adaptability (N4)**: of the answers to requests triggered by an event or replan (`event:*`, `replan:*`, not
  the periodic cadence), the share whose plan (playbook, posture) differs from the same strategist's previous answer;
  compared with the selector's primary answers in the same matches.
- **Explainability (N3)**: share of accepted proposals whose rationale is non-empty, at most 400 characters and
  cites at least one number (a feature value, threshold or time).

Gates (if one fails the non-strength result is not interpretable and is reported as such):
G1 validity (accepted / shadow proposals) >= 0.90. G2 fog rejections = 0. G3 p95 proposal latency <= 20 s (the
strategic cadence).

## Criteria (one run, no prompt or threshold changes after it)
1. N1 shadow entropy >= selector entropy + 0.15.  2. N2 >= 0.25.  3. N3 >= 0.80.  4. N4 shadow rate >= selector rate.
All gates and all four criteria pass -> the Full vocabulary earns a played held-out tier comparison under its own
pre-registration. Any failure -> it does not; the adopted tier (`Parameters`) stays and the numbers are recorded.

## Pipeline check (fake client, not a result)
`--llm-fake`, arm `llm-shadow-t3`, training maps x `live-rush` x 4 seeds (12 matches): 360 shadow proposals,
validity 1.0, 0 fog rejections, entropy 0.639 vs selector 0.318, N2 94/94, N3 341/360, N4 21/112 vs selector
23/134. This shows the metric is
computable; `fake-counter-v2` is a scripted policy, so it says nothing about the model.

## Amendment (2026-09-27, before any model run)
N4 added: the design critique (#3) names adaptable, varied and explainable; N1-N3 covered only the last two.

## Outcome (2026-09-28, one run, nothing changed after it)
Run at f9155eb after the reboot restored the endpoint: `docs/results/2026-09-28-llm-shadow-t3/` (`run.sh`,
`metrics.txt`, `run/report.md`, raw decision logs in `run/decisions.tar.xz`). 24 matches, 432 shadow proposals,
worker-fast on the local endpoint.

| | criterion | result | |
|---|---|---|---|
| G1 validity | >= 0.90 | 0.988 (427 / 432) | pass |
| G2 fog rejections | = 0 | 0 | pass |
| G3 p95 latency | <= 20 s | 8.77 s (p50 7.13 s) | pass |
| N1 variety | shadow >= selector + 0.15 | 0.572 vs 0.182 | pass |
| N2 Full-tier authority use | >= 0.25 | 0.862 (200 / 232) | pass |
| N3 explainability | >= 0.80 | 0.119 (51 / 427) | **fail** |
| N4 adaptability | shadow >= selector | 0.592 (29 / 49) vs 0.088 (7 / 80) | pass |

**Result: the Full vocabulary does not earn a played held-out tier comparison; `Parameters` stays adopted.** The
gates pass, so the numbers are interpretable. N3 fails on length, not grounding: of the 427 accepted proposals, 365
rationales exceed 400 characters (median 558), 11 cite no number, none are empty. N2 is carried mostly by
objectives, parameters and composition; posture never differed (0 / 232). As pre-registered, no prompt or threshold is
changed after the run; a shorter-rationale prompt would need its own pre-registration.
