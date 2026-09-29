# Startup crash: fork service started at ExeRun or on the first game frame (A/B pre-registration, 2026-09-29, before any counted run)
The lab's fork ra2yrcpp DLL sees an intermittent `STATUS_STACK_OVERFLOW` inside DDrawCompat 0.5.4's `DDRAW.dll`
(offset 0x1C104) at game startup, in about 2 of 20 launches, against 0 of about 18 with the stock DLL
(`deploy/ra2-lab/README.md`). DDrawCompat and its ini stay unchanged (they are the golden appliance). The planner's
follow-up: a pre-registered A/B that defers the fork's service start (asio and command threads) from ExeRun to the
first game frame, with at least 60 launches per arm. The fork change is `bayleafwalker/ra2yrcpp`
`feat/defer-service-start` (58a5438): configuration key `deferServiceStart`, off by default.

Arms: one DLL (58a5438, built with upstream's docker MinGW toolchain; payload sha256 recorded in `payload.txt`) in
both arms. A = `deferServiceStart` absent (the service starts at ExeRun, as deployed); B = `deferServiceStart: true`
(`LAB_DEFER_SERVICE_START=1`). Each run is `PREPARE_STAGE=2 lab-run.sh --stage 1` (the unattended stage-1 match with
the fork DLL on both clients: two launches). 60 runs, 30 per arm, in the fixed order ABBA repeated 15 times, so any
drift over the ~6 hours falls on both arms alike. Driver: `docs/results/2026-09-29-defer-service-ab/run.sh`, which
classifies each launch from its run's `syringe-client-<side>.log` with `classify.py` (committed with this file):
`crash` = an NTSTATUS exit code (`C` followed by seven hex digits); `ok` = any other recorded exit code; `unknown` = no
exit code (the run failed before or around the launch). A run's match completion is `control_plane_lifecycle_complete`
in its evidence. One smoke run (20260928-195101-s1, arm B) checked the pipeline before this file and is not counted.

Criteria (60 launches per arm):
- P1: B has fewer `crash` launches than A, exact two-sided Fisher test on crash against non-crash launches, p < 0.05.
- P2 (guard): among runs where no launch crashed, B's completion rate is at least A's minus 10 percentage points.
- Validity: at most 6 `unknown` launches per arm; more makes the result uninterpretable and it is reported as such.
Decision: P1 and P2 pass (and validity holds) -> `deferServiceStart` is adopted for the lab (stage 2 writes it by
default) and the fork PR is merged. Otherwise the default stays off, the fork PR is closed unmerged, and the counts are
recorded. Crash dumps and DDrawCompat logs of crashed launches are kept on the host only. Stop rule: three runs in a
row that fail for infrastructure reasons (tunnel, control plane, guest agent) pause the series; it resumes from the
next run in order once fixed, and the pause is recorded. No other arms, no extra runs, no change to the classifier.

## Outcome
Run 2026-09-28 19:57 to 2026-09-29 09:31 at 9d7dfbe (payload sha256 in `payload.txt`), all 60 runs in the fixed
ABBA order, 120 launches. A host reboot at about 22:25 interrupted run 35 before it produced evidence; it was not
counted, and the series resumed from run 35 once the guests were back and the preflight passed (the three immediate
"no evidence" entries at 08:04 were preflight refusals while the guests booted, with no launch). Both pauses are in
`infra.log`. Analysis: `docs/results/2026-09-29-defer-service-ab/analysis.txt`; per-run logs in `run-logs.tar.xz`.

- A (service at ExeRun): 0 crash, 60 ok, 0 unknown launches; 30 of 30 runs completed.
- B (`deferServiceStart: true`): 0 crash, 60 ok, 0 unknown launches; 29 of 30 runs completed (run 14 hit the
  40-minute match cap with both clients running, so it has no completion evidence and counts as not completed).
- P1: 0 of 60 against 0 of 60, Fisher exact p = 1.0: **fails**.
- P2: 96.7% against 100%, within 10 points: passes. Validity holds (0 unknown launches per arm).

Decision, as pre-registered: `deferServiceStart` is not adopted; the lab default stays off. The fork branch
`bayleafwalker/ra2yrcpp` `feat/defer-service-start` (58a5438) never had a PR opened, so there is none to close; it
stays unmerged. Also reported, not decided on: the startup crash did not occur in either arm (0 of 120 launches,
against about 2 of 20 before), so this series could not have shown an effect of the deferral.
