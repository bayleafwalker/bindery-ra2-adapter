# Full-vocabulary LLM shadow pass with a bounded rationale: pre-registration (2026-09-28, before the run)
Follows `2026-09-27-llm-shadow-full-vocabulary-preregistration.md`, whose one run passed every gate and N1, N2 and N4
but failed N3 (explainability 0.119): 365 of 427 rationales exceeded 400 characters (median 558), 11 cited no number.
The prompt then asked only for "a short rationale". That outcome recorded that a shorter-rationale prompt needs its
own pre-registration; this is it. The cell uses training data only.

Change (prompt only, `src/Bindery.Ra2.Bot.Claude/IntentPromptBuilder.cs`): "a short rationale" becomes "a rationale
(see Field conventions)", and Field conventions gains: "rationale: one or two sentences, at most 300 characters,
naming the feature value, threshold or game time that decided the choice (for example "BaseThreatRatio 1.40 >=
1.2"); put further reasoning in assumptions." No schema maxLength and no truncation in code (either would make N3
measure the code, not the model). The system prompt is shared, so the change applies to every vocabulary tier.

Run: exactly the cell and commands of `docs/results/2026-09-28-llm-shadow-t3/run.sh` (arm `llm-shadow-t3`,
`--maps training --opponents live-balanced,ai-rush:hard --seeds 4 --benchmark contested`, local endpoint
`--llm-endpoint http://127.0.0.1:8020/v1 --llm-model worker-fast`, decision logs on), output
`docs/results/2026-09-29-llm-shadow-short-rationale/`. Before starting, `/v1/models` must list worker-fast; if not,
wait -- no other model. Metrics: `docs/experiments/shadow_metrics.py <out> llm-shadow-t3`.

Criteria (unchanged; one run, nothing changed after it): G1 validity >= 0.90, G2 fog rejections = 0, G3 p95
latency <= 20 s; N1 shadow entropy >= selector + 0.15; N2 >= 0.25; N3 >= 0.80; N4 shadow rate >= selector rate.
Also reported, not criteria: median rationale length, count over 400 characters, posture changes within N2 (0 of 232
last time).
All pass -> the Full vocabulary earns a played held-out tier comparison under its own pre-registration.
Any failure -> the Full-vocabulary line closes for worker-fast: `Parameters` stays adopted, no third prompt.

## Outcome
Run once at a60af92 (`docs/results/2026-09-29-llm-shadow-short-rationale/commit`), worker-fast on the local endpoint,
24 matches (17 wins), 434 shadow proposals; `metrics.txt`, decision logs in `run/decisions.tar.xz` (+ `.sha256`;
the metrics reproduce from the extracted archive).

Decision: **every gate and criterion passes**, so the Full vocabulary earns a played held-out tier comparison under
its own pre-registration (not started here).

| Criterion | Threshold | Measured | Result |
|---|---|---|---|
| G1 validity | >= 0.90 | 0.993 (431/434) | pass |
| G2 fog rejections | = 0 | 0 | pass |
| G3 p95 latency | <= 20 s | 8.35 s (p50 6.91 s) | pass |
| N1 shadow entropy | >= selector 0.182 + 0.15 | 0.569 | pass |
| N2 authority use | >= 0.25 | 0.836 (235/281) | pass |
| N3 explainability | >= 0.80 | 0.891 (384/431) | pass |
| N4 adaptability | >= selector 0.0875 | 0.521 (25/48) | pass |

Reported only: median rationale 153 characters (558 last run); 1 of 431 over 400 (365 of 427 last run), 3 over the
prompt's 300; 46 cite no number (11 last run) and 0 are empty, so N3's remaining misses are ungrounded, not long ones.
Posture changes within N2: 0 of 235, as last time: N2 is carried by objectives, parameters, composition and regions.
