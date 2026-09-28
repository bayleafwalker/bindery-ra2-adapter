# Played held-out vocabulary tier comparison, live worker-fast (pre-registration, 2026-09-29, before the run)
`2026-09-29-llm-shadow-short-rationale-preregistration.md` passed every gate and criterion, so the Full vocabulary
earned a played held-out tier comparison under its own pre-registration; this is it. It applies the codified adoption
rule (`VocabularyAdoption.Decide`, `src/Bindery.Ra2.Bot.Claude/VocabularyTiers.cs`; the rule text is in
`src/Bindery.Ra2.Bot.Claude/Data/vocabulary-adoption.json`) and nothing else: a wider tier is adopted only if the tier
below it is adopted and, on held-out maps, its paired comparison against that tier (same opponent, map and seed),
played with a live model, has more pairs won than lost on match score, an exact two-sided sign-test p below 0.05, and
a 95% bootstrap interval of the mean score difference entirely above 0. `Parameters` (llm-t1) is the adopted base tier.

Run (one, at the commit in `docs/results/2026-09-29-tier-heldout/commit`): `--arms llm-t1,llm-t2,llm-t3 --maps heldout
--opponents ai-horde:easy,ai-horde:medium,ai-horde:hard,ai-armor:easy,ai-armor:medium,ai-armor:hard --seeds 8
--benchmark contested --llm-endpoint http://127.0.0.1:8020/v1 --llm-model worker-fast --write-adoption
<out>/vocabulary-adoption.json` (96 matches per arm; 96 pairs for ObjectivesAndRegions against Parameters and 96 for
Full against ObjectivesAndRegions). Held-out AI opponents, because the training opponents lose nearly every contested
game and leave no room for a tier to differ. Before starting, `/v1/models` must list worker-fast; if not, wait -- no
other model. Commands: `docs/results/2026-09-29-tier-heldout/run.sh`.

Decision: exactly the `adoptedTier` and `reasons` the run's `vocabulary-adoption.json` records. If it adopts a tier
above Parameters, that file replaces `src/Bindery.Ra2.Bot.Claude/Data/vocabulary-adoption.json` in its own commit with
both test suites run; otherwise the embedded record is updated only with this evidence and Parameters stays. Also
reported, not decided on: wins per arm and faction, validity and latency per tier. One run; no retuning, no other
model, no change to the rule, no extra seeds after the run.

## Outcome
Not run yet.
