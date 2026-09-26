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
| G. Simulator & arena | `src/Bindery.Ra2.Bot.Sim/`, `tools/Bindery.Ra2.Bot.Arena/` | Region-graph skirmish simulator: seeded RNG, 3–5 authored maps (2-player; held-out split), economy (harvest trips, ore depletion), production queues with prerequisites and power penalty, building placement, movement along region links, combat resolution per region using rules damage × effectiveness, fog per player (sight by region presence + last-seen), per-player `IObservationSource`/`ICommandSink`, oracle mode, reset, replay log. Opponents: the independent scripted AI in `src/Bindery.Ra2.Bot.Sim/Opponents/` (training `ai-rush`, `ai-balanced`, `ai-turtle`, `ai-air`, held-out `ai-horde`, `ai-armor`; easy/medium/hard; retail-style build lists and task-force waves, own observation frame only, no code shared with the bot's planner), and styles (`rush`, `turtle`, `tech`, `harass`, `balanced`) pinned to playbooks on a frozen copy of the stack as of 7f3e2c7 (`tools/Bindery.Ra2.Bot.Baseline`; `live-<style>` on the live stack). Arena CLI: arms × maps × opponents × seeds → JSON + Markdown report with win rate (held-out maps and held-out opponents separate), invalid plans, decision lateness, strategic churn, production idle time, resource float, trade efficiency, inference cost, hidden-information leakage probe. |
| H. RA2 bridge & docs | `src/Bindery.Ra2.Adapter/Bot/`, adapter tests for it, `README.md`, CI | `Ra2ObservationAssembler` (folds normalized ra2yrcpp observations into `ObservationFrame`s; fields the telemetry does not carry are reported as missing, not invented), `IRa2CommandTransport` seam and `Ra2CommandSink`, `Ra2BotHost` (the host loop: telemetry source → normalizer → assembler → `BotRuntime.Tick` → sink, flushed per frame, until match end; tested against `RecordedRa2TelemetrySource` and a fake transport), CI steps building and testing the bot projects on Linux and Windows. |

## Arena arms

| Arm | Strategist | Notes |
|---|---|---|
| `selector` | `PlaybookSelector` | baseline |
| `bandit` | `ContextualBanditStrategist` | learns across matches within a run: one learner per arm (oracle and personality variants apart), training maps against training opponents only |
| `llm-shadow` | `PlaybookSelector` active, `ClaudeStrategist` shadow | step 4 |
| `llm` | `ClaudeStrategist` at the adopted vocabulary tier, selector fallback | step 5 |
| `llm-t0` … `llm-t3` | `ClaudeStrategist` at `PlaybookOnly`, `Parameters`, `ObjectivesAndRegions`, `Full` | step 6; `--arms tiers`; adoption rule in the report and `vocabulary-adoption.json` |
| `llm+fast` | `llm` plus `ClaudeStrategist(Mode=Refine, claude-haiku-4-5)` at 5 s | comparison only |
| `distilled` | `DistilledStrategist` over a dataset from `llm` runs, escalating to `ClaudeStrategist` | step 7; escalation rate and cost per match in the report |
| `*-oracle` | any arm with oracle frames | diagnostic; labelled; `--arms x,x-oracle` or `--oracle both` runs both on the same jobs and reports belief − oracle per metric |

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
  the arena probe that perturbs hidden simulator state (enemy credits and
  queue, wounded hidden enemies, a hidden unit in an unseen region and one just
  across a border inside the arm's weapon reach) at 90 s and 240 s and diffs the
  strategist context hash for up to 60 s, stopping early only when the objects
  the arm can see first differ (must be 0 differences). Sim-level lockstep tests
  (`SimFogLeakTests`) cover the combat and event paths the real bot may not reach
  in that window. Entity ids are numbered per owner, so an id never reveals how
  much the enemy has built.

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
  tactical controllers, and the 9999-second "unknown" convention. Not applied
  then: owned building types in `StrategistContext` (applied in completeness
  round 1, below), an `IStrategistDiagnostics`
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
  The five pinned-playbook styles that chose this default, and the training
  `ai-*` styles the strength work and the tuner iterated against, are training
  opponents: win rates against them are partly in-sample, and `report.md` says
  so.
- **Held-out opponents** (`OpponentSets`, round 2). `ai-horde` (barracks-first
  infantry mass behind early defences, large late waves) and `ai-armor`
  (infantry-screened second war factory, large tank waves, 1.5× income) are
  independent scripted styles written for evaluation only. The arena never
  credits the bandit with a match against one or on a held-out map (the episode
  is abandoned), never
  puts one in a distillation dataset or teacher run, and the tuner refuses them;
  the selector default above predates them. `--opponents heldout` runs them and
  the contested benchmark's defaults include them. `report.md` has a "Held-out
  opponents" section: win rate by opponent split × map split and paired tables
  on held-out opponents only. Calibration (2026-09-26, contested, all maps, 4
  seeds, 40 matches per arm, `--llm-fake`): selector 24/40 (0.60), bandit 21,
  llm-shadow 24, llm 22, llm+fast 20, distilled 27; the training `ai-*` and
  frozen pinned styles lose every contested game, which is why they cannot
  serve as the held-out set.
- **Arena datasets** (`dataset-<arm>.ndjson`) contain training-map decisions
  against training opponents only, so a distilled arm is never trained on the
  maps or opponents it is evaluated on. The distilled arm's teacher always plays
  the training maps (whatever `--maps` says) and the run's training opponents
  (the contested live styles when the run names only held-out ones); below 20
  examples the arm is skipped with the reason recorded.

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
  range, and bombards it; with no such cell (or only water or unreachable
  ones) it assaults as before. The retreat hysteresis runs first, so an
  outnumbered sieging squad retreats. An order that does not engage (harass
  return, retreat squad, scout) moves plainly: an attack-move holds in any
  region with an enemy in it.
  `RegionsOfInterest` are scouted first and are the staging area (first one that
  is reachable, not the attack target and not enemy-held). Each has a test that
  fails if it is ignored. For stand-off to mean anything the simulator now lets a
  unit with no target in its own region fire at an enemy in another region
  within its weapon range in cells (same-region fire is unchanged and takes
  precedence), provided its owner can see that region: RA2 cannot acquire a
  target in fog, so stand-off bombardment needs a spotter. A kill event reaches
  the killer only when the victim's cell is visible to it, and a superweapon
  launch names the firing building only to its owner; an attack order on a target already in range no longer walks into
  its region. Contract change: `SquadOrder.StandoffBufferCells` (optional,
  default 0).
- **State compiler completeness.** Superweapons: the simulator charges a
  powered superweapon building (`SimSettings.SuperweaponChargeSeconds`, RA2's
  10 minutes by default; low power pauses it), fires it on
  `LaunchSuperweaponCommand` (damage within a radius, both sides) and announces
  `SuperweaponLaunched` to every player. Every frame carries every superweapon
  timer (`ObservationFrame.Superweapons`), as RA2 shows them to all players; an
  enemy timer names owner, type and countdown, not the building. Features:
  `StrategicFeatures.Superweapons` (own and enemy charge fraction, seconds to
  ready, ready), `SuperweaponDetected` on a new enemy superweapon and on an
  enemy (not own) launch; the planner fires a ready superweapon at the known
  enemy building area with the most known value net of the own objects the
  strike would also hit, and holds it when no target is worth more. `ForceCluster.ValueByRole`
  gives army value by role and location; `EnemyFeatures.TechLastSeenAgeSeconds`
  (from `EnemyPlayerBelief.TechLastSeen`) gives an age per known enemy tech and
  production item. All reach the LLM prompt. Contract changes (all optional,
  additive): `SuperweaponStatus`, `ObservationFrame.Superweapons`,
  `BeliefSnapshot.Superweapons`, `EnemyPlayerBelief.TechLastSeen`,
  `ForceCluster.ValueByRole`, `EnemyFeatures.TechLastSeenAgeSeconds`,
  `SuperweaponTimer`, `SuperweaponFeatures`, `StrategicFeatures.Superweapons`,
  `LaunchSuperweaponCommand` (sink kind `launch_superweapon`). The RA2
  telemetry contract (observation/v1) does not carry superweapon timers yet,
  so a retail frame's `Superweapons` is null.
- **Rule facts for the LLM.** `StrategistContext.OwnedBuildingTypes` (the
  player's own buildings, so fog-safe; the scheduler fills it from belief) lets
  the prompt's situation carry `techProgress`: each playbook tech goal's
  remaining prerequisite path, cost and serial build seconds from the base
  actually owned, and whether it can be started now. `counters` lists, for each
  enemy unit type the player has seen, the faction's three most effective armed
  unit types by the rules' effectiveness multiplier (only types that can hit it:
  anti-air weapons hit aircraft only), with effective damage, cost and whether
  each is buildable now. The empty-base rule facts stay in the cacheable match
  context. Contract change: `StrategistContext.OwnedBuildingTypes` (optional).
- **A benchmark that can tell arms apart.** Under the original setting every
  arm won 180/180 and the `llm` arm replayed the selector's games. The
  simulator gains per-player income multipliers and starting credits and seeded
  combat noise (`SimSettings.CombatNoise`, its own RNG stream). An income bonus
  for the scripted or frozen opponents did not help them (the selector still won
  60/60 at 2.5× opponent income: they lose to the early army, not for money);
  mirror play against the live-stack pinned styles gave 50%, but decided by
  faction (the Soviet side won every mirror). `--benchmark contested` therefore
  uses the five live-stack styles, gives the Allied side 20,000 starting credits
  and ±25% combat noise: calibration (selector, all maps, 6 seeds, 150 matches)
  73/150 = 0.49, as Allied 28/75, as Soviet 45/75 (noiseless: 69/150).
  `FakeMessageClient` now plays its own policy (`fake-counter-v2`: armour timing
  first, anti-air on seen aircraft, tech on two refineries, parameters from the
  enemy estimate, the counters table's best role in the composition, regions of
  interest) instead of the selector's. The report pairs matches by opponent,
  map and seed against `--baseline` and tests every metric (sign test, Holm,
  bootstrap interval) and flags a saturated baseline. First contested run
  (`--arms selector,llm --maps all --seeds 4 --llm-fake`, 100 pairs, fake
  client): selector 49/100, `llm` 32/100; paired score −0.17 [−0.30, −0.03],
  16 better / 33 worse (sign p 0.021, Holm 0.149); first attack 54 s earlier,
  trade share −0.11 (Holm 0.035), activations +3.2 per 10 min. These measure the
  fake policy, not a model.
- **Oracle side by side.** `--oracle` turned every arm into its oracle version,
  so belief and oracle never shared a report. An arm named `x-oracle`, or
  `--oracle both`, now runs next to `x` on the same jobs; `report.md` adds
  "Perception bottleneck (belief − oracle)", the paired table of `x` against
  `x-oracle`. First run (contested, selector, all maps, 4 seeds, 100 pairs):
  belief 49/100, oracle 81/100; score −0.32 [−0.44, −0.21] (5 better, 37
  worse, Holm p < 0.0001), trade share −0.16, value destroyed −3138: in this
  simulator perception costs the selector about a third of a win per match.
- **Distillation teacher and escalation.** Without `--dataset` the distilled arm
  trained on the selector's decisions and escalated to a second selector. It now
  trains on the `llm` arm's primary decisions on the run's training maps (that
  arm runs first; if it was not requested it runs unreported, and without a
  credential the distilled arm is skipped with the reason), and escalates to the
  Claude strategist behind the same simulated latency (`DistilledStrategist`
  forwards `OnFrame` to it, so an escalation completes on its latency frame and
  the scheduler never waits for it inline). The log shows who decided (an
  escalated proposal is Claude's, source `Llm`); the report's Distillation table
  gives decisions, escalations and rate from the log next to cost per match and
  the teacher's cost. First contested run (fake client, 100 matches each):
  `llm` 32/100 at $1.13 per match (list price on estimated tokens), `distilled`
  39/100 at $0.0028 per match, 10 escalations in 3,246 decisions (0.3%),
  trained on 1,848 examples.
- **Replaying a recorded match.** `ReplayStrategist` existed but the arena never
  kept per-match logs in a form it could read. The arena now writes each arm
  match's decision log through `NdjsonDecisionLogWriter` to
  `decisions/<match>.ndjson` with a `<match>.match.json` manifest
  (`bindery.arena.match/v1`: arm, opponent, map, split, seed, match length,
  benchmark, LLM latency, recorded hash, result), and `arena replay <log>`
  rebuilds the arm exactly as the run did with its primary and shadow
  strategists replaced by `ReplayStrategist`s, re-plays the match and compares
  hashes (exit 0 equal, 2 different, with the first differing record, requests
  without a recording, recorded answers never asked for; `--out` writes the
  replayed log). Fixes this needed in `ReplayStrategist`: shadow answers are
  read from `strategy.shadow` records; a request the recording made but never
  saw answered (in flight at match end) stays unanswered instead of failing;
  and records a live strategist writes itself while being asked (the arena's
  role-less Claude failure records) are written back at the same point
  (`echoLog`). Tests replay `llm`, `llm-shadow`, `llm+fast`, `bandit` and
  `distilled` matches to identical hashes and catch an edited log.
- **Explanations and post-game analysis.** `PostGameReport`
  (`src/Bindery.Ra2.Bot/Analysis`) is built from a decision log alone and is
  deterministic: the timeline of intents that took effect (playbook, posture,
  source and role, confidence, rationale, the trigger of the request that
  produced it, the arbiter's reason, renewals, how it ended), pivots with their
  trigger and the previous intent's end reason, proposals without effect
  (validator codes, late reasons, arbiter refusals), key events (event and
  replan triggers, aborts, first attack and defence, superweapon launches, the
  result, which the arena now logs as `match.result`), shadow agreement (shadow
  proposals naming the primary's playbook for the same request) and time by
  playbook and posture. `arena analyze` renders it; `--narrate` adds a narrative
  from `PostGameNarrator` (Claude, structured output, behind `IMessageClient`;
  the fake client scripts one from the report and says so). Every deterministic
  strategist's rationale now names the playbook, the rule or model that chose it
  and the evidence (`StrategyRationale`: time, base threat, army ratio with the
  estimate and its confidence, refineries, harvesters, scouting). The run report
  shows shadow agreement next to the shadow proposal count.
- **Vocabulary tiers (build step 6).** The LLM could set every intent field with
  no measured justification. `ClaudeStrategistOptions.Vocabulary` now names a
  tier: `PlaybookOnly` (the choice, confidence, expiry, assumptions,
  rationale), `Parameters` (+ playbook parameters), `ObjectivesAndRegions`
  (+ objectives, regions of interest), `Full` (+ posture, budget, composition,
  conditions). `IntentVocabulary.Restrict` replaces fields above the tier with
  the playbook's defaults and posture-derived objectives (`IntentComposer`, as
  the deterministic strategists do) before validation, and the system prompt
  states the tier. The default is the tier adopted in the embedded
  `vocabulary-adoption.json`, `Parameters` (step 5's authority), since no live
  held-out comparison exists yet. `VocabularyAdoption.AdoptionRule`: a wider
  tier is adopted only if the tier below is adopted and, on held-out maps, a
  live-model paired comparison against it shows more pairs won than lost, sign
  p < 0.05 and a score interval above 0. Arena arms `llm-t0`…`llm-t3`; the
  report adds per-pair tables of each tier against the one below and the
  verdict; the run writes `vocabulary-adoption.json` (`--write-adoption` to
  update the embedded record). First fake run (contested, all maps, 4 seeds, 100
  pairs per comparison): t0 39, t1 37, t2 25, t3 32 wins of 100; t2 vs t1 score
  −0.12 [−0.23, 0], 12/24 pairs better/worse (p 0.065); verdict `Parameters`
  (fake evidence never counts). Note that the plain `llm` arm now runs at
  `Parameters`; the `llm` figures earlier in this record were at `Full`.
- **Personalities.** `BotOptions.Personality` reached only the LLM prompt. Four
  authored styles (`Personalities`: aggressive, turtle, tech, harasser) each
  carry prompt guidance (the prompt now sends id, guidance and the faction's
  preferred playbook instead of the bare name), a preferred playbook per
  faction, parameter scaling and a defence threshold. The selector plays the
  preferred playbook whenever no defence, outnumbered or anti-air rule forces
  another and scales its parameters; the bandit adds a score bonus to it; the
  distilled model adds a logit bias to it when it is a trained class; rationales
  name the style. `--personality` multiplies arms by styles (`selector@turtle`)
  and the report's Play styles section gives playbook and posture shares, time
  to first attack and pairwise Jensen–Shannon divergences. First run
  (contested, all maps, 2 seeds, 50 matches per arm and style, fake client):
  selector styles differ in playbook time by JSD 0.74–0.82 against each other
  and the default; postures separate turtle and tech from the rest (JSD
  0.73–0.82) while aggressive and harasser remain mostly Pressure (0.01–0.19);
  the turtle style attacks first 61–80 s later; wins: default 25, aggressive
  19, turtle 33, tech 20, harasser 24 of 50. The fake `llm` styles are less
  distinct (playbook JSD 0.05–0.58): its policy only consults the style after
  its opening push.
- **Runs after round 1** (fake client throughout; no live model was run).
  The original command (`--arms all --maps all --opponents
  ai-balanced,rush,tech --seeds 2 --llm-fake`) still has every arm winning
  30/30, and the report now says the benchmark is saturated; the `llm` arm plays
  its own playbooks there (armour timing, 2.6 more activations per 10 min) but
  the outcome cannot move. Contested (`--arms all --maps all --benchmark
  contested --seeds 4 --llm-fake`, 100 matches per arm, paired against the
  selector's 49/100): bandit 73 (score +0.24 [0.13, 0.35], 30/6 pairs,
  Holm p 0.0008; it learns across the run), `llm-shadow` 49 (plays the
  selector; shadow agreed on the playbook 1,395/2,900 = 0.48), `llm` 37
  (−0.12 [−0.21, −0.02]), `llm+fast` 39, distilled 40 (5 escalations in 3,200
  decisions, $0.0016 per match against the teacher's $0.97). Sample replays of
  one match per LLM, bandit and distilled arm from that run all gave equal
  hashes with no missing recordings. The decision logs of 600 matches take
  272 MB (`--no-decisions` for large runs).


## Completeness round 2 (2026-09-26)

All LLM results in this section come from `--llm-fake`; no live model was run.

- **Held-out opponents.** See "Held-out opponents" under the integration
  record: `ai-horde` and `ai-armor` are evaluation-only, never learned from or
  tuned against, and reported in their own section of `report.md`.
- **The distilled arm's teacher always plays the training maps.** Before this,
  `--maps heldout` left the teacher with 0 examples and every distilled decision
  escalated to the LLM under the distilled label. Now the teacher runs on
  `SimMaps.Training` whatever `--maps` says, and a dataset under 20 examples
  skips the arm with the reason recorded. The operator's command (`--arms
  llm+fast,distilled --maps heldout --opponents live-rush --benchmark contested
  --seeds 1 --llm-fake`) now gives 106 examples, 0/46 escalations and $0 per
  match.
- **Changed unit rosters.** `arena run --rules <json>` (and `tune search|validate
  --rules`) plays every side on a `RulesDocument` in place of the embedded
  fixture; replay reads the file again from the match manifest. The committed
  variant `Data/bindery-sim-variant-roster.json`
  (`RulesDatabase.LoadEmbeddedVariantFixture`) renames MTNK to GTNK, removes
  HTK and V3, re-costs E2, HTNK and FV, and adds TNKD. The authored playbooks
  name type ids only in tech goals. As written, they fail validation cleanly:
  the validator rejects the intent with `type.unknown` and the fallback keeps
  control. `PlaybookRosterAdapter` fits them to the roster through rule facts.
  Each missing goal is replaced by the same-role unit, reachable by every
  playbook faction, preferring the same kind and then the nearest cost.
  A playbook with an irreplaceable goal is dropped, with the reason recorded.
  On the variant: `allied-grizzly-timing` MTNK → GTNK, `soviet-flak-mix`
  HTK → E4, and `soviet-v3-siege` is dropped (no artillery left). The scripted
  `ai-*` opponents fit their build lists the same way. The frozen pinned
  styles are not adapted.

  Run (contested, all maps, 2 seeds, opponents: the five `live-*` styles plus
  `ai-horde`, `ai-armor`, `ai-rush` and `rush`; 90 matches per arm, with the
  same command on the fixture in brackets): selector 57 [59], bandit 58 [67],
  llm-shadow 57 [59], llm 56 [47], llm+fast 57 [50], distilled 59 [49].
  0 rejected proposals in every arm on the variant.

  Against the held-out opponents, out of 20 matches per arm: selector 12 [14],
  bandit 13 [15], llm 11 [10], llm+fast 12 [11], distilled 14 [12].

  **Live model and retail roster.** The fake LLM policy picks playbooks by id
  from the adapted catalogue, so no fake-client result shows whether a live
  model reasons from the rule facts and counters in its prompt under a changed
  roster. That needs a live credential (`ANTHROPIC_API_KEY`, then the same
  command without `--llm-fake`).

  A retail mod roster needs an operator's `rulesmd.ini` through
  `RulesmdImporter`, then `--rules` on the JSON it writes. `Ra2BotHost` takes a
  built runtime, so a caller that builds it on imported rules should pass
  `PlaybookRosterAdapter.Adapt(PlaybookLibrary.LoadDefault().All, rules,
  RulesDatabase.LoadEmbeddedFixture()).Library` as the playbooks.
