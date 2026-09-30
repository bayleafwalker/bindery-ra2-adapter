# Forwarder outage during the run, and how the result is read (written 2026-10-01, before the rerun)

The run finished with EXIT=0. The local forwarder hit its 2-hour background limit at about 23:41 and was restarted
within about a minute. The first 22 cells had normal delivery (0-3 failed proposals per match). Eight Luna matches ran
through the outage with every model call failing: island-bridges live-turtle seeds 1 and 3, and all six live-harass
cells. They made 0 proposals, with 22-85 failed calls each, and the selector fallback played the whole game. The arena
itself labels the run "not a model result" (352 of 999 calls failed).

The pre-registration counts "every played cell ... including cells with failed or late proposals". That clause was
written for occasional failures. A cell in which the model made no decision is not a Luna-vs-selector comparison.
Decided before the rerun, with Luna's outcomes on those 8 cells unknown (only fallback outcomes are known):
- **Primary:** delete the 8 Luna records and rerun exactly those cells with `--resume` and the forwarder up, with no
  other change. The rule is applied to all 30 cells with the rerun values.
- **Also reported:** the literal reading with the outage cells counted as played (better 2, worse 4, which is parity),
  and the 22 unaffected cells alone.
The selector arm is deterministic and is not rerun.
