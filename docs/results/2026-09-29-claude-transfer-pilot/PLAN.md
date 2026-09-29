# Claude transfer pilot (written before the run, 2026-09-29)

Question for the next build: worker-fast at Parameters (llm-t1) loses to the deterministic selector on the held-out
cells (81 paired cells: 29 worse, 5 better, sign p 4e-5; mostly Soviet, 22 worse vs 3 better), and the mechanism is
playbook choice: as Soviet it proposes `soviet-rhino-rush` 1,273 of 1,827 times while the selector spends ~93% of
Soviet time in `soviet-flak-mix`. Does Claude, the model that plays, make the same choice?

Pilot: arm `llm-t1`, `--maps heldout --opponents ai-horde:medium --seeds 2 --benchmark contested`, 4 matches
(seeds 1 Allied, 2 Soviet, both held-out maps). The model is Claude Opus 5.5 through the operator's Claude
subscription (`claude -p --model opus`, no tools, the arena's own system prompt and JSON schema) behind
`claude_proxy.py`, an OpenAI-compatible shim, so the arena records it as a non-Anthropic endpoint. Build at the
current origin/main.

Decision rule on the Soviet matches' Primary proposals:
- rhino-rush share >= 50%: the mechanism transfers; run the 20-cell Soviet test (seeds 2 and 4, five held-out
  opponents) paired with the selector before building the playbook guard.
- rhino-rush share <= 20% and flak-mix the most-proposed playbook: the failure is worker-fast-specific; the next
  build is perception / attack gate, and no played LLM test follows.
- otherwise: run the 20-cell test.
Also reported, not decided on: outcomes against the selector on the same 4 cells, latency, validity.

## Amendment before any scored run (2026-09-29 ~18:00)
The first attempt through `claude_proxy.py` (Opus 5.5 on the operator's subscription) stopped at 16:22 when the
subscription's session limit was reached, after about 63 valid calls (the Allied match plus two Soviet decisions),
with no match scored. It is kept as `attempt-1-subscription/` and not counted. The rerun uses the arena's own
Anthropic client (no `--llm-endpoint`) with an API key, so the model is the strategist's production default
`claude-opus-5` (`ClaudeStrategistOptions.DefaultStrategicModel`), the model that actually plays. Same arm, cells,
and decision rule as above.
