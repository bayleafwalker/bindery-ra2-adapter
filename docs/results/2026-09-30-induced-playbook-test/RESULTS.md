# Induced playbooks on held-out cells: results (2026-09-30)
**Correction (2026-09-30, after merge): the candidate is NOT promoted.** The branch-1 call stands under the rule as
written, but that rule ran on the `standard` benchmark, and this run's own report flags it as saturated: the selector
scored 0.750, outside the 30-70% band, so "win-rate comparisons carry little information". The pre-registration did not
name a benchmark, so the default ran, and the flag was missed when the rule was applied. A free follow-up diagnostic
(Correction addendum below) shows the pinned playbook is exploitable where the selector is not. The next step is a
test on a benchmark that can rank arms (`docs/experiments/2026-09-30-luna-contested-preregistration.md`), not a
promotion gate for this playbook.


Pre-registration: `docs/experiments/2026-09-30-induced-playbook-preregistration.md` (7e6bdd4, 19:44; amended twice to name
the inducer build, PR #43's merge 5435efd, before induce ran: 5549227 and d7dcada, cherry-picked here as 1f290c1 and 0a46c87). Candidate named before any held-out match: `CANDIDATE.md`
(5232386 on branch `exp/induced-playbook-test`, kept as the timestamp record; the pre-registration commits are cherry-picked here with their original author dates). Build 5435efd (Release). All arms are deterministic with no model; the run cost nothing and took 23 s
(Soviet) + 19 s (Allied).

## Data path
- Tier 1, collection (`../2026-09-30-luna-training-collection/`): gpt-6-luna via OpenCode Go played 24 training
  matches (3 training maps x ai-rush, ai-balanced, ai-turtle, ai-air at hard x seeds 2 and 3) and won 24/24 (21 by
  elimination). 743 proposals in the match records (0 failed, 7 late-discarded); the forwarder logged 825 requests, all
  status 200 (the difference is presumably leakage-probe and first-match calls outside the match records;
  not verified), ~6.8M input and 0.78M output tokens (results.json itself records 6.16M / 0.71M),
  about $1.05 at list price inside the flat plan (6-match pilot included; pilot gates in its `PLAN.md` all passed).
- Tier 2, induction (`induce-report.md`, `induced.json`), with `--split training --min-support 6`: 3 playbooks.
  The candidate `induced-generic-expand-soviet-8f797e00` has 12 supporting matches. Its attack gate is army 5800 and
  GameSeconds 490.45, taken from the 6 of 12 matches that launched under it, and `expandAtSeconds` defaults to 30.
  The others are `induced-soviet-rhino-rush-soviet-7d66f1a3` (6 matches) and `induced-allied-boom-allied-553cbefc`
  (12 matches).

## Primary: candidate vs selector, 20 paired Soviet held-out cells (seeds 2, 4)

| Arm | Wins / 20 |
|---|---|
| `pinned:induced-generic-expand-soviet-8f797e00` (candidate) | 19 |
| `pinned:generic-expand` (its base, defaults) | 19 |
| `selector` | 15 |
| `pinned:induced-soviet-rhino-rush-soviet-7d66f1a3` | 9 |
| Luna `llm-t1`, reference only (PR #38, build 852e625, not re-run) | 20 |

Candidate vs selector: **better 5, worse 1**.
- Better: fortress-choke ai-armor:hard seeds 2 and 4, ai-armor:medium seed 4, ai-horde:hard seeds 2 and 4.
- Worse: open-steppe ai-horde:easy seed 4, which the candidate lost on timeout at 1200 s.

better - worse = 4 >= 2, so **rule branch 1 applies: the tiered pipeline works end to end.** The candidate is
promoted as the tier-3 Soviet arm candidate, and the next build is a playbook promotion gate: this test's shape,
automated, so an induced playbook is admitted only through a held-out test like this one.

## Secondary (reported, decides nothing)
- **The candidate vs its base: 0 better, 0 worse, the same winner in all 20 cells** (the play differs: decision
  logs differ in every cell, and fortress-choke matches run 3-33 s longer under the candidate). On these cells, compiling Luna's
  parameters and phase gate added nothing measurable over pinning `generic-expand` at its defaults. The value came
  from the choice of base, which is what Luna chose, not from the induced parameters or gate. This is not new
  evidence: the base's held-out record was known before the run (listed as contamination in the pre-registration). This matches the
  earlier held-out free check (pinned `generic-expand` 18/20 at an older build; 19/20 here).
- The fortress-choke hard cells: the candidate wins all 4 (ai-horde:hard and ai-armor:hard, seeds 2 and 4); the
  selector loses all 4. In PR #38 Luna won all 4 and the selector lost 3 of them (it won ai-armor:hard seed 4).
- The induced rhino-rush playbook is poor: 9/20; vs the selector better 3, worse 9. It would not pass a promotion
  gate. Six supporting matches was enough to induce it but not enough for it to be good.
- Allied (seeds 1, 3): the induced allied-boom playbook, pinned allied-boom and the selector all score 10, 11 and 10
  of 20. Each wins every open-steppe cell (10/10) and almost nothing on fortress-choke (0, 1, 0 of 10). The induced
  playbook vs the selector: better 0, worse 0; vs its base: better 0, worse 1 (fortress-choke ai-armor:easy seed 1). The Allied fortress-choke problem is untouched.

## Limits
- One run per cell. The arms are deterministic, so a re-run adds nothing; the limit is the 20 cells themselves.
- The selector scored 15/20 here and 17/20 in PR #38 (build 852e625). The cause is untested: the selector was not
  re-run at 852e625. Its two new losses (fortress-choke ai-armor:hard s4, ai-armor:medium s4) are 2 of the
  candidate's 5 better cells. Counted against PR #38's selector instead, the result would be better 3, worse 1:
  a margin of 2, still branch 1 but exactly at the threshold. Read branch 1 as holding, not as a wide margin.
- Selection on outcome: Luna won all 24 training matches, so the inducer saw no losses to contrast with.
- The phase gate came from only 6 launches, all at ~490 s. The matches that did not launch under this playbook give
  it no gate information.

## Reproducing
Decision logs are archived as `decisions.tar.xz` (+ `.sha256`) beside each `results.json`. To re-run the induce,
extract `../2026-09-30-luna-training-collection/run/decisions.tar.xz` into that `run/` directory first, then run the
command in the pre-registration. The original commits carried the raw logs; this branch archives them to keep ~114 MB
out of main.

## Correction addendum: free diagnostic after the merge (training maps only; decides nothing)
Build c50b769, all arms Soviet (seeds 2, 4, and 6, 8 under contested), no model; files in `diagnostic/`. Training
opponents are partly in-sample for the selector (its default playbook was chosen against these styles), which favours it.

| Opponents | Candidate | `pinned:generic-expand` | Selector |
|---|---|---|---|
| scripted ai-*:easy, standard (24) | 22 | 22 | 24 |
| frozen bot styles (rush, turtle, tech, harass, balanced), standard (30) | 17 | 18 | 30 |
| live-* bot styles, `--benchmark contested` (60) | 15 | 21 | 55 |
| scripted ai-* at hard, contested (48) | 38 | 40 | 48 |

- The late mass attack beats scripted AIs, the held-out ones included, but a fixed playbook is exploited by
  bot-style opponents: 1/6 vs balanced and tech; 1/12 vs live-balanced and live-harass; 0/12 vs live-tech. The selector
  changes playbook against them.
- Under contested, induction is worse than its base (53 vs 61 of 108, mostly timeouts: 33 vs 24). The induced 490 s
  gate delays the attack.
- Calibration (`diagnostic/calibration/`, selector only, contested, training maps, seeds 1-8): as Soviet the selector
  wins 103/108 (saturated); as Allied against live-* it wins 20/60 (33%, inside the band). That is the region the
  follow-up test uses.

What went wrong in the process, for the record:
- The benchmark was not pre-registered.
- The saturation flag was ignored.
- The rule compared against the selector only on scripted-AI cells, so it could not detect exploitability.
- The collection had no losses (24/24), so the inducer had nothing to contrast.
- The test re-measured a known fact: Luna's edge was already known to be choosing generic-expand.
