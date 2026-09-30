# Induced playbooks on held-out cells (pre-registration, 2026-09-30)
**Status: registered before `arena induce` was run on the collection.** Written while the training collection
(`docs/results/2026-09-30-luna-training-collection/`) was still playing; no induced playbook existed when this was
committed. Every value below is fixed now; nothing is tuned on held-out data.

Question (the tier-2 -> tier-3 decision): does a playbook that `arena induce` compiles from gpt-6-luna's won decisions
on TRAINING maps against TRAINING opponents play, as a deterministic `pinned:<id>` arm with no model, at least as
well as the selector on the held-out Soviet cells, and does compiling add anything over simply pinning its base
playbook at defaults?

## Inputs, fixed now
- Training data: `docs/results/2026-09-30-luna-training-collection/run/` only (24 `llm-t1` matches: twin-valley,
  river-crossing, island-bridges x ai-rush, ai-balanced, ai-turtle, ai-air at hard x seeds 2 (Soviet, west) and 3
  (Allied, east); gpt-6-luna via OpenCode Go; build fba7da8; `--extended-metrics`). If the collection ends with
  fewer matches (a crash, a resumed tail), the inducer reads what is there; the match count is reported.
- Induction: `arena induce --from docs/results/2026-09-30-luna-training-collection/run --arm llm-t1 --split training
  --min-support 6 --out induced.json --report induce-report.md`, at the merge commit of PR #43 (PR #42's inducer
  plus its re-review follow-ups: only adopted LLM intents count, per-match parameter medians, launches credited to
  the playbook active at launch). Amended 2026-09-30 ~20:10, before induce ran on the collection, when #43 was
  opened; the original text named #42's merge commit (6093615). `--min-support 6` = half of the 12 matches per faction, fixed before seeing any cluster; the PR's
  default (30 won matches) cannot be reached by a 24-match collection. No other inducer option is set.
- Candidates: every induced playbook for Soviet (`induced-<base>-soviet-<hash>`). If more than one, each is tested
  and the decision reads the one with the most supporting matches (ties: lexicographically first id), named in the
  results before any held-out match runs. If none reaches support 6, no held-out run is made and the outcome is
  "induction yields no candidate at this data size".

## Held-out run
Cells: the 20 Soviet cells of the PR #38 test: maps open-steppe and fortress-choke x opponents ai-armor:easy,
ai-armor:medium, ai-armor:hard, ai-horde:easy, ai-horde:hard x seeds 2 and 4.
```
arena run --arms pinned:<candidate>,pinned:<base>,selector --maps heldout \
  --opponents ai-armor:easy,ai-armor:medium,ai-armor:hard,ai-horde:easy,ai-horde:hard --seed-list 2,4 \
  --playbooks induced.json --extended-metrics --interleave --resume --out <results dir>
```
All three arms are deterministic and free (no model). `<base>` is the candidate's base playbook at its defaults.
Luna (`llm-t1`) is not re-run: its 20/20 on the same cells (`twenty-cell-luna/`, build 852e625) is quoted as a
reference, not a decision cell, because a re-run would spend the flat plan for a comparison already made.

## Decision rule (primary: candidate vs selector, 20 paired Soviet cells)
better = cells the candidate wins and the selector loses; worse = the reverse.
1. worse = 0 and better >= 1, or better - worse >= 2: **the tiered pipeline works end to end**. Promote the
   candidate as the default tier-3 Soviet arm candidate, and the next build is a playbook promotion gate (this test's
   shape, automated) so induced playbooks are admitted only through a held-out test like this one.
2. worse - better >= 2: **induction as built does not transfer**. No promotion; the next step is to read the report
   (phase gate vs parameters) against the training data only, not the held-out cells.
3. Otherwise: **parity**. The candidate is recorded as an equal, model-free alternative to the selector; no default
   changes. The next build returns to the fortress-choke perception/strategy fixes.

Secondary, reported, decides nothing: candidate vs `pinned:<base>` (does compiling add over the base?), each arm's
wins per opponent and per map, and the fortress-choke hard cells (ai-horde:hard, ai-armor:hard) where the selector
lost and Luna won in PR #38. Allied induced playbooks, if any, are reported on seeds 1 and 3 of the same maps and
opponents, also secondary.

## Known contamination, stated before the run
- `pinned:generic-expand` was already played on these 20 cells in a free check (18/20; losses ai-horde:easy on
  open-steppe). That result is held-out and informed nothing in the inducer, which is mechanical and reads only the
  training collection; but anyone reading the result knows the base's held-out record, so the candidate-vs-base
  comparison is secondary.
- Held-out opponents' difficulties were calibrated against the selector on all five maps (see any arena report's
  preamble), so the selector's held-out rate is partly by construction.
- 20 cells, deterministic arms: one run per cell; the pinned arm's determinism is covered by
  `PinnedArmTests.A_pinned_arm_is_deterministic`.
