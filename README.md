# Bindery RA2/YR adapter

GPL-3.0-or-later Windows adapter boundary for the Bindery external-runtime
research experiment. The repository contains client-owned control-plane and
launch plumbing only. It does not contain Red Alert 2/Yuri's Revenge
executables, maps, registry exports, proprietary assets, or redistributed game
files.

The adapter supports three explicit transport profiles:

- `bindery-native`: the `bindery-relay/v1` opaque relay contract;
- `cncnet-private`: the live hermetic CnCNet-compatible tunnel boundary;
- `cncnet-baseline`: an unchanged CnCNet tunnel deployment retained only for
  comparison.

The live target is Yuri's Revenge in CnCNet RA2 Mode. The spawner/injection
boundary is represented by `ISpawnerBoundary` and models Syringe loading
`yrpp-spawner`; the adapter never copies or embeds proprietary game code. A
Windows CI runner must perform the real spawner build and field acceptance
after the upstream notices and revisions are reviewed.

The control-plane client uses the v1 session shape (`compatibility`,
`participant_policy`, `placement`, and `capture`) and emits lifecycle kinds as
the wire strings `ready`, `started`, `exited`, `failed`, and
`capture_degraded`. Relay placement and transport credentials remain
coordinator-issued; the adapter does not select or mint them.

Reports return typed session/enrollment state, and known session or enrollment
IDs can be read through the public v1 endpoints. These reads expose observed
control-plane phases; they are not synthetic match evidence.

`TwoClientMatchDriver` prepares the native path for two distinct player
identities and remains the local relay fixture. `CncNetPrivateMatchDriver`
prepares the live path: it creates one session, enrolls both clients, requires a
coordinator-issued `cncnet-private` placement, and hands the endpoint to the
external CnCNet-compatible tunnel boundary. It intentionally does not emulate
that tunnel protocol.

The two live clients should be disposable clones of one golden Windows
appliance. See [`docs/golden-appliance.md`](docs/golden-appliance.md) and the
lab boundary in [`docs/architecture/ra2-yr-cncnet-lab.md`](docs/architecture/ra2-yr-cncnet-lab.md).
The generated golden manifest is schema v2 and records the exact selected
package-embedded spawner artifact; the standalone spawner release is not
silently interchangeable.

## Local checks

```powershell
dotnet build src/Bindery.Ra2.Adapter/Bindery.Ra2.Adapter.csproj --configuration Release
dotnet test tests/Bindery.Ra2.Adapter.Tests/Bindery.Ra2.Adapter.Tests.csproj --configuration Release
dotnet build tools/Bindery.Ra2.Adapter.LiveAcceptance/Bindery.Ra2.Adapter.LiveAcceptance.csproj --configuration Release
dotnet build tools/Bindery.Ra2.Adapter.ControlPlaneGate/Bindery.Ra2.Adapter.ControlPlaneGate.csproj --configuration Release
```

The control-plane-only gate can exercise the real identity, placement,
enrollment, readiness-boundary, and heartbeat path without claiming a game
match:

```bash
dotnet run --project tools/Bindery.Ra2.Adapter.ControlPlaneGate/Bindery.Ra2.Adapter.ControlPlaneGate.csproj -- \
  /private/ra2-yr-cncnet-v0.2.manifest.json http://127.0.0.1:18080
```

The live Windows handoff is documented in
[`docs/live-qualification.md`](docs/live-qualification.md). It requires a
private settings file and a real Windows/control-plane environment; no live
credentials or proprietary game files belong in this repository.

## Strategic bot

A hierarchical, mostly-deterministic RTS bot for RA2/YR lives in
`src/Bindery.Ra2.Bot` (engine-agnostic) and plugs into this adapter through
`src/Bindery.Ra2.Adapter/Bot`. Full design, invariants and metrics are in
[`docs/architecture/strategic-bot.md`](docs/architecture/strategic-bot.md);
the final tournament, known limitations, and how to run the live LLM arms and
the retail RA2 path are in
[`docs/architecture/strategic-bot-results.md`](docs/architecture/strategic-bot-results.md).
All published results come from the approximate region simulator, with the LLM
arms on a deterministic fake client; no live-model or retail-RA2 result exists
yet.

