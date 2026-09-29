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

Attempt 2 (17:52, API, `claude-opus-5`): not counted. Every one of the 139 Primary requests was refused with
`invalid_request_error`: the API key is not scoped to a workspace and requests lacked `anthropic-workspace-id`, so all
four matches were played by the selector fallback (its "3 of 4 wins" is not a Claude result). Kept as
`attempt-2-unscoped-key/`. The next attempt sets `ANTHROPIC_CUSTOM_HEADERS=anthropic-workspace-id: <id>` (read by the
Anthropic SDK) or uses a workspace-scoped key; nothing else changes.

## Outcome (attempt 3, 17:57-18:39, API, `claude-opus-5` with the workspace header)
Counted. All 4 matches were played by Claude: 210 Primary proposals, all accepted by validation, 6 failed calls
(2 and 4 in the two Allied matches; the selector fallback covered those gaps), median latency ~10 s (p90 ~14 s),
about $10.75 in API cost (`run/`, `run.log`).

Soviet Primary proposals (the decision input):

| Match | rhino-rush | flak-mix | generic-expand | generic-defend | v3-siege | total | result |
|---|---|---|---|---|---|---|---|
| open-steppe seed 2 | 33 | 0 | 14 | 17 | 1 | 65 | lost (timeout, assets 33,200 vs 34,400) |
| fortress-choke seed 2 | 1 | 7 | 16 | 0 | 0 | 24 | won (elimination) |
| pooled | 34 (38%) | 7 | 30 | 17 | 1 | 89 | |

38% is neither >= 50% nor <= 20%, so the rule's third branch applies: **run the 20-cell test.** Claude does not
reproduce worker-fast's near-exclusive rhino-rush (1,273 of 1,827, 70%), but it still leans on it in one of two
Soviet games, and flak-mix is not its top pick.

Reported, not decided on: against the selector on the same 4 cells (`selector-same-cells/`, same build), both win
3 of 4. They differ on two cells: Allied fortress-choke seed 1 (Claude won on timeout; the selector lost on timeout)
and Soviet open-steppe seed 2, the rhino-rush-heavy match (Claude lost on timeout; the selector won by elimination
at 336 s). Allied play: allied-grizzly-timing 63, allied-boom 39, generic-defend 9, allied-ifv-mix 1 over 2 wins.

## The 20-cell test (defined before it is run, 2026-09-29 ~18:55)
Cells: Soviet only, seeds 2 and 4 (west and east start), both held-out maps, the five held-out opponents not used in
the pilot: `ai-horde:easy, ai-horde:hard, ai-armor:easy, ai-armor:medium, ai-armor:hard` (2 x 2 x 5 = 20). Arms
`llm-t1` (API, `claude-opus-5`, workspace header, as attempt 3) and `selector`, same build, contested benchmark. The
arena plays the arm as Soviet on even seeds, so `--seeds 4` runs seeds 1-4; only the 20 even-seed cells count, the
Allied cells are reported but not decided on. Estimated cost ~$110 for 40 LLM matches (both factions), ~1-1.5 h.

Paired comparison on the 20 Soviet cells (Claude better / worse / same winner as the selector), and the pooled
rhino-rush share of Claude's Soviet Primary proposals. The A/A floor for a live model is 17% of cells changing winner
(tier-heldout `aa-noise.txt`), ~3 of 20.
- Claude worse on at least 6 more cells than it is better (e.g. 7 vs 1; one-sided sign p <= 0.035) and rhino-rush
  >= 30% of its Soviet proposals: the playbook mechanism holds for the model that plays; the next build is the
  Soviet playbook guard.
- worse minus better <= 2: Claude holds up against the selector as Soviet; the next build is perception / attack
  gate, and the playbook guard is kept for local-model (worker-fast) deployments only, not built now.
- otherwise (3-5, or >= 6 with rhino-rush < 30%): the loss is not explained by playbook choice alone; the next build
  is perception / attack gate, and the Soviet losing matches' decision logs are read to name the mechanism before
  any guard.
