# Reinforcement batching: pre-registration (2026-09-27, before the held-out run)
Change: while an attack runs, its members persist; units built since gather at the staging region and join in
batches of ReinforceSquadTargetSize (previously declared and read by nothing).
Training sweep (training maps x training opponents, contested, 8 seeds, arm-only --knob; live-* opponents at the
default of the build under test, 4):
| Batch | selector | selector-oracle |
|---|---|---|
| 1 (old behaviour) | 287/336 | 309/336 |
| 2 | 294/336 | 308/336 |
| 4 | 291/336 | 304/336 |
| 6 | 281/336 | 297/336 |
Chosen default: 2. Held-out criteria (one run, no retuning after it), vs 8-seed side-rotated step-2 numbers:
1. OOS selector >= 72/96.  2. OOS selector-oracle >= 62/96.
Outcome (held-out x ai-horde/ai-armor, contested, 8 seeds): selector 72/96 -> 74/96; oracle 64/96 -> 68/96. Both
criteria PASS; the gains are within noise and are not claimed as improvements. Training: selector 283 -> 288/336,
oracle 308 -> 310/336.
