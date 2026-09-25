// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Nodes;

namespace Bindery.Ra2.Bot.Claude;

/// <summary>
/// A built prompt. <see cref="MatchContext"/> holds what is constant for the
/// match (faction, catalogue, rule facts, personality) and
/// <see cref="Situation"/> what changes per snapshot (features, active intent,
/// history); both are canonical JSON. The order system → match context →
/// situation puts every byte that repeats across calls in the cacheable prefix.
/// </summary>
public sealed record IntentPrompt(string SystemPrompt, string MatchContext, string Situation)
{
    /// <summary>User turn blocks: the match context ends at a cache breakpoint.</summary>
    public IReadOnlyList<PromptBlock> UserBlocks => [new PromptBlock(MatchContext, true), new PromptBlock(Situation, false)];
}

/// <summary>
/// Builds the strategist prompt from a <see cref="StrategistContext"/> and
/// nothing else. That is the fog invariant on the LLM side: the builder has no
/// access to belief snapshots, observations or engine state, so the prompt can
/// only contain what the feature compiler already derived from belief. Output is
/// deterministic (sorted keys, rounded numbers, sorted collections) so identical
/// contexts give identical bytes.
/// </summary>
public sealed class IntentPromptBuilder
{
    /// <summary>Top-level keys of <see cref="IntentPrompt.MatchContext"/>.</summary>
    public static readonly IReadOnlyList<string> MatchContextKeys = ["catalogue", "faction", "personality", "ruleFacts"];

    /// <summary>Top-level keys of <see cref="IntentPrompt.Situation"/>.</summary>
    public static readonly IReadOnlyList<string> SituationKeys = ["activeIntent", "features", "history"];

    private const string CommonSystemPrompt =
        """
        You are the strategic chief of staff for a Red Alert 2 / Yuri's Revenge skirmish bot. A deterministic operational planner and tactical controllers execute whatever you decide; you never control units, buildings, cells or clicks. Your only output is one strategic intent as JSON matching the provided schema, and nothing else.

        What you may decide:
        - Choose exactly one playbook from the catalogue in the match context (by its id) and set its parameters. Only parameter names the playbook declares are meaningful; stay within each parameter's [min, max]. Omitted parameters take the playbook default.
        - Set posture, objectives, budget shares, army composition targets, regions of interest, attack conditions, abort triggers, replan triggers, an expiry, a confidence, your assumptions and a short rationale.

        How to reason:
        - Base every claim on the features given. Do not assume enemy units, tech or positions that the features do not show. Enemy estimates carry evidence ages (seconds) and confidence in [0, 1]; old or low-confidence evidence is uncertainty, not fact. When scouting is stale or coverage is low, prefer plans that stay safe under that uncertainty, or add a Scout objective, and say so in assumptions.
        - Rule facts (costs, build seconds, prerequisite paths from an empty base) are authoritative; use them for timing and affordability rather than memory of the game.
        - Respect commitment: the active intent has a minimum commitment window (minCommitRemainingSeconds). Replacing it inside that window is only accepted when one of its abort triggers has fired or the base is under serious threat, so otherwise propose the same playbook with adjusted parameters.
        - Changing posture needs clearly higher confidence than the incumbent's. Frequent switching loses games.

        Field conventions:
        - Region ids are integers from the features; regionId is null when a field does not need a region. LocalForceRatio and ScoutingAgeSeconds conditions require a regionId.
        - Objective priority: lower number is more important. TechTo objectives name a typeId from the rule facts; other objectives use typeId null.
        - Budget shares (economy, army, tech, defense) are non-negative and sum to 1.
        - Composition shares are fractions of army value per role, 0 <= minShare <= maxShare <= 1.
        - Conditions compare a metric with a threshold (Lt, Le, Gt, Ge). Attack conditions must all hold before an attack; any abort trigger ends the intent early; replan triggers ask for a new decision.
        - expiresInSeconds is how long the intent stays valid (typically 60–240). confidence is in [0, 1].
        - Trends are {now, d5s, d15s, d60s}: the current value and its change over the last 5, 15 and 60 game seconds. Times and ages are game seconds. null means unknown or unbounded.

        Input: the first user block is the match context (constant for the match: faction, playbook catalogue, rule facts, personality). The second is the current situation (features, active intent, recent history). Personality is style guidance only; it never overrides these rules.
        """;

    private const string RefineSystemPrompt =
        """

        Mode: REFINE. You may not change the plan, only tune it. Return the active intent's playbookId unchanged and choose new values for its parameters; any other playbookId is rejected. Posture, objectives, budget, composition and conditions are taken from the active intent regardless of what you return, so copy them unchanged.
        """;

