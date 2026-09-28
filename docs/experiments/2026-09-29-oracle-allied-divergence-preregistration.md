# Why the oracle arm trails as Allied: traced divergence (pre-registration, 2026-09-29, before any trace is read)
Question: in the held-out-maps × held-out-AI cell, what decision first separates `selector` from `selector-oracle`
in the Allied games that the selector wins and the oracle loses? `docs/architecture/strategic-bot-results.md` (2026-09-28
section) found the oracle's Allied deficit at SeenAttackForceRatio 0.8 (paired 46 against 1) and still at 1.0 (148
against 164 of 240, p 0.026, at fe3244a), and named candidates: the armour-share (composition) rule, reduced scouting
when enemy positions are known, and the defend triggers. The ratio question itself is closed
(`2026-09-29-seen-attack-ratio-preregistration.md`: C1 failed, 0.8 stays). This study explains; it changes no
parameter, and nothing here may be tuned from its held-out data.

Run (one, at the commit recorded in `docs/results/2026-09-29-oracle-divergence/commit`):
`--arms selector,selector-oracle --maps heldout --opponents ai-horde:easy,ai-horde:medium,ai-horde:hard,
ai-armor:easy,ai-armor:medium,ai-armor:hard --seeds 40 --benchmark contested --no-decisions --knob
SeenAttackForceRatio=1.0 --trace <dir>` (480 matches per arm, 240 Allied pairs). Commands:
`docs/results/2026-09-29-oracle-divergence/run.sh`. Analysis: `docs/experiments/oracle_divergence.py` (committed with
this file; its docstring defines the categories and their precedence). 1.0 is the value at which the deficit was last
seen significant; it is a probe setting for this study only.

Gate: the Allied paired split (selector-only wins against oracle-only wins, exact two-sided sign test) must have
p < 0.05 at this commit. Otherwise stop: the deficit is not present at HEAD and nothing is classified.

Classification: for each Allied discordant pair, the first game second at which the two arms' decisions differ in one
category (intent, composition, scouting, defend, attack-gate; that precedence within a second; `none` if none differs
before the earlier match end). Decisions are compared as choices and targets, not as numbers.

Decision: a category is the primary cause if it is the divergence of >= 50% of selector-only pairs and its share there
is >= 2x its share among oracle-only pairs (with no oracle-only pairs, the 50% condition alone, stated as such).
Primary cause found -> it is written up with example traces as the explanation, and any fix is a separate
pre-registered change tested on training cells. None -> the per-category counts are recorded and the line closes
with "no single first divergence". No rerun, no other knob values, no change to the categories after the run.

The script was checked for parsing only on training traces (`--maps training --opponents ai-rush --seeds 1`), never on
this cell.

## Outcome
Run once at 11739be (`docs/results/2026-09-29-oracle-divergence/commit`): selector-oracle 366/480; `analysis.txt`,
`run/report.md`, `run/results.json.xz`, and the 92 traces of the 46 Allied discordant pairs in
`discordant-traces.tar.xz` (+ `.sha256`; the analysis reproduces from these archives).

Gate: passes. Allied pairs 240: selector-only 31, oracle-only 15, sign p 0.0259.

Decision: **no primary cause**, so the line closes with "no single first divergence".

| First divergence | selector-only | oracle-only |
|---|---|---|
| scouting | 31/31 (1.00) | 15/15 (1.00) |
| intent, composition, defend, attack-gate, none | 0 | 0 |

Scouting is the first divergence of every discordant pair in both directions, always at second 106: in all 46 pairs
the selector issues its first scout order then and the oracle issues none. It fails the 2x condition
(1.00 against 1.00), and as a first divergence it is structural, not discriminating: it happens whoever wins, so this
design cannot say which later decision decides these games. Per the pre-registration, no categories are changed after
the run and no rerun follows; a design that skips the scout difference (for example, first divergence after the
first scout order) would need its own pre-registration.

Reported only, not decided on: as Allied the oracle issues a scout order at any time in 187 of its 240 games (the
selector in 240 of 240; full traces, not committed). In the discordant pairs (committed traces) the oracle never
scouts in 19 of 31 selector-only and 6 of 15 oracle-only pairs. Whether never scouting matters is a question for a
separate pre-registration, not a finding of this one.
