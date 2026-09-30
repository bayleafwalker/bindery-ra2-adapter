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

The on-demand broadcast channel -- back-to-back matches, a rendered client
captured by OBS to a local MediaMTX room stream with optional Twitch output,
and a player-safe agent seat -- is described in
[`docs/architecture/ra2-channel.md`](docs/architecture/ra2-channel.md), with
the streaming setup in [`deploy/ra2-channel`](deploy/ra2-channel/README.md).

## Local checks

```powershell
dotnet build src/Bindery.Ra2.Adapter/Bindery.Ra2.Adapter.csproj --configuration Release
dotnet test tests/Bindery.Ra2.Adapter.Tests/Bindery.Ra2.Adapter.Tests.csproj --configuration Release
dotnet build tools/Bindery.Ra2.Adapter.LiveAcceptance/Bindery.Ra2.Adapter.LiveAcceptance.csproj --configuration Release
dotnet build tools/Bindery.Ra2.Adapter.ControlPlaneGate/Bindery.Ra2.Adapter.ControlPlaneGate.csproj --configuration Release
dotnet build tools/Bindery.Ra2.Adapter.Channel/Bindery.Ra2.Adapter.Channel.csproj --configuration Release
dotnet build src/Bindery.Ra2.Adapter.Planner.Claude/Bindery.Ra2.Adapter.Planner.Claude.csproj --configuration Release
dotnet build src/Bindery.Ra2.Adapter.Ra2yrcpp/Bindery.Ra2.Adapter.Ra2yrcpp.csproj --configuration Release
```

The channel tool plays the live match back to back and drives OBS:

```powershell
dotnet run --project tools/Bindery.Ra2.Adapter.Channel -- C:/private/channel-settings.json
```