| Layer | Cadence | What it does |
|---|---|---|
| Belief / Features | every frame | Turns raw observations into remembered, confidence-decayed state and compiled trends. |
| Tactical | 5–15 Hz | Squad micro, harvester safety, repair, deploy. |
| Operational | 1 s / on major events | Production, build placement, squad formation and objectives. |
| Strategic | 10–30 s / on major events | One deterministic or LLM strategist proposes an `StrategicIntent`; never emits commands directly. |
| Validator / arbiter | every proposal | Fog, freshness, commitment/hysteresis and single-ownership checks before anything reaches execution. |

### Running the tests

```bash
nix shell nixpkgs#dotnet-sdk_8 -c dotnet test tests/Bindery.Ra2.Bot.Tests -c Release
nix shell nixpkgs#dotnet-sdk_8 -c dotnet test tests/Bindery.Ra2.Adapter.Tests -c Release
```

The adapter's bot-bridge tests (`BotBridgeTests.cs`) target `net8.0-windows`
but build and run on Linux because `EnableWindowsTargeting` is set; no
Windows-only APIs are used.

### Running the arena

The arena CLI (`tools/Bindery.Ra2.Bot.Arena`) plays configured strategist
arms against opponent styles on the deterministic region-graph simulator and
reports win rate, invalid plans, decision lateness, strategic churn,
production idle time, resource float, trade efficiency, inference cost and a
hidden-information leakage probe:

```bash
nix shell nixpkgs#dotnet-sdk_8 -c dotnet run --project tools/Bindery.Ra2.Bot.Arena -- run \
  --arms selector --maps training --opponents balanced --seeds 1 \
  --max-seconds 300 --out artifacts/arena
```

