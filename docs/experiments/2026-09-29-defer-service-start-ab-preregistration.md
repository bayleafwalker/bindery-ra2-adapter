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
Not run yet.