    private readonly int historyLimit;
    private readonly int eventLimit;
    private readonly int unitsPerRole;

    /// <param name="historyLimit">Most recent intent history entries included.</param>
    /// <param name="eventLimit">Most recent strategic events included.</param>
    /// <param name="unitsPerRole">Cheapest buildable units per role listed in rule facts; bounds prompt size with large imported rulesets.</param>
    public IntentPromptBuilder(int historyLimit = 8, int eventLimit = 12, int unitsPerRole = 4)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(historyLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(eventLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(unitsPerRole);
        this.historyLimit = historyLimit;
        this.eventLimit = eventLimit;
        this.unitsPerRole = unitsPerRole;
    }

    /// <summary>The byte-stable system prompt for a mode. It never contains per-call data.</summary>
    public static string SystemPrompt(StrategistMode mode) =>
        mode == StrategistMode.Refine ? CommonSystemPrompt + RefineSystemPrompt : CommonSystemPrompt;

    /// <param name="fallbackPersonality">Used when the context carries no personality of its own.</param>
    public IntentPrompt Build(StrategistContext context, StrategistMode mode, string? fallbackPersonality = null)
    {
        JsonObject match = new()
        {
            ["catalogue"] = Catalogue(context),
            ["faction"] = context.Features.Faction.ToString(),
            ["personality"] = context.Personality ?? fallbackPersonality,
            ["ruleFacts"] = RuleFacts(context),
        };
        JsonObject situation = new()
        {
            ["activeIntent"] = ActiveIntent(context),
            ["features"] = Features(context.Features),
            ["history"] = History(context.History),
        };
        return new IntentPrompt(SystemPrompt(mode), CanonicalJson.Serialize(match), CanonicalJson.Serialize(situation));
    }

    private static IReadOnlyList<Playbook> FactionPlaybooks(StrategistContext context) =>
        context.Playbooks.For(context.Features.Faction).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();

    private static JsonArray Catalogue(StrategistContext context)
    {
        JsonArray catalogue = new();
        foreach (Playbook playbook in FactionPlaybooks(context))
        {
            JsonArray parameters = new();
            foreach (PlaybookParameter p in playbook.Parameters.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                parameters.Add(new JsonObject
                {
                    ["name"] = p.Name,
                    ["min"] = CanonicalJson.Number(p.Min),
                    ["max"] = CanonicalJson.Number(p.Max),
                    ["default"] = CanonicalJson.Number(p.Default),
                    ["description"] = p.Description,
                });
            }
            catalogue.Add(new JsonObject
            {
                ["id"] = playbook.Id,
                ["description"] = playbook.Description,
                ["posture"] = playbook.Posture.ToString(),
                ["minCommitSeconds"] = CanonicalJson.Number(playbook.MinCommitSeconds),
                ["budget"] = Budget(playbook.Budget),
                ["composition"] = Composition(playbook.Composition),
                ["techGoals"] = Strings(playbook.TechGoals),
                ["attackConditions"] = Conditions(playbook.AttackConditions),
                ["abortTriggers"] = Conditions(playbook.AbortTriggers),
                ["parameters"] = parameters,
            });
        }
        return catalogue;
    }

    /// <summary>
    /// Rule facts the model needs for timing: every tech goal of the faction's
    /// playbooks with its prerequisite path, and the cheapest units per role.
    /// Paths are computed from an empty base because <see cref="StrategistContext"/>
    /// does not carry the player's owned buildings; that makes this block
    /// constant for the match and therefore cacheable.
    /// </summary>
    private JsonObject RuleFacts(StrategistContext context)
    {
        Faction faction = context.Features.Faction;
        IRulesDatabase rules = context.Rules;
        HashSet<string> empty = new(StringComparer.Ordinal);

        JsonArray techGoals = new();
        foreach (string goal in FactionPlaybooks(context).SelectMany(p => p.TechGoals).Distinct(StringComparer.Ordinal).OrderBy(g => g, StringComparer.Ordinal))
        {
            if (!rules.TryGet(goal, out UnitRule rule))
            {
                techGoals.Add(new JsonObject { ["typeId"] = goal, ["known"] = false });
                continue;
            }
            IReadOnlyList<string>? path = rules.PathTo(faction, empty, goal);
            double? pathCost = path?.Sum(t => rules.TryGet(t, out UnitRule r) ? r.Cost : 0) + rule.Cost;
            double? pathSeconds = path?.Sum(t => rules.TryGet(t, out UnitRule r) ? r.BuildSeconds : 0) + rule.BuildSeconds;
            techGoals.Add(new JsonObject
            {
                ["typeId"] = rule.TypeId,
                ["name"] = rule.Name,
                ["known"] = true,
                ["cost"] = rule.Cost,
                ["buildSeconds"] = CanonicalJson.Number(rule.BuildSeconds),
                ["power"] = rule.Power,
                ["reachable"] = path is not null,
                ["prerequisitePath"] = path is null ? null : Strings(path),
                ["pathLength"] = path?.Count,
                ["totalCostFromEmptyBase"] = pathCost is { } c ? CanonicalJson.Number(c) : null,
                ["serialBuildSecondsFromEmptyBase"] = pathSeconds is { } s ? CanonicalJson.Number(s) : null,
            });
        }

        JsonArray units = new();
        IEnumerable<IGrouping<UnitRole, UnitRule>> byRole = rules.All
            .Where(r => r.Kind != EntityKind.Building && r.Factions.Contains(faction))
            .GroupBy(r => r.Role)
            .OrderBy(g => g.Key);
        foreach (IGrouping<UnitRole, UnitRule> group in byRole)
        {
            foreach (UnitRule rule in group.OrderBy(r => r.Cost).ThenBy(r => r.TypeId, StringComparer.Ordinal).Take(unitsPerRole))
            {
                IReadOnlyList<string>? path = rules.PathTo(faction, empty, rule.TypeId);
                units.Add(new JsonObject
                {
                    ["typeId"] = rule.TypeId,
                    ["name"] = rule.Name,
                    ["role"] = rule.Role.ToString(),
                    ["kind"] = rule.Kind.ToString(),
                    ["cost"] = rule.Cost,
                    ["buildSeconds"] = CanonicalJson.Number(rule.BuildSeconds),
                    ["strength"] = rule.Strength,
                    ["weapon"] = rule.Weapon.ToString(),
                    ["range"] = CanonicalJson.Number(rule.Range),
                    ["speed"] = CanonicalJson.Number(rule.Speed),
                    ["antiAir"] = rule.AntiAir,
                    ["pathLength"] = path?.Count,
                });
            }
        }

        return new JsonObject
        {
            ["rulesetId"] = rules.RulesetId,
            ["techGoals"] = techGoals,
            ["units"] = units,
        };
    }

    private static JsonObject? ActiveIntent(StrategistContext context)
    {
        if (context.ActiveIntent is not { } intent)
        {
            return null;
        }
        GameTime now = context.Features.Time;
        IntentHistoryEntry? entry = context.History.FirstOrDefault(h => h.IntentId == intent.IntentId && h.EndedAt is null);
        GameTime acceptedAt = entry?.AcceptedAt ?? intent.IssuedAt;
        double active = Math.Max(0, now.SecondsSince(acceptedAt));
        double? minCommit = context.Playbooks.TryGet(intent.PlaybookId, out Playbook playbook) ? playbook.MinCommitSeconds : null;

        JsonObject parameters = new();
        foreach (KeyValuePair<string, double> p in intent.PlaybookParameters)
        {
            parameters[p.Key] = CanonicalJson.Number(p.Value);
        }

        return new JsonObject
        {
            ["intentId"] = intent.IntentId,
            ["source"] = intent.Source.ToString(),
            ["playbookId"] = intent.PlaybookId,
            ["posture"] = intent.Posture.ToString(),
            ["parameters"] = parameters,
            ["objectives"] = Objectives(intent.Objectives),
            ["budget"] = Budget(intent.Budget),
            ["composition"] = Composition(intent.Composition),
            ["regionsOfInterest"] = Regions(intent.RegionsOfInterest),
            ["attackConditions"] = Conditions(intent.AttackConditions),
            ["abortTriggers"] = Conditions(intent.AbortTriggers),
            ["replanTriggers"] = Conditions(intent.ReplanTriggers),
            ["confidence"] = CanonicalJson.Number(intent.Confidence),
            ["assumptions"] = Strings(intent.Assumptions),
            ["secondsActive"] = CanonicalJson.Number(active),
            ["minCommitRemainingSeconds"] = minCommit is { } m ? CanonicalJson.Number(Math.Max(0, m - active)) : null,
            ["expiresInSeconds"] = CanonicalJson.Number(Math.Max(0, intent.ExpiresAt.SecondsSince(now))),
        };
    }

    private JsonArray History(IReadOnlyList<IntentHistoryEntry> history)
    {
        JsonArray result = new();
        IEnumerable<IntentHistoryEntry> recent = history
            .OrderBy(h => h.AcceptedAt.Frame)
            .ThenBy(h => h.IntentId, StringComparer.Ordinal)
            .TakeLast(historyLimit);
        foreach (IntentHistoryEntry h in recent)
        {
            result.Add(new JsonObject
            {
                ["playbookId"] = h.PlaybookId,
                ["posture"] = h.Posture.ToString(),
                ["source"] = h.Source.ToString(),
                ["acceptedAtSeconds"] = CanonicalJson.Number(h.AcceptedAt.Seconds),
                ["endedAtSeconds"] = h.EndedAt is { } e ? CanonicalJson.Number(e.Seconds) : null,
                ["endReason"] = h.EndReason,
            });
        }
        return result;
    }

    private JsonObject Features(StrategicFeatures f)
    {
        EconomyFeatures e = f.Economy;
        ArmyFeatures a = f.Army;
        EnemyFeatures en = f.Enemy;

        JsonArray clusters = new();
        foreach (ForceCluster c in a.Clusters.OrderBy(c => c.Region.Value).ThenByDescending(c => c.Value).ThenBy(c => c.Units))
        {
            clusters.Add(new JsonObject
            {
                ["region"] = c.Region.Value,
                ["units"] = c.Units,
                ["value"] = CanonicalJson.Number(c.Value),
                ["averageHealth"] = CanonicalJson.Number(c.AverageHealth),
            });
        }

        JsonArray control = new();
        foreach (KeyValuePair<RegionId, RegionControl> c in f.MapControl.Control.OrderBy(c => c.Key.Value))
        {
            control.Add(new JsonObject { ["region"] = c.Key.Value, ["control"] = c.Value.ToString() });
        }

        JsonArray regionAges = new();
        foreach (KeyValuePair<RegionId, double> r in f.Scouting.RegionAgeSeconds.OrderBy(r => r.Key.Value))
        {
            regionAges.Add(new JsonObject { ["region"] = r.Key.Value, ["ageSeconds"] = CanonicalJson.Number(r.Value) });
        }

        JsonArray threats = new();
        foreach (ThreatAssessment t in f.Threats.OrderBy(t => t.Region.Value).ThenByDescending(t => t.EnemyValue))
        {
            threats.Add(new JsonObject
            {
                ["region"] = t.Region.Value,
                ["isBase"] = t.IsBase,
                ["enemyValue"] = CanonicalJson.Number(t.EnemyValue),
                ["ownValue"] = CanonicalJson.Number(t.OwnValue),
                ["localForceRatio"] = CanonicalJson.Number(t.LocalForceRatio),
                ["enemyEtaSeconds"] = CanonicalJson.Number(t.EnemyEtaSeconds),
                ["reinforcementSeconds"] = CanonicalJson.Number(t.ReinforcementSeconds),
                ["confidence"] = CanonicalJson.Number(t.Confidence),
            });
        }

        JsonArray events = new();
        IEnumerable<StrategicEvent> recentEvents = f.Events
            .OrderBy(ev => ev.Time.Frame)
            .ThenBy(ev => ev.Kind)
            .ThenBy(ev => ev.Detail, StringComparer.Ordinal)
            .TakeLast(eventLimit);
        foreach (StrategicEvent ev in recentEvents)
        {
            events.Add(new JsonObject
            {
                ["kind"] = ev.Kind.ToString(),
                ["ageSeconds"] = CanonicalJson.Number(Math.Max(0, f.Time.SecondsSince(ev.Time))),
                ["severity"] = CanonicalJson.Number(ev.Severity),
                ["detail"] = ev.Detail,
                ["region"] = ev.Region?.Value,
            });
        }

        return new JsonObject
        {
            ["gameSeconds"] = CanonicalJson.Number(f.Time.Seconds),
            ["observationMode"] = f.Mode.ToString(),
            ["economy"] = new JsonObject
            {
                ["credits"] = Trend(e.Credits),
                ["incomePerMinute"] = Trend(e.IncomePerMinute),
                ["spendingPerMinute"] = Trend(e.SpendingPerMinute),
                ["cashRunwaySeconds"] = CanonicalJson.Number(e.CashRunwaySeconds),
                ["productionUtilization"] = CanonicalJson.Number(e.ProductionUtilization),
                ["harvesters"] = e.Harvesters,
                ["refineries"] = e.Refineries,
                ["oreRemainingFraction"] = CanonicalJson.Number(e.OreRemainingFraction),
                ["power"] = new JsonObject
                {
                    ["produced"] = e.Power.Produced,
                    ["drained"] = e.Power.Drained,
                    ["lowPower"] = e.Power.LowPower,
                },
            },
            ["army"] = new JsonObject
            {
                ["armyValue"] = Trend(a.ArmyValue),
                ["valueByRole"] = RoleMap(a.ValueByRole),
                ["clusters"] = clusters,
                ["lossesValue"] = Trend(a.LossesValue),
                ["killsValue"] = Trend(a.KillsValue),
            },
            ["enemy"] = new JsonObject
            {
                ["estimatedArmyValue"] = Trend(en.EstimatedArmyValue),
                ["armyValueConfidence"] = CanonicalJson.Number(en.ArmyValueConfidence),
                ["compositionByRole"] = RoleMap(en.CompositionByRole),
                ["knownTech"] = Strings(en.KnownTech.OrderBy(t => t, StringComparer.Ordinal)),
                ["knownProduction"] = Strings(en.KnownProduction.OrderBy(t => t, StringComparer.Ordinal)),
                ["newestObservationAgeSeconds"] = CanonicalJson.Number(en.NewestObservationAgeSeconds),
                ["medianObservationAgeSeconds"] = CanonicalJson.Number(en.MedianObservationAgeSeconds),
                ["superweaponKnown"] = en.SuperweaponKnown,
            },
            ["mapControl"] = new JsonObject
            {
                ["control"] = control,
                ["expansionCandidates"] = Regions(f.MapControl.ExpansionCandidates),
                ["ownedOreFraction"] = CanonicalJson.Number(f.MapControl.OwnedOreFraction),
            },
            ["scouting"] = new JsonObject
            {
                ["coverageFraction"] = CanonicalJson.Number(f.Scouting.CoverageFraction),
                ["regionAgeSeconds"] = regionAges,
                ["importantUnknowns"] = Strings(f.Scouting.ImportantUnknowns),
            },
            ["threats"] = threats,
            ["events"] = events,
        };
    }

    private static JsonObject Trend(Trend t) => new()
    {
        ["now"] = CanonicalJson.Number(t.Current),
        ["d5s"] = CanonicalJson.Number(t.Delta5s),
        ["d15s"] = CanonicalJson.Number(t.Delta15s),
        ["d60s"] = CanonicalJson.Number(t.Delta60s),
    };

    private static JsonObject RoleMap(IReadOnlyDictionary<UnitRole, double> map)
    {
        JsonObject result = new();
        foreach (KeyValuePair<UnitRole, double> entry in map.OrderBy(e => e.Key))
        {
            result[entry.Key.ToString()] = CanonicalJson.Number(entry.Value);
        }
        return result;
    }

    private static JsonObject Budget(BudgetShares b) => new()
    {
        ["economy"] = CanonicalJson.Number(b.Economy),
        ["army"] = CanonicalJson.Number(b.Army),
        ["tech"] = CanonicalJson.Number(b.Tech),
        ["defense"] = CanonicalJson.Number(b.Defense),
    };

    private static JsonArray Composition(IReadOnlyList<CompositionTarget> targets)
    {
        JsonArray result = new();
        foreach (CompositionTarget t in targets)
        {
            result.Add(new JsonObject
            {
                ["role"] = t.Role.ToString(),
                ["minShare"] = CanonicalJson.Number(t.MinShare),
                ["maxShare"] = CanonicalJson.Number(t.MaxShare),
            });
        }
        return result;
    }

    private static JsonArray Objectives(IReadOnlyList<Objective> objectives)
    {
        JsonArray result = new();
        foreach (Objective o in objectives)
        {
            result.Add(new JsonObject
            {
                ["kind"] = o.Kind.ToString(),
                ["regionId"] = o.Region?.Value,
                ["typeId"] = o.TypeId,
                ["priority"] = o.Priority,
            });
        }
        return result;
    }

    private static JsonArray Conditions(IReadOnlyList<Condition> conditions)
    {
        JsonArray result = new();
        foreach (Condition c in conditions)
        {
            result.Add(new JsonObject
            {
                ["metric"] = c.Metric.ToString(),
                ["op"] = c.Op.ToString(),
                ["threshold"] = CanonicalJson.Number(c.Threshold),
                ["regionId"] = c.Region?.Value,
            });
        }
        return result;
    }

    private static JsonArray Regions(IEnumerable<RegionId> regions)
    {
        JsonArray result = new();
        foreach (RegionId r in regions)
        {
            result.Add(r.Value);
        }
        return result;
    }

    private static JsonArray Strings(IEnumerable<string> values)
    {
        JsonArray result = new();
        foreach (string v in values)
        {
            result.Add(v);
        }
        return result;
    }
}