Start from [`docs/channel-settings.example.json`](docs/channel-settings.example.json).
Omit `obs` to run the match loop without broadcasting. Omit `agentSeat` and
`liveTelemetry` for the proven two-client path; `telemetryRecording` (a path
with `{channel}` and `{match}`) follows an NDJSON recording instead of the
live ra2yrcpp service. The live transport and agent seat have only been
tested against a fake service, not a live game. Both the channel and
live-acceptance tools accept an optional spectator client (`observerIdentity`,
`observerClientInstanceId`, `observerLaunch`, `observerHost`).

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
opponent, map, seed, match length, benchmark, LLM latency and the match's result
record), each written the moment that match finishes. `--interleave` plays the
arms' matches cell by cell (opponent, map, seed) across arms instead of one arm after
another, so paired comparisons fill in as the run goes (the distilled arm and a live
LLM arm still run per arm, after the interleaved ones; default order unchanged).
`--seed-list 2,4` plays exactly those seeds instead of 1..`--seeds` (the arm is Soviet on even
seeds and Allied on odd ones; west on 1-2, 5-6, ..., east on 3-4, 7-8, ...), e.g. a Soviet-only test at half
the matches.
`--playbooks <file.json>` (repeatable) loads operator- or machine-authored playbook sets
(`PlaybookDocument` JSON) next to the 12 built-in ones; every component of every arm (prompt catalogue,
validator, selector, distilled strategist, replay) uses the merged library. A duplicate playbook id is
refused with an error naming the id and file, and the files' SHA-256 is part of the resume fingerprint.
`arena playbooks export [--out <file>]` writes the default library (tuned parameters applied) in that format,
the reference for authoring or inducing a set.
`--arms pinned:<playbookId>` is the deterministic runner for one playbook (tier 3 of LLM play, then
`arena induce`, then this): a full live bot whose Primary strategist always proposes that playbook at its
default parameters, renewed at the normal cadence, with no LLM and no selector choice (the selector stays the
fallback). It takes any default or `--playbooks` id (an induced one included) and refuses an unknown id before
any match. A playbook plays only the factions it lists; on the other side the selector fallback plays, so
use `--seed-list` to pin the side (the arm is Soviet on even seeds); the arm prints a warning to stderr when asked
to play a faction its playbook does not list.
`arena induce --from <dir> [--from <dir> ...] --arm <arm> [--split training|heldout|all] --out <playbooks.json>
[--report <md>] [--min-support N] [--playbooks <file> ...]` compiles an arm's decision logs
(`decisions/<match>.ndjson` and `.match.json`, found recursively under each `--from`) into playbooks. For each
(faction, chosen base playbook) cluster with at least `--min-support` (default 5) won matches in which the arm proposed it (a match counts
once however often it proposed the choice; only proposals the arbiter adopted count, so ones the validator rejected,
the scheduler discarded late or the arbiter refused, e.g. to keep the incumbent, are ignored; when no cluster reaches
the threshold the largest cluster's support is printed), it writes `induced-<base>-<faction>-<hash of inputs>`: the base's posture, budget,
composition and parameter ranges, parameter defaults at the median, over the supporting matches, of each match's median proposed value (so a match
that renewed 40 times weighs the same as one that proposed twice; clamped to the base's
range), and two phases, `build` and `attack`. `attack` is entered when `OwnArmyValue` reaches the median army
value at the arm's first launch in those matches at its rising edge (an attacking squad note repeats every plan tick, so a playbook adopted mid-attack launches nothing) while that playbook was the active one (a launch is credited only to the
cluster whose playbook was active at the time; a match with no such launch still supports the cluster but adds no launch
statistics) and `GameSeconds` reaches the 25th percentile of first-launch
time; its attack conditions are the base's with the `OwnArmyValue` bound at that median (added when the base has
none) and a `GameSeconds` floor at that time, set both on the `attack` phase and on the playbook itself, so the gate
also holds in the `build` phase. The output is byte-identical for identical logs. `--split` defaults to `training` (a training map against a training opponent, as in the dataset export; held-out
opponents such as `ai-horde` are excluded even on training maps); `heldout` (a held-out map or opponent) or `all` prints a
warning, because an induced playbook must not be tested on the data it was induced from. `--report` lists per
cluster the supporting matches and proposals, wins, parameter medians and interquartile ranges, the launch
time and army distributions and each source log's SHA-256. The data is conditioned on won matches (selection on
outcome): it shows what the model did when it won, not what made it win, and the report header says so.
`--resume` reruns an interrupted or extended run into the same `--out`: matches whose
`.match.json` is already there are loaded, not replayed, and only the missing ones run;
a record that disagrees with its job (arm, opponent, map, split, seed, benchmark,
`--max-seconds`, and a run fingerprint: code identity, rules hash, `--playbooks` hash, `--knob`s, LLM model,
endpoint and latency, `--dataset` hash) is refused with an error naming the differing field.
A partly recorded `bandit` arm is refused (it learns across matches in order); a complete
one is loaded without being re-trained, so its leakage probe (which runs on the fresh, untrained
learner) may differ from the original run's. An LLM arm skipped because every first-match
proposal failed leaves its record on disk, so resuming after fixing credentials skips it again
until those records are deleted. `--resume` cannot be combined with
`--no-decisions`. `results.json` and `report.md` cover loaded and new matches alike.
To re-run a
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
Neither is an arm whose model calls mostly failed: `--max-llm-failure-rate F`
(default 0.2) marks a live LLM arm "not a model result" in `report.md`, warns at
the end of `arena run`, and refuses its tier evidence, when its pooled failed
share of calls exceeds F. A call is a Primary `strategy.proposal` (answered) or a
transport or model failure such as `claude.timeout` (failed); `superseded` and
`no_opinion` are ordinary arbitration outcomes and not counted. Each match
records `llmCalls` (`answered`, `failed`, `failureRate`) in `results.json`, and
the win-rate table has an "LLM delivery" column; no match is dropped, since
otherwise the selector fallback's wins would be credited to the model.

The `llm`, `llm-shadow` and `llm+fast` arena arms (`src/Bindery.Ra2.Bot.Claude`)
need `ANTHROPIC_API_KEY` in the environment, or a resolvable `ant auth`
profile. Without either, those arms are skipped with a recorded reason; pass
`--llm-fake` to exercise the pipeline with a scripted client instead of a live
call.
`--extended-metrics` additionally offers those arms the enemy-composition and
map-control condition metrics (`EnemyAirShare`, `EnemyVehicleShare`,
`EnemyInfantryShare`, `EnemyArmyConfidence`, `OwnedRegions`) in the prompt and
output schema; without it prompts and schemas are unchanged. The class shares
read 0 until the seen enemy army is worth at least 600, so one scout is not a
composition.

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
  observation/v1 also has no separate entity-state event: an entity's
  position, health and owner are only as current as the source's last upsert
  of it, so the native source must re-send upserts for own units and, once per
  frame cadence, for every enemy in sight (an enemy not re-sighted drops out of
  the frame and lives on only in belief memory).
