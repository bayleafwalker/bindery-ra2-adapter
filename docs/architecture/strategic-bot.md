# Strategic bot: hierarchical RA2/YR AI with an optional LLM chief of staff

Implements the operator directive in
[`strategic-bot-proposal.md`](strategic-bot-proposal.md): a conventional
hierarchical RTS bot, layered by decision timescale, with at most one slow LLM
that chooses intent. Everything real-time is deterministic.

## Decisions

| Question | Decision |
|---|---|
| Where the bot lives | `src/Bindery.Ra2.Bot` (engine-agnostic, `net8.0`, no Windows dependency). The adapter stays the transport/lifecycle boundary. |
| LLM layering | One optional strategic LLM (`src/Bindery.Ra2.Bot.Claude`). The "fast LLM" exists only as an evaluation arm (`llm+fast`) restricted to re-parameterising the active playbook; it is not a default layer. |
| LLM authority | Selects and parameterises validated playbooks (build-sequence step 5). It cannot emit commands, unit IDs or cells. Shadow mode (step 4) records proposals without applying them. |
| Development environment | A deterministic region-level skirmish simulator (`src/Bindery.Ra2.Bot.Sim`) provides observe, act, reset, replay and logging offline (step 1), with fog per player and an explicit oracle mode. It is an approximation, not retail RA2, and every arena result says so. |
| Retail RA2 route | The adapter's ra2yrcpp seam: telemetry in (`IRa2TelemetrySource`), commands out through a new `IRa2CommandTransport` seam. The native command RPC belongs to the ra2yrcpp fork, as telemetry decoding already does; this repo does not invent it. |
| Chrono Divide | Next integration target (TypeScript bot API, headless). Out of scope for this slice; it plugs in as another `IObservationSource` + `ICommandSink`. |
| Rules facts | `IRulesDatabase` loaded from JSON. Two sources: an operator-run importer over their own `rulesmd.ini` (output kept outside the repo, hash-qualified ruleset ID), and a committed approximate fixture `bindery-sim-approx` for the simulator and tests. No retail game files enter the repo (`ci/verify-no-assets.ps1`). |
| Model | `claude-opus-5` by default for the strategist, adaptive thinking, effort `low` (latency bound: the cadence is 10–30 s); `claude-haiku-4-5` for the `llm+fast` arm. Both configurable. |

## Layers and cadence

All cadences are in game frames (`GameTime.FramesPerSecond = 15`).

| Layer | Type | Cadence | Contract |
|---|---|---|---|
| Belief | `BeliefModel` | every frame | `IBeliefModel` → `BeliefSnapshot` (versioned) |
| Features/events | `FeatureCompiler` | every frame (trend ring buffer) | `IFeatureCompiler` → `StrategicFeatures` |
| Tactical | `SquadController`, `HarvesterSafetyController`, `RepairController`, `DeployController` | every frame at 15 Hz, configurable down to 5 Hz | `ITacticalController` |
| Operational | `OperationalPlanner` | every 1 s and on `StrategicEvent` severity ≥ 0.5 | `IOperationalPlanner` → `OperationalPlan` |
| Strategic | any `IStrategist` | every 20 s (configurable 10–30 s) and on major event, asynchronous | `IStrategist` → `StrategistProposal` |
| Validator/arbiter | `IntentValidator`, `IntentArbiter` | every proposal | `IIntentValidator`, lease manager, command gate |

`BotRuntime.Tick(ObservationFrame)` runs one frame: apply belief, compile
features, collect any completed strategist proposal (never blocking), validate
and arbitrate, run operations on its cadence, run tactics, pass all commands
through the command gate, return the commands.

## Invariants (each has a test that forces its failure case)

1. **Fog.** Strategists receive `StrategistContext`, which contains features
   compiled from belief only. In `Belief` mode no feature may depend on an
   enemy object that was never observed. A test runs the simulator in belief
   mode, mutates hidden enemy state, and asserts the strategist context is
   byte-identical. The validator rejects intents that name enemy tech the
   player has not seen (`fog.unknown_type`) or regions outside the map.