Arms: `selector`, `bandit`, `llm-shadow`, `llm`, `llm+fast`, `distilled`
(`--arms all`); opponents (`--opponents all`): the independent scripted AI
`ai-rush`, `ai-balanced`, `ai-turtle`, `ai-air` and the held-out
`ai-horde`, `ai-armor` (optionally `:easy`, `:medium`, `:hard`; default hard;
`src/Bindery.Ra2.Bot.Sim/Opponents`, no code shared with the bot's planner; the
held-out two never feed learning, distillation or tuning, and
`--opponents heldout` selects them), and the pinned-playbook styles `rush`, `turtle`, `tech`,
`harass`, `balanced`, which run a frozen copy of the bot's stack as of commit
7f3e2c7 (`tools/Bindery.Ra2.Bot.Baseline`) so they stay a stationary benchmark
(`live-<style>` runs the style on the live stack); maps: `training`, `heldout`
or `all`. The arm is a full `BotRuntime` and plays Allied on odd seeds and
Soviet on even ones. `--trace <dir>` writes a per-match diagnostic (state every
10 s, destroyed entities, the arm's decision log).
`--benchmark contested` is the setting that can rank arms: mirror opponents on
the live stack (`live-balanced`, `live-rush`, `live-tech`, `live-turtle`,
`live-harass`, plus the held-out `ai-horde` and `ai-armor`, unless
`--opponents` is given), the Allied side (whichever seat) starting with 20,000
credits against 10,000 to offset the fixture's Soviet edge, and ±25% seeded
combat noise; the selector wins about half of its games against the live styles
(84/150 in the final tournament). The knobs are
also separate flags (`--opponent-income`, `--opponent-credits`,
`--allied-income`, `--allied-credits`, `--combat-noise`). The default
`standard` benchmark is easier (the selector won 594/690 in the final
tournament, and every game against the frozen pinned styles), and the report
flags any baseline win rate outside 30–70% as saturated. `report.md` pairs every arm's matches with the `--baseline` arm
(default `selector`) by opponent, map and seed and prints a per-metric table:
mean difference with a 95% bootstrap interval, better/worse/tied pairs, exact
sign-test p and Holm-adjusted p.
`--oracle` gives every arm full-state frames (results are labelled);
`--oracle both`, or an arm named with a `-oracle` suffix
(`--arms selector,selector-oracle`), runs the belief and oracle versions side by
side on the same jobs, and the report adds a per-metric belief − oracle table
(the perception-bottleneck diagnostic); and
`--dataset <file>` trains the distilled arm on an exported dataset (otherwise on
the `llm` arm's training-map decisions from the same run, running that arm first,
unreported, if it was not requested); the distilled arm escalates
out-of-distribution states to the Claude strategist (fake or live), and the
report's Distillation table shows its escalation rate next to cost per match. The output directory holds `results.json`,
`probes.json` (per-arm leakage probes and skipped arms), `report.md`, one
`dataset-<arm>.ndjson` of training-map decisions per arm, and
`decisions/<match>.ndjson` (the arm's full decision log, about 0.2–0.5 MB per
match; `--no-decisions` turns it off) with `decisions/<match>.match.json` (arm,
opponent, map, seed, match length, benchmark and LLM latency). To re-run a
recorded match from its log, LLM answers included, without a model:

```bash
nix shell nixpkgs#dotnet-sdk_8 -c dotnet run --project tools/Bindery.Ra2.Bot.Arena -c Release -- replay artifacts/arena/decisions/llm_live-rush_twin-valley_1.ndjson [--out replayed.ndjson]
```

`arena analyze <log> [--out report.md] [--narrate] [--llm-fake]` builds the
deterministic post-game report (`PostGameReport`: intent timeline with
rationale and trigger, pivots and what caused them, proposals without effect and
why, key events, shadow agreement, time by playbook and posture) and, with
`--narrate`, a short narrative from Claude (`PostGameNarrator`; the fake client
writes a scripted one, labelled). `arena replay`:
It replaces the arm's primary and shadow strategists with `ReplayStrategist`s,
prints whether the replayed decision log hash equals the recorded one (exit 0,
else 2 with the first differing record), how many requests had no recording,
and how many recorded answers were never asked for.

### Tuning parameters

`tools/Bindery.Ra2.Bot.Tune` searches playbook parameter defaults and selected
planner/feature knobs (`src/Bindery.Ra2.Bot/Tuning/TuningKnobs.cs` lists each
with its range and why it is tunable) with a seeded separable CMA-ES on the
**training** maps only, then validates the result on the **held-out** maps:

```bash
nix shell nixpkgs#dotnet-sdk_8 -c dotnet run --project tools/Bindery.Ra2.Bot.Tune -c Release -- search \
  --date 2026-09-26 --opponents all,self --generations 24 --population 12 --out artifacts/tune
nix shell nixpkgs#dotnet-sdk_8 -c dotnet run --project tools/Bindery.Ra2.Bot.Tune -c Release -- validate \
  --tuned artifacts/tune/tuned-candidate.json --date 2026-09-26 --seeds 20 \
  --out artifacts/tune-validate --write src/Bindery.Ra2.Bot/Data/tuned-parameters.json
```

Fitness per match is win 1 / draw 0.5 / loss 0 plus `--trade-weight` (0.1)
× destroyed/(destroyed+lost); every candidate of a generation plays the same
opponents, maps and seeds, next to the distribution mean and the untuned
defaults. `--opponents` takes arena opponent names, `bot:default` (the untuned
live bot) and `bot:champion` (the previous generation's best); `all` is every
`ai-*` and pinned style, `self` both `bot:*`, and `--mode selfplay` makes
`self` the default. Matches run in parallel; results do not depend on core
count. The date is a CLI argument so the output is reproducible byte for byte.
`validate` writes the set with `adopted` true only if the held-out rule in
`Program.AdoptionRule` passes; the bot (`PlaybookLibrary.LoadDefault`,
`StandardBot.Create` defaults) applies the embedded set only when adopted, and
`PlaybookLibrary.LoadAuthored` always gives the untuned playbooks.

### Importing an operator ruleset

`RulesmdImporter` (`src/Bindery.Ra2.Bot/Rules`) turns an operator-supplied
`rulesmd.ini` into the JSON `IRulesDatabase` fixture the bot reads, hashing it
into a `rulesmd-sha256:<hash>` ruleset ID. Run the importer against your own
`rulesmd.ini` **outside this repository** — `rulesmd.ini` and other retail
assets are forbidden here and rejected by `ci/verify-no-assets.ps1`. Keep the
imported JSON output wherever your own bot configuration lives; only the
approximate `bindery-sim-approx` fixture is committed.

### LLM arms

`--personality aggressive,turtle,tech,harasser[,none]` runs every arm under
each authored play style (`src/Bindery.Ra2.Bot/Strategy/Personalities.cs`: prompt
guidance for the LLM, and for the deterministic strategists a preferred playbook
per faction, parameter scaling and a defence threshold); arms are labelled
`selector@turtle` and the report's Play styles section gives each style's
playbook and posture shares, time to first attack, and the pairwise
Jensen–Shannon divergence between styles.

What the LLM may decide comes in vocabulary tiers (`VocabularyTier`:
`PlaybookOnly`, `Parameters`, `ObjectivesAndRegions`, `Full`); fields above a
strategist's tier are replaced by the playbook's defaults. The default is the
tier adopted in `src/Bindery.Ra2.Bot.Claude/Data/vocabulary-adoption.json`,
which is `Parameters` (select and parameterise) until a live held-out run shows
a wider tier wins: `--arms tiers` runs `llm-t0`…`llm-t3`, the report compares
each with the tier below pair by pair, and the run writes
`vocabulary-adoption.json` (`--write-adoption <path>` writes it elsewhere, for
example over the embedded record). Fake-client runs are never evidence.

The `llm`, `llm-shadow` and `llm+fast` arena arms (`src/Bindery.Ra2.Bot.Claude`)
need `ANTHROPIC_API_KEY` in the environment, or a resolvable `ant auth`
profile. Without either, those arms are skipped with a recorded reason; pass
`--llm-fake` to exercise the pipeline with a scripted client instead of a live
call.

### Honesty notes

- `src/Bindery.Ra2.Bot.Sim` is a **deterministic approximation**, not retail
  RA2/YR: authored maps, an approximate rules fixture, and simplified combat
  and economy resolution. Arena results from it are directional, not a
  substitute for retail validation.
- The retail RA2/YR route goes through this adapter's `IRa2TelemetrySource`
  (in) and `IRa2CommandTransport` (out) seams over the ra2yrcpp fork's native
  RPC. This repository decodes/encodes the documented envelopes
  (`bindery.ra2.bot-observation/v1`, `bindery.ra2.bot-command/v1`) but does
  not implement or vendor that native transport. `Ra2BotHost`
  (`src/Bindery.Ra2.Adapter/Bot`) is the host loop between the two seams
  (telemetry → normalizer → `Ra2ObservationAssembler` → `BotRuntime.Tick` →
  `Ra2CommandSink` → transport, flushed per frame, stopping at match end); it is
  tested end to end against recorded telemetry (`RecordedRa2TelemetrySource`,
  NDJSON) and a fake transport. What a retail match still needs: a native
  `IRa2TelemetrySource` and `IRa2CommandTransport` from the ra2yrcpp fork, and
  telemetry for production queues, ore and superweapon timers, which
  observation/v1 does not carry (the planner then treats queues as empty).
