# Strategic bot proposal (operator directive, 2026-09-25)

Recorded verbatim as submitted. The implementation spec is
[`strategic-bot.md`](strategic-bot.md).

---

Assuming RA2 means *Command & Conquer: Red Alert 2/Yuri’s Revenge*:

## Bottom line

The idea is sound if you reinterpret it as:

> A conventional hierarchical RTS bot with one optional LLM acting as a slow “chief of staff.”

The valuable hierarchy is by decision timescale—not by having several LLMs. The low-level system is not supplemental; it is most of the actual AI. For maximum competitive strength, the LLM may add little. For adaptable, varied, explainable, human-like play, a strategic LLM could be genuinely worthwhile.

## Adversarial review

The strongest parts of your proposal are:

- Extracting trends and changes rather than dumping raw state.
- Separating strategic and tactical timescales.
- Keeping rapid reactions deterministic.
- Letting higher layers express goals rather than individual clicks.

The questionable parts are:

- A “fast LLM” is usually the wrong middle layer. It remains slower, less spatially and numerically reliable, less reproducible, and harder to debug than utility logic, behavior trees, HTN/GOAP planning, or a small game-trained policy.
- Having one LLM summarize state for another is lossy. Compute income trends, force ratios, threat maps, timing windows and uncertainty directly.
- Multiple language agents often make correlated mistakes. A critic LLM may agree with a plausible but illegal or badly timed plan rather than catch it.
- Strategy based only on “general trends” will miss what actually decides RTS games: prerequisites, exact production time, cash runway, travel time, power, unit readiness and scouting age.
- An asynchronous model will sometimes answer after the position has changed. Late results must be disposable.
- Without strict ownership, layers will fight: attack/retreat oscillation, duplicated spending and constantly reassigned units.
- Fog of war is easy to leak accidentally. The strategist must receive the player’s belief state, not the engine’s omniscient state.

So the distilled verdict is:

> Keep layered AI; drop layered LLMs as the default.

## Architecture I would actually build

| Layer | Cadence | Appropriate implementation | Responsibility |
|---|---:|---|---|
| State and belief model | Every tick | Deterministic | Visible entities, queues, resources, terrain, stable IDs, last-seen enemy information with age/confidence |
| Feature/event compiler | Every tick or few ticks | Deterministic | Threat maps, income forecasts, composition values, scouting freshness, map control and meaningful change detection |
| Tactical/reflex control | 5–20 Hz | Rules, utility scoring or compact learned policies | Targeting, focus fire, retreat, pathing, repair, deployment, harvesting safety |
| Operational planning | Every 0.5–2 seconds/events | Utility/HTN/constraint planner | Production, squads, objectives, reinforcements, build placement and budget reservations |
| Strategic planning | Every 10–30 seconds/major event | Optional LLM | Doctrine, economic posture, composition goals, attack windows, opponent hypotheses and contingencies |
| Validator/arbiter | Every proposed intent | Deterministic | Legality, prerequisites, resource conflicts, freshness, fog compliance and fallback |

The information flow should be:

```text
Game adapter → belief state → deterministic features/events
                                      ↓
                         optional strategic LLM
                                      ↓
                       validator and intent arbiter
                                      ↓
                operational planner → tactical controllers
                                      ↓
                                  Game
```

The LLM should never emit “select tanks and click coordinate 83,42.” Its output should be a small typed intent containing:

- Snapshot/version it reasoned from.
- Expiry time.
- Strategic posture.
- Prioritized objectives.
- Economy/army/tech budget shares.
- Desired composition ranges.
- Regions or targets of interest.
- Attack conditions.
- Abort and replan triggers.
- Confidence and assumptions.

Each unit, squad and production budget should have one controller lease at a time. Add minimum commitment periods and hysteresis so the bot cannot change its mind every update.

## What the state compiler should provide

Give the strategist compact, exact summaries with 5/15/60-second deltas:

- Income, spending, cash runway and production utilization.
- Army value by role and location—not merely unit count.
- Recent attrition and local force ratios.
- Known enemy production, tech and composition, with observation age.
- Ore remaining, expansion opportunities and map control.
- Threatened bases, likely attack paths and reinforcement times.
- Scouting coverage and important unknowns.
- Timing events: new tech spotted, miner loss, production transition, MCV loss, superweapon state or a major army-value swing.

Also supply an authoritative rules database for costs, timings, prerequisites and counters. Never ask the LLM to remember those reliably.

## Practical RA2 implementation route

1. **Chrono Divide is probably the fastest serious prototype.** Its maintained bot project describes a feature-complete browser rebuild with a bot API, headless execution, replay generation, action logs and state visualisation. That removes much of the observe/act integration work. [Chrono Divide bot repository](https://github.com/Supalosa/supalosa-chronodivide-bot)

2. **OpenRA/RA2 is attractive when engine access and reproducibility matter.** OpenRA already has modular bot components for construction, resource management and related behavior, and there is an RA2 mod repository. Its Lua scripting is sandboxed, so an external-model integration would probably belong in the C# engine or a dedicated bridge rather than ordinary map scripts. Treat it as an OpenRA ruleset, not necessarily byte-for-byte retail RA2. [OpenRA bot traits](https://docs.openra.net/en/playtest/traits/), [Lua API](https://docs.openra.net/en/release/lua/), [RA2 mod](https://github.com/OpenRA/ra2)

3. **Retail Yuri’s Revenge plus Phobos/YRpp gives maximum fidelity but maximum engineering cost.** Phobos substantially expands native AI scripting, making it useful for scripted baselines and playbooks, but a live external planner would still require a custom engine extension and state/action bridge. [Phobos AI scripting documentation](https://phobos.readthedocs.io/en/latest/AI-Scripting-and-Mapping.html)

4. **Pixel/screen control should be the last choice.** Unless vision and input automation are the research subject, perception and unreliable commands will consume the project.

Use offline or explicitly permitted private games while developing external control.

## Sensible build sequence

1. Build reliable observe, act, reset, replay and logging interfaces.
2. Produce a competent deterministic bot on one faction and a small map set.
3. Add 5–20 authored strategic playbooks and a deterministic selector.
4. Run the LLM in shadow mode: record its decisions without letting it control anything.
5. Allow it only to select and parameterize validated playbooks.
6. Expand its action vocabulary only where controlled tests show a measurable benefit.
7. If it becomes useful but expensive, distill its decisions into a compact classifier or policy and reserve the LLM for unusual states.

Compare:

- Baseline scripted selector.
- Contextual bandit over playbooks.
- Strategic LLM.
- Strategic plus “fast LLM.”
- Same systems with oracle state, solely to diagnose whether perception is the bottleneck.

Measure win rate on held-out maps and opponent styles, but also invalid plans, decision lateness, strategic churn, production idle time, resource float, trade efficiency, inference cost and hidden-information leakage.

## Why the concept still makes sense

There is research precedent for almost exactly this arrangement. SwarmBrain placed an LLM above a condition-response tactical system specifically because LLM latency was unsuitable for reflexes. HIMA later combined specialized imitation agents with a strategic meta-controller and event-triggered replanning. These are evidence that the structure is plausible, but not proof that it will produce a competitive RA2 bot: their evaluation settings are largely StarCraft II and text/built-in-AI environments. [SwarmBrain](https://arxiv.org/abs/2401.17749), [HIMA](https://arxiv.org/abs/2508.06042)

The LLM is most defensible when you want:

- Coherent strategic pivots under unfamiliar situations.
- Different personalities or recognizable play styles.
- Adaptation across mods and changing unit rosters.
- Explanations and post-game analysis.
- Selection or generation of conditional playbooks.

If the only objective is win rate in a fixed RA2 ruleset, conventional planning, imitation, evolutionary build-order search or self-play will probably be stronger and cheaper.

So your setup survives in this form:

> Deterministic systems turn game telemetry into trustworthy strategic concepts; one slow LLM chooses intent; game-native planners and controllers do all real-time execution.