2. **Freshness.** A proposal based on snapshot version *v* is discarded
   (`strategy.late_discarded`) when the current time is more than
   `MaxProposalAgeSeconds` (default 15 s) past the snapshot's time, or when a
   `StrategicEvent` with severity ≥ 0.7 occurred after it. Expired intents
   (`ExpiresAt`) fall back to the deterministic selector.
3. **Commitment and hysteresis.** An active intent holds for the playbook's
   `MinCommitSeconds` (default 45 s). Within that window a replacement is
   accepted only if an abort trigger of the active intent fires, or a base
   threat ratio exceeds 1.5. Posture changes need the challenger's confidence
   to exceed the incumbent's by a margin (default 0.15). Squad retreat uses
   separate engage/disengage force-ratio thresholds (retreat below 0.6,
   re-engage above 1.0) and a minimum 5 s in state.
4. **Single ownership.** Every unit, squad and budget pool has at most one
   live lease. The command gate drops (and logs `command.dropped`) any unit
   command whose controller does not hold that unit's lease. Budget pools are
   reserved in a ledger; total reservations never exceed credits on hand plus
   one planning period of forecast income.
5. **Validation before effect.** Nothing a strategist returns reaches the
   planner without `IIntentValidator.Validate` accepting it. Rejections carry
   stable codes. Playbook parameters are clamped to their declared ranges;
   budgets are normalised; missing fields take playbook defaults. Tech goals
   must be reachable for the faction via `IRulesDatabase.PathTo`.
6. **Determinism.** Given the same seed, simulator, and deterministic
   strategists, a match replays identically (same decision log hash). LLM
   arms are recorded, and a recorded run can be replayed from its decision
   log with a `ReplayStrategist`.

## Work packages and file ownership

Contracts in `src/Bindery.Ra2.Bot/Contracts/` are fixed; a package that needs
a contract change records it in its report instead of editing the file.

