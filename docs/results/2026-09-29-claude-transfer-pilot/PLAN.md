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
Counted. All 4 matches were played by Claude: 201 Primary proposals (195 validated, all accepted; 6 arrived late and
were discarded), 6 `proposal_failed` records of which 1 was an API failure (`claude.server_error`, 503) and 5 were
`superseded`/`no_opinion` (4 in Allied fortress-choke seed 1, 2 in Allied open-steppe seed 1), median latency ~10 s
(p90 ~14 s), about $10.75 in API cost (`run/`, `run.log`). The selector fallback also proposed in gaps, including 7
times in Soviet open-steppe seed 2 (6 after an abort request, 3 of them rhino-rush); the table counts Claude only.

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

## The 20-cell test (defined before it is run, 2026-09-29 18:42, launched ~18:43; rule amended 18:44, before any match finished)
Cells: Soviet only, seeds 2 and 4 (west and east start), both held-out maps, the five held-out opponents not used in
the pilot: `ai-horde:easy, ai-horde:hard, ai-armor:easy, ai-armor:medium, ai-armor:hard` (2 x 2 x 5 = 20). Arms
`llm-t1` (API, `claude-opus-5`, workspace header, as attempt 3) and `selector`, same build, contested benchmark. The
arena plays the arm as Soviet on even seeds, so `--seeds 4` runs seeds 1-4; only the 20 even-seed cells count, the
Allied cells are reported but not decided on. Estimated cost ~$110 for 40 LLM matches (both factions, ~$2.69 each), ~2-2.5 h (the first LLM match runs alone, then
up to 16 in parallel; the pilot's 4 took 42 min).

Paired comparison on the 20 Soviet cells (Claude better / worse / same winner as the selector), and the pooled
rhino-rush share of Claude's Soviet Primary proposals. The A/A floor for a live model is 17% of cells changing winner
(tier-heldout `aa-noise.txt`), ~3 of 20.
- Claude worse on at least 6 more cells than it is better, with one-sided sign p <= 0.05 on the discordant cells
  (6 vs 0 p 0.016, 7 vs 1 p 0.035; 8 vs 2 p 0.055 does not qualify), and rhino-rush Claude's most-proposed playbook in
  the majority of the cells where it is worse: the playbook mechanism holds for the model that plays; the next build
  is the Soviet playbook guard.
- worse minus better <= 2: Claude holds up against the selector as Soviet; the next build is perception / attack
  gate, and the playbook guard is kept for local-model (worker-fast) deployments only, not built now.
- otherwise (3-5, or >= 6 without the sign-test or rhino-rush condition): the loss is not explained by playbook choice alone; the next build
  is perception / attack gate, and the Soviet losing matches' decision logs are read to name the mechanism before
  any guard.
Amendment (18:44, after independent review, before any 20-cell result was read): the first version required
rhino-rush >= 30% of Claude's pooled Soviet proposals, a threshold set after seeing the pilot's 38%; it is replaced by
the per-cell mechanism check above (rhino-rush the top playbook in most Claude-worse cells), and the sign-test
p condition is stated as a threshold instead of one example. Pilot counts corrected (201 Primary, not 210; 1 API
failure among 6 failed records).

## Amendment: the 20-cell test's model and scope (2026-09-29 19:49, before the test's first match)
The first launch (18:43, API, `claude-opus-5`, `--seeds 4`, both arms) was stopped at ~18:47 before any match
finished, because it would have spent ~$110 of a limited API budget, half of it on Allied cells the rule does not
use. API spend for the whole pilot line ends at $13.39. Two changes, nothing else:
- **Model:** `gpt-6-luna` through OpenCode Go (a flat subscription), via `ocgo_proxy.py`, a local forwarder that
  adds the key and the `x-opencode-session` header and translates chat/completions to the Responses API that the
  GPT models require. The question becomes: does an affordable model that could actually play repeat worker-fast's
  Soviet mistake, and does it hold up against the selector? Luna was chosen on a 4-cell pilot on the same cells as
  attempt 3 (`luna-pilot/`, 19:07-19:47): 4 of 4 won, 177 Primary proposals, 0 rejected, 3 superseded, 0 API
  failures, median latency 11 s (p90 15 s), ~$0.04. Its Soviet proposals were all `generic-expand` (58 of 58),
  none rhino-rush. Other candidates probed: Kimi K2.7 Code (pilot at the same time: median 57 s, ~12% upstream
  5xx; too slow for play), DeepSeek V4 Flash and Qwen3.7 Plus (valid JSON but not piloted), MiniMax M3 and
  DeepSeek V4.1 Flash (no valid structured output), GLM-5.3 Flash (70 s per call).
- **Scope:** Soviet cells only, via the new `--seed-list 2,4` (#34): 20 LLM and 20 selector matches, main at 852e625.
The decision rule is unchanged. With 0% rhino-rush in the pilot the guard branch is unlikely; the test mainly
separates "holds up as Soviet" (next build: perception / attack gate) from "loses for another reason" (read the
losing logs first).
