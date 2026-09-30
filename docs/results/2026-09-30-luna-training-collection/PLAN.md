# Luna decision collection on training maps (2026-09-30)

Decision it informs: which phased playbooks `arena induce --split training` (PR #42) compiles from gpt-6-luna's
won decisions, i.e. the candidate `pinned:<induced>` arm(s) for the pre-registered held-out test against the
selector and Luna. Training maps and training opponents only; no held-out map or opponent is played here.

Build: origin/main fba7da8 (Release). Model: `gpt-6-luna` via OpenCode Go (flat plan) through the local forwarder
`ocgo_proxy.py 8032 proxy.ndjson`; no Anthropic API.

Cells: training maps (twin-valley, river-crossing, island-bridges) x training scripted opponents at hard
(ai-rush, ai-balanced, ai-turtle, ai-air) x seeds 2 (Soviet, west) and 3 (Allied, east) = 24 `llm-t1` matches.

Pilot (~30 min): the ai-rush slice (6 matches) into `run/`. Continue to the collection only if all of:
- LLM failure rate (API errors + rejected proposals) under 10%;
- projected list-price cost for 24 matches (from proxy.ndjson usage) at most $2;
- Luna wins at least 1 of 6 (induction reads won matches only).
Collection: same command with all four opponents and `--resume` into the same `run/`, so pilot matches count.