| Package | Owns | Delivers |
|---|---|---|
| A. Belief & features | `src/Bindery.Ra2.Bot/Belief/`, `src/Bindery.Ra2.Bot/Features/` | `BeliefModel` (last-seen memory, confidence decay with configurable half-life, destroyed confirmation, region last-seen, enemy tech/faction inference from seen types), `FeatureCompiler` (5/15/60 s trends from a ring buffer of per-second samples, income from credit deltas net of spending, cash runway, production utilisation, army value by role and region clusters, losses/kills, enemy estimate with confidence, map control, expansion candidates, scouting coverage and important unknowns, threat assessments with ETA and likely attack path over the region graph and reinforcement time, strategic event detection with de-duplication), `RegionGraph` (Dijkstra over links, travel seconds from speed). |
| B. Rules & playbooks | `src/Bindery.Ra2.Bot/Rules/`, `src/Bindery.Ra2.Bot/Playbooks/`, `src/Bindery.Ra2.Bot/Data/` | `RulesDatabase` (JSON load, `PathTo`, `CanBuild`, `Effectiveness` from weapon-vs-armor matrix), `RulesmdImporter` (INI parser for an operator-supplied `rulesmd.ini`: `[BuildingTypes]`/`[VehicleTypes]`/`[InfantryTypes]`/`[AircraftTypes]` lists, `Cost`, `Strength`, `Prerequisite`, `Power`, `TechLevel`, `Owner`, `Armor`, `Primary`, `Speed`, `Sight`; build time derived from cost; role inference table; output JSON with `rulesetId = "rulesmd-sha256:<hash>"`), an embedded approximate fixture `bindery-sim-approx.json` covering Allied and Soviet core tech (power, refinery, barracks, factory, radar/tech buildings, battle lab, core infantry, tanks, anti-air, artillery, harvester, MCV, defenses, one superweapon each), a `PlaybookLibrary` with 12 authored playbooks (e.g. `allied-boom`, `allied-grizzly-timing`, `allied-ifv-mix`, `allied-prism-turtle`, `allied-harass`, `soviet-rhino-rush`, `soviet-flak-mix`, `soviet-v3-siege`, `soviet-apoc-tech`, `soviet-turtle`, `generic-defend`, `generic-expand`). |
| C. Arbitration & runtime | `src/Bindery.Ra2.Bot/Arbitration/`, `src/Bindery.Ra2.Bot/Runtime/` | `ConditionEvaluator`, `IntentValidator`, `IntentArbiter` (commitment, hysteresis, abort/replan triggers, expiry, fallback), `LeaseManager`, `CommandGate`, `BudgetLedger`, `StrategyScheduler` (async proposal lifecycle, cadence + event triggers, one in-flight request, late discard, shadow mode that records a second strategist's proposals without applying them), `DecisionLog` (in-memory + NDJSON writer), `BotRuntime` and `BotOptions`, `ReplayStrategist`. |
| D. Operations & tactics | `src/Bindery.Ra2.Bot/Operations/`, `src/Bindery.Ra2.Bot/Tactics/` | `OperationalPlanner` (budget split by intent shares; production chosen to close composition gaps using rules cost/build time/effectiveness vs known enemy composition; tech path via `PathTo`; power upkeep; harvester/refinery targets; build placement at region level with candidate cells around own base; squad formation from unleased combat units; objective assignment by priority; reinforcement; attack only when attack conditions hold), tactical controllers (`SquadController`: target selection by effectiveness × low health, focus fire, retreat/re-engage hysteresis, path via region graph; `HarvesterSafetyController`; `RepairController`; `DeployController` for MCV/deployables). |
| E. Deterministic strategists | `src/Bindery.Ra2.Bot/Strategy/` | `PlaybookSelector` (rule-based baseline over features), `ContextualBanditStrategist` (LinUCB over playbooks with a fixed feature vector, `IOutcomeLearner`), `FeatureVector` (shared numeric encoding, documented order), `DistilledStrategist` (multinomial logistic regression trained from a decision dataset; escalates to an inner strategist when the state is out of distribution by Mahalanobis-diagonal distance), `DecisionDataset` (export/import from decision logs). |
| F. Claude strategist | `src/Bindery.Ra2.Bot.Claude/` | `ClaudeStrategist` (Anthropic C# SDK 12.50.0; `claude-opus-5`; structured output via `OutputConfig`/`JsonOutputFormat` with a JSON schema for an `IntentDraft` DTO; adaptive thinking; effort option; per-request timeout; refusal and max-token stop reasons return null with a logged reason; token usage into `ProposalCost`), `IntentPromptBuilder` (compact deterministic JSON of features, relevant rule facts and the faction's playbook catalogue; stable system prompt first for prompt caching), `IntentDraftMapper` (DTO → `StrategicIntent`), `ClaudeStrategistOptions` including `Mode = Strategic \| Refine` (Refine = the `llm+fast` arm: may only change parameters of the active playbook). Behind an `IMessageClient` seam so tests run on canned responses; one live smoke test runs only when `BINDERY_BOT_LIVE_LLM=1`. |
| G. Simulator & arena | `src/Bindery.Ra2.Bot.Sim/`, `tools/Bindery.Ra2.Bot.Arena/` | Region-graph skirmish simulator: seeded RNG, 3–5 authored maps (2-player; held-out split), economy (harvest trips, ore depletion), production queues with prerequisites and power penalty, building placement, movement along region links, combat resolution per region using rules damage × effectiveness, fog per player (sight by region presence + last-seen), per-player `IObservationSource`/`ICommandSink`, oracle mode, reset, replay log. Opponents: the independent scripted AI in `src/Bindery.Ra2.Bot.Sim/Opponents/` (`ai-rush`, `ai-balanced`, `ai-turtle`, `ai-air`, easy/medium/hard; retail-style build lists and task-force waves, own observation frame only, no code shared with the bot's planner), and styles (`rush`, `turtle`, `tech`, `harass`, `balanced`) pinned to playbooks on a frozen copy of the stack as of 7f3e2c7 (`tools/Bindery.Ra2.Bot.Baseline`; `live-<style>` on the live stack). Arena CLI: arms × maps × opponents × seeds → JSON + Markdown report with win rate (held-out maps separate), invalid plans, decision lateness, strategic churn, production idle time, resource float, trade efficiency, inference cost, hidden-information leakage probe. |
| H. RA2 bridge & docs | `src/Bindery.Ra2.Adapter/Bot/`, adapter tests for it, `README.md`, CI | `Ra2ObservationAssembler` (folds normalized ra2yrcpp observations into `ObservationFrame`s; fields the telemetry does not carry are reported as missing, not invented), `IRa2CommandTransport` seam and `Ra2CommandSink`, `Ra2BotHost` (the host loop: telemetry source → normalizer → assembler → `BotRuntime.Tick` → sink, flushed per frame, until match end; tested against `RecordedRa2TelemetrySource` and a fake transport), CI steps building and testing the bot projects on Linux and Windows. |

## Arena arms

| Arm | Strategist | Notes |
|---|---|---|
| `selector` | `PlaybookSelector` | baseline |
| `bandit` | `ContextualBanditStrategist` | learns across matches within a run |
| `llm-shadow` | `PlaybookSelector` active, `ClaudeStrategist` shadow | step 4 |
| `llm` | `ClaudeStrategist`, selector fallback | step 5 |
| `llm+fast` | `llm` plus `ClaudeStrategist(Mode=Refine, claude-haiku-4-5)` at 5 s | comparison only |
| `distilled` | `DistilledStrategist` over a dataset from `llm` runs | step 7 |
| `*-oracle` | any arm with oracle frames | diagnostic; labelled |

LLM arms need `ANTHROPIC_API_KEY` (or an `ant auth` profile) and are skipped
with a recorded reason when no credential resolves. With no credential, a
`--llm-fake` flag substitutes a recorded or scripted client so the pipeline
itself is exercised.

## Parameter tuning

`tools/Bindery.Ra2.Bot.Tune` runs a seeded separable CMA-ES
(`src/Bindery.Ra2.Bot/Tuning/EvolutionStrategy.cs`, no external packages)
over the unit cube of `TuningSpace`: every playbook parameter the planner reads
(`attackArmyValue`, `defendThreatRatio`, `expandAtSeconds`,
`harassIntervalSeconds`, `harvesterTarget`, `retreatBelowForceRatio`,
`siegeRangeBufferCells`; a test requires every declared parameter to have a
consumer; the first tuning run below predates the last three) and
the option knobs in `TuningKnobs` (12 `OperationalOptions`, 3
`FeatureOptions`, each with its range and reason). Search uses training maps
only, with common random numbers per generation; validation uses held-out maps
against the untuned bot and a head-to-head with a seat-bias control. The
result is embedded as `Data/tuned-parameters.json` with provenance and
validation; the bot applies it only when `adopted` is true.

First run (2026-09-26, from 2322ab8): 24 generations × 12 samples,
opponents every `ai-*` and pinned style plus `bot:default`/`bot:champion`,
22,770 training matches in 1,405 s. Fitness never separated from the untuned
bot (best − default ≤ 0.0008 per generation; every candidate won 60/66, the
six losses per generation being faction-decided self-play games). Held-out
(2 maps × 9 opponents × 20 seeds): untuned 360/360, tuned 360/360; paired
fitness difference 0.0001 [−0.0001, 0.0003]; head-to-head 40/80 against a
40/80 control. Not adopted. The simulator benchmark is saturated and outcomes
are fixed by faction and seat, so it cannot rank these knobs; tuning needs a
harder or less deterministic benchmark first.

## Metrics definitions

- **Win rate**: wins / completed matches, reported separately for training
  and held-out maps and per opponent style, with match counts.
- **Invalid plans**: rejected proposals / proposals.
- **Decision lateness**: seconds from the snapshot a proposal was based on to
  its validation; late-discard count / proposals.
- **Strategic churn**: intent activations per 10 game minutes; posture flips
  per 10 minutes.
- **Production idle time**: seconds with at least one factory and no queued
  item while credits ≥ the cheapest buildable item / match seconds.
- **Resource float**: time-averaged credits on hand.
- **Trade efficiency**: enemy value destroyed / own value lost.
- **Inference cost**: input/output tokens and USD per match at the published
  per-million-token rate for the configured model.
- **Hidden-information leakage**: count of validator `fog.*` rejections, plus
  the arena probe that perturbs hidden simulator state and diffs the
  strategist context hash (must be 0 differences).

## Integration record

Recorded when the eight work packages were merged into `feat/strategic-bot`.

- **Package E** (deterministic strategists) was not delivered: its agent was
  launched in a checkout of a different repository. It was implemented during
  integration in `src/Bindery.Ra2.Bot/Strategy/`, together with
  `IntentComposer` (playbook defaults plus posture-derived objectives, shared by
  every deterministic strategist), `PinnedPlaybookStrategist` (arena opponent
  styles), `TwoSpeedStrategist` (the `llm+fast` primary slot) and
  `StrategistContextHash` (fog probe).
- **Contract changes** (each minimal): `BotJson.Options` writes non-finite
  doubles as named literals and `RegionId` dictionary keys as numbers, so
  features and belief serialise; `ObservationFrame.OreRemaining` (visible ore
  regions only, optional) and `BeliefSnapshot.OreLastSeen` make
  `EconomyFeatures.OreRemainingFraction` measurable; doc comments state the
  `OperationalPlan.BudgetReservations` key convention, who leases units for
  tactical controllers, and the 9999-second "unknown" convention. Not applied:
  owned building types in `StrategistContext`, an `IStrategistDiagnostics`
  interface, and moving `IFrameAwareStrategist` into the contracts (the arena
  subscribes to `ClaudeStrategist.ProposalFailed` instead).
- **Single definitions.** The planner's private condition evaluator was deleted;
  `ConditionEvaluator` is the only definition of every metric. The
  `strategy.intent_activated` record carries the `DecisionDataset` payload
  (`faction`, `featureVersion`, `features`, canonical `intent`).
- **Budget ledger.** The runtime runs the ledger in accrual mode: pools are
  running accounts fed by share, and a pool short of an item may borrow other
  pools' unreserved balance after every pool has reserved from its own. A
  per-period split of credits on hand could never afford a 2000-credit
  refinery from a 30% pool once credits fell, which stalled every economy.
- **Selector default.** In a style-versus-style matrix on training maps only
  (5 opponent styles × 3 maps × 4 seeds per style), the mixed-army playbooks
  won 47/60, tech 43, rush and harass 36, turtle far less; the selector's
  default is the faction's mixed army, with armour kept as the answer to a
  confident armour-heavy enemy. (An earlier exploratory matrix also included
  held-out maps; the choice was re-checked on training maps before adoption.)
  The arena's held-out split holds out maps, not opponents: the five
  pinned-playbook styles that chose this default are the same opponents the
  arena reports against, so selector and distilled-arm win rates against them
  are partly in-sample, and `report.md` says so. The independent `ai-*` opponents
  share no planner code with the bot, but the strength work iterated against
  them (training maps only), so they too are held out by map, not by opponent.
- **Arena datasets** (`dataset-<arm>.ndjson`) contain training-map decisions
  only, so a distilled arm is never trained on the maps it is evaluated on.

## Completeness round 1 (2026-09-26)

Gaps an operator review found after integration, and how each was closed.

- **Every playbook parameter and intent field acts.** `defendThreatRatio` is the
  base threat ratio at which the planner pulls the army to the threatened base
  (default 1.0, the previous fixed value; `generic-defend`'s default is now 1.0
  too). `harassIntervalSeconds` times harass sorties (out, dwell
  `HarassDwellSeconds`, home to regroup, next sortie one interval after the
  last). `siegeRangeBufferCells` reaches tactics as
  `SquadOrder.StandoffBufferCells`: a squad with artillery holds outside a known
  defense's region, at least its range plus the buffer away and within artillery
  range, and bombards it; with no such cell it assaults as before.
  `RegionsOfInterest` are scouted first and are the staging area (first one that
  is reachable, not the attack target and not enemy-held). Each has a test that
  fails if it is ignored. For stand-off to mean anything the simulator now lets a
  unit with no target in its own region fire at an enemy in another region
  within its weapon range in cells (same-region fire is unchanged and takes
  precedence); an attack order on a target already in range no longer walks into
  its region. Contract change: `SquadOrder.StandoffBufferCells` (optional,
  default 0).
