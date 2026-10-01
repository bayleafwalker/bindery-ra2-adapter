# Luna vs selector on the contested benchmark: results (2026-10-01)

Pre-registration: `docs/experiments/2026-09-30-luna-contested-preregistration.md` (a5d56b1 at 21:41; amended 21:43
for definitions only; both before any match). Outage handling was decided before the rerun: `OUTAGE.md` (59bdb2f).
Build a5d56b1 (code c50b769), gpt-6-luna via OpenCode Go. Cells: `--benchmark contested`, 3 training maps x live-balanced,
live-rush, live-tech, live-turtle, live-harass x seeds 1 and 3, with the arm as Allied. 30 paired cells.

## Primary: rule branch 3, parity
| Arm | Wins / 30 | live-balanced | live-harass | live-rush | live-tech | live-turtle |
|---|---|---|---|---|---|---|
| Luna (`llm-t1`) | 7 | 0/6 | 0/6 | 2/6 | 3/6 | 2/6 |
| selector | 10 | 0/6 | 0/6 | 2/6 | 4/6 | 4/6 |

- **Luna better on 1 cell:** island-bridges live-rush s1.
- **Luna worse on 4 cells:** river-crossing live-rush s1, river-crossing live-turtle s1 and s3, twin-valley live-tech s1.
- worse - better = 3 < 5, so **parity: model playbook choice is not shown to matter on the benchmark that can rank
  arms.** The direction favours the selector, but the result is within the stated noise (the pre-registration gives a
  ~10-25% false-call risk at +/-5).
- **Delivery:** 956 proposals answered, 2 failed, after the rerun.

All three readings named in `OUTAGE.md` give branch 3:
- 30 cells, 8 rerun (primary): better 1, worse 4
- literal, outage cells counted as played: better 2, worse 4
- the 22 unaffected cells alone: better 1, worse 4

Luna also plays worse by the secondary metrics: trade efficiency 0.33 vs the selector's 0.51 (value destroyed / value
lost), and first attack at 397 s in 12 of 30 games vs 255 s in 16 of 30.

## Secondary (reported, decides nothing)
- **Luna does not adapt to the opponent.** Its time share in `allied-boom` is 0.59-0.85 against every live style:
  live-turtle 0.85, live-tech 0.78, live-harass 0.76, live-balanced 0.73, live-rush 0.59. The rest is mostly
  generic-defend and allied-ifv-mix. On the standard benchmark it chose generic-expand for Soviet just as uniformly.
  The model mostly picks one default per faction, which is what a fixed playbook does.
- **Other models cannot play at the arena's 25 s reply deadline**, on the 6 pre-registered cells (live-balanced,
  live-harass x 3 maps x seed 1):
  - `deepseek-v4-pro`: forwarder median 61 s. All 20 first-match proposals timed out, so the arena skipped the arm.
  - `deepseek-v4-flash`: median 23 s, p90 52 s. 95 of 155 calls failed, exactly the 95 calls over 25 s. It won 1/6
    and is labelled "not a model result".
  - The probe (`ocgo_model_probe.py`, 6 s and 15 s) used a short prompt and understated latency on the arena prompt
    (~8,000 input tokens) by 4-10x.
  - kimi-k3, qwen and mimo were already slower than these in the probe. grok-4.7, glm-5.3 and minimax-m3 fail on
    protocol or JSON.
- **Cost:** Luna 8.87M input and 0.97M output tokens over the pilot, the run and the rerun, about $1.4 at list
  price inside the flat plan, under the $2.5 cap. The deepseek pilots were small.

## Consequence
Branch 3, together with the secondary results, means no model on the flat plan both meets the latency deadline and
beats the selector. Luna meets the deadline and does not beat the selector; the others do not meet it. Spending on a
paid stronger model (Claude Opus, about $2.69/match) is not supported by this evidence either: the failure seen is
non-adaptive choice, not weak reasoning, and nothing here shows a stronger model would choose differently. The
selector stays the tier-3 default. The useful next build is on the selector's own weak cells: as Allied it wins 0/12
against live-balanced and live-harass, and so does Luna.

## Incidents
- The forwarder ran as a background job with a 2-hour limit and was stopped mid-run (~23:41), and again after the
  pilots (~03:42). In the main run, 8 Luna matches ran through the outage with every call failing. They were rerun
  as decided in `OUTAGE.md`, and the original records are kept in `outage-records/`. A run longer than the limit
  needs the forwarder started outside the job system (for example `systemd-run --user`).
- The pilot was 6 Luna matches, not the 3 first written. Corrected in the 21:43 amendment.
