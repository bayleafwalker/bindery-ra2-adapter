// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bindery.Ra2.Bot.Claude;

namespace Bindery.Ra2.Bot.Arena;

/// <summary>
/// The <c>--llm-fake</c> client: a deterministic stand-in for the Anthropic API that
/// reads the same prompt a real model would get and answers with a schema-valid
/// <see cref="IntentDraft"/> derived from it. It exercises the whole LLM pipeline
/// (prompt building, structured-output parsing, mapping, vocabulary tiers, validation,
/// latency and arbitration) without a credential. Results produced with it are labelled
/// <c>llm-fake</c>; they measure the pipeline and a scripted policy, not a model.
/// </summary>
/// <remarks>
/// <para>The policy (<see cref="PolicyId"/>) is deliberately not the deterministic selector's, so an arena run can
/// tell the two apart; it reads the prompt's JSON only (features, counters, catalogue, personality):</para>
/// <list type="number">
/// <item>Defend (<c>generic-defend</c>) at a base threat of 1.2 or more (the selector waits for 1.3).</item>
/// <item>Enemy aircraft among the seen types (from the counters table): the faction's anti-air answer
/// (<c>allied-harass</c> rocketeers, <c>soviet-flak-mix</c>).</item>
/// <item>Before <see cref="EarlyPushSeconds"/>: the faction's armour timing (<c>allied-grizzly-timing</c> /
/// <c>soviet-rhino-rush</c>), where the selector opens with the mixed army.</item>
/// <item>Afterwards: armour while ahead (own army at least 1.3 times the estimate), the faction's tech playbook with
/// two refineries and an even army, else the mixed army.</item>
/// </list>
/// <para>A known personality shifts the choice: aggressive keeps the armour timing, turtle and tech take their
/// playbooks once safe, harasser takes the harass / siege playbook. Parameters are set from the situation (attack
/// army value at 1.1 times the enemy estimate plus 300, within the declared range; shorter harass intervals; a
/// wider siege buffer), the counters table's best role gets at least a 30% composition share, regions of interest
/// are the first expansion candidate and the known enemy region, and an attack aborts on a 1500-value loss in
/// 15 s. The vocabulary tier of the strategist decides which of these fields survive.</para>
/// <para>In Refine mode it keeps the active playbook and moves each numeric parameter 10% toward aggression when
/// ahead and toward caution when behind, within the catalogue's declared range. Token usage is estimated as
/// characters / 4 so the cost columns are exercised; it is not a measurement.</para>
/// </remarks>
public sealed class FakeMessageClient : IMessageClient
{
    public const string PolicyId = "fake-counter-v2";

    public const double EarlyPushSeconds = 300;

    public int Calls { get; private set; }

    /// <summary>Called with every request before it is answered.</summary>
    public Action<ModelRequest>? RequestObserver { get; init; }

    public Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        RequestObserver?.Invoke(request);
        if (string.Equals(request.SystemPrompt, PostGameNarrator.SystemPrompt, StringComparison.Ordinal)) return Task.FromResult(Narrate(request));
        JsonNode match = JsonNode.Parse(request.UserContent[0].Text)!;
        JsonNode situation = JsonNode.Parse(request.UserContent[^1].Text)!;
        bool refine = request.SystemPrompt.Contains("Mode: REFINE", StringComparison.Ordinal);

        IntentDraft draft = refine ? Refine(match, situation) : Strategic(match, situation);
        string text = JsonSerializer.Serialize(draft, IntentDraftSchema.ParseOptions);
        long input = (request.SystemPrompt.Length + request.UserContent.Sum(static b => b.Text.Length)) / 4;
        ModelUsage usage = new(input, text.Length / 4, 0, 0);
        return Task.FromResult(new ModelReply(text, "end_turn", null, usage, request.Model));
    }

    /// <summary>A scripted narrative for <see cref="PostGameNarrator"/> requests: facts from the report JSON, in a fixed sentence pattern.</summary>
    private static ModelReply Narrate(ModelRequest request)
    {
        JsonNode report = JsonNode.Parse(request.UserContent[^1].Text)!;
        JsonArray timeline = report["timeline"]?.AsArray() ?? [];
        JsonArray pivots = report["pivots"]?.AsArray() ?? [];
        JsonNode? first = timeline.FirstOrDefault();
        string opening = first is null
            ? "No intent took effect."
            : $"It opened with {first["playbookId"]} ({first["posture"]}) because: {first["rationale"] ?? "no rationale recorded"}.";
        string turns = pivots.Count == 0
            ? "It never changed playbook or posture."
            : string.Create(CultureInfo.InvariantCulture, $"It changed course {pivots.Count} times, first at {Number(pivots[0]!["atSeconds"]):0} s to {pivots[0]!["toPlaybook"]} (trigger {pivots[0]!["trigger"] ?? "none recorded"}).");
        double? agreement = report["shadow"]?["compared"] is JsonNode c && Number(c) > 0 ? Number(report["shadow"]!["agreed"]) / Number(c) : null;
        string shadow = agreement is { } a ? string.Create(CultureInfo.InvariantCulture, $" The shadow strategist agreed on the playbook {a:P0} of the time.") : string.Empty;
        string text = string.Create(CultureInfo.InvariantCulture, $"Fake client narrative, scripted from the report, not a model. {timeline.Count} intents took effect over {Number(report["durationSeconds"]):0} s. {opening} {turns} {report["rejected"]?.AsArray().Count ?? 0} proposals had no effect.{shadow} Result: {report["result"] ?? "not in the log"}.");
        string reply = JsonSerializer.Serialize(new { narrative = text });
        return new ModelReply(reply, "end_turn", null, new ModelUsage(request.UserContent.Sum(static b => b.Text.Length) / 4, reply.Length / 4, 0, 0), request.Model);
    }

    private static string? Personality(JsonNode match) => match["personality"] switch
    {
        JsonObject o => o["id"]?.GetValue<string>(),
        JsonValue v when v.TryGetValue(out string? s) => s,
        _ => null,
    };

    private static IntentDraft Strategic(JsonNode match, JsonNode situation)
    {
        string faction = match["faction"]!.GetValue<string>();
        bool allied = faction == nameof(Faction.Allied);
        string? personality = Personality(match);
        JsonNode features = situation["features"]!;
        double seconds = Number(features["gameSeconds"]);
        double own = Number(features["army"]?["armyValue"]?["now"]);
        double enemy = Number(features["enemy"]?["estimatedArmyValue"]?["now"]);
        double refineries = Number(features["economy"]?["refineries"]);
        JsonArray counters = situation["counters"]?.AsArray() ?? [];

        double threat = 0;
        int? baseRegion = null;
        foreach (JsonNode? t in features["threats"]?.AsArray() ?? [])
        {
            if (t is null || t["isBase"]?.GetValue<bool>() != true) continue;
            baseRegion ??= t["region"]!.GetValue<int>();
            double enemyValue = Number(t["enemyValue"]), ownValue = Number(t["ownValue"]);
            double ratio = enemyValue <= 0 ? 0 : ownValue <= 0 ? 10 : enemyValue / ownValue;
            if (ratio > threat)
            {
                threat = ratio;
                baseRegion = t["region"]!.GetValue<int>();
            }
        }
        int? enemyRegion = (features["mapControl"]?["control"]?.AsArray() ?? [])
            .Where(static c => c?["control"]?.GetValue<string>() == nameof(RegionControl.Enemy))
            .Select(static c => (int?)c!["region"]!.GetValue<int>())
            .OrderBy(static r => r)
            .FirstOrDefault();
        int? expansion = (features["mapControl"]?["expansionCandidates"]?.AsArray() ?? [])
            .Select(static r => (int?)r!.GetValue<int>())
            .FirstOrDefault();
        bool enemyAir = counters.Any(static c => c?["enemyKind"]?.GetValue<string>() == nameof(EntityKind.Aircraft));

        string armour = allied ? "allied-grizzly-timing" : "soviet-rhino-rush";
        string mixed = allied ? "allied-ifv-mix" : "soviet-flak-mix";
        string tech = allied ? "allied-boom" : "soviet-apoc-tech";
        string turtle = allied ? "allied-prism-turtle" : "soviet-turtle";
        string harass = allied ? "allied-harass" : "soviet-v3-siege";
        string antiAir = allied ? "allied-harass" : "soviet-flak-mix";

        string playbookId;
        string rationale;
        if (threat >= 1.2)
        {
            playbookId = "generic-defend";
            rationale = string.Create(CultureInfo.InvariantCulture, $"Base threat ratio {threat:0.00}: defend before it grows.");
        }
        else if (enemyAir)
        {
            playbookId = antiAir;
            rationale = "Enemy aircraft seen (counters table): anti-air answer.";
        }
        else if (personality == "turtle" && seconds >= 60)
        {
            playbookId = turtle;
            rationale = "Turtle personality: defences first, attack only with a clear lead.";
        }
        else if (personality == "harasser" && seconds >= 90)
        {
            playbookId = harass;
            rationale = "Harasser personality: raid economy, avoid the main army.";
        }
        else if (seconds < EarlyPushSeconds || personality == "aggressive" && seconds < 2 * EarlyPushSeconds)
        {
            playbookId = armour;
            rationale = string.Create(CultureInfo.InvariantCulture, $"Early armour timing before the enemy techs (t={seconds:0} s).");
        }
        else if (enemy > 0 && own >= 1.3 * enemy)
        {
            playbookId = armour;
            rationale = string.Create(CultureInfo.InvariantCulture, $"Army {own:0} vs estimated {enemy:0}: press the advantage with armour.");
        }
        else if ((refineries >= 2 && (enemy <= 0 || own >= 0.8 * enemy)) || personality == "tech")
        {
            playbookId = tech;
            rationale = string.Create(CultureInfo.InvariantCulture, $"Two refineries and an even army ({own:0} vs {enemy:0}): tech up.");
        }
        else
        {
            playbookId = mixed;
            rationale = "Behind or even without the economy for tech: mixed army.";
        }

        JsonNode playbook = Catalogue(match, playbookId);
        string posture = playbook["posture"]!.GetValue<string>();
        List<DraftObjective> objectives = [];
        if (baseRegion is { } home)
        {
            objectives.Add(new DraftObjective { Kind = nameof(ObjectiveKind.DefendRegion), RegionId = home, TypeId = null, Priority = threat >= 1 ? 1 : 5 });
        }
        bool aggressive = posture is nameof(StrategicPosture.Pressure) or nameof(StrategicPosture.AllIn) or nameof(StrategicPosture.Harass);
        if (aggressive && enemyRegion is { } target)
        {
            string kind = posture == nameof(StrategicPosture.Harass) ? nameof(ObjectiveKind.Harass) : nameof(ObjectiveKind.AttackRegion);
            objectives.Add(new DraftObjective { Kind = kind, RegionId = target, TypeId = null, Priority = 2 });
        }
        else
        {
            objectives.Add(new DraftObjective { Kind = nameof(ObjectiveKind.Scout), RegionId = null, TypeId = null, Priority = 3 });
        }
        if (posture == nameof(StrategicPosture.Expand) && expansion is { } site)
        {
            objectives.Add(new DraftObjective { Kind = nameof(ObjectiveKind.Expand), RegionId = site, TypeId = null, Priority = 3 });
        }

        List<DraftParameter> parameters = [];
        foreach (JsonNode? p in playbook["parameters"]?.AsArray() ?? [])
        {
            if (p is null) continue;
            string name = p["name"]!.GetValue<string>();
            double min = Number(p["min"]), max = Number(p["max"]), value = Number(p["default"]);
            value = name switch
            {
                "attackArmyValue" => 1.1 * enemy + 300,
                "harvesterTarget" => refineries >= 2 ? 5 : 4,
                "expandAtSeconds" => min + (max - min) * 0.25,
                "harassIntervalSeconds" => min + (max - min) * 0.25,
                "siegeRangeBufferCells" => max - 1,
                "defendThreatRatio" => 0.9,
                _ => value,
            };
            parameters.Add(new DraftParameter { Name = name, Value = Math.Round(Math.Clamp(value, min, max), 3) });
        }

        List<DraftComposition> composition = Composition(playbook, counters);
        List<DraftCondition> attack = Conditions(playbook["attackConditions"]);
        double attackValue = parameters.FirstOrDefault(static p => p.Name == "attackArmyValue")?.Value ?? 0;
        if (attackValue > 0)
        {
            attack = [.. attack.Select(c => c.Metric == nameof(ConditionMetric.OwnArmyValue) ? new DraftCondition { Metric = c.Metric, Op = c.Op, Threshold = attackValue, RegionId = c.RegionId } : c)];
        }
        List<DraftCondition> abort = Conditions(playbook["abortTriggers"]);
        abort.Add(new DraftCondition { Metric = nameof(ConditionMetric.LossesValue15s), Op = nameof(Comparison.Ge), Threshold = 1500, RegionId = null });

        List<int> interest = [];
        if (expansion is { } e) interest.Add(e);
        if (enemyRegion is { } r && !interest.Contains(r)) interest.Add(r);

        DraftBudget budget = Budget(playbook["budget"]!);
        if (refineries < 2 && seconds < EarlyPushSeconds && budget.Army >= 0.1)
        {
            budget = new DraftBudget { Economy = budget.Economy + 0.1, Army = budget.Army - 0.1, Tech = budget.Tech, Defense = budget.Defense };
        }

        return new IntentDraft
        {
            PlaybookId = playbookId,
            Posture = posture,
            Parameters = parameters,
            Objectives = objectives,
            Budget = budget,
            Composition = composition,
            RegionsOfInterest = interest,
            AttackConditions = attack,
            AbortTriggers = abort,
            ReplanTriggers = [],
            ExpiresInSeconds = 90,
            Confidence = 0.7,
            Assumptions = [$"fake client ({PolicyId}): policy derived from the prompt features"],
            Rationale = rationale,
        };
    }

    /// <summary>The playbook's composition with the counters table's best role (against the most-seen enemy type) raised to at least a 30% minimum share.</summary>
    private static List<DraftComposition> Composition(JsonNode playbook, JsonArray counters)
    {
        List<DraftComposition> composition = (playbook["composition"]?.AsArray() ?? [])
            .Select(static c => new DraftComposition { Role = c!["role"]!.GetValue<string>(), MinShare = Number(c["minShare"]), MaxShare = Number(c["maxShare"]) })
            .ToList();
        string? counterRole = counters
            .Where(static c => c?["best"]?.AsArray().Count > 0)
            .Select(static c => c!["best"]![0]!["role"]?.GetValue<string>())
            .FirstOrDefault(static r => r is not null);
        if (counterRole is null) return composition;
        int index = composition.FindIndex(c => c.Role == counterRole);
        if (index >= 0)
        {
            DraftComposition c = composition[index];
            composition[index] = new DraftComposition { Role = c.Role, MinShare = Math.Max(c.MinShare, 0.3), MaxShare = Math.Max(c.MaxShare, 0.3) };
        }
        else
        {
            composition.Add(new DraftComposition { Role = counterRole, MinShare = 0.3, MaxShare = 0.6 });
        }
        double total = composition.Sum(static c => c.MinShare);
        if (total > 1)
        {
            composition = [.. composition.Select(c => new DraftComposition { Role = c.Role, MinShare = Math.Round(c.MinShare / total, 3), MaxShare = c.MaxShare })];
        }
        return composition;
    }

    private static IntentDraft Refine(JsonNode match, JsonNode situation)
    {
        JsonNode active = situation["activeIntent"] ?? throw new InvalidOperationException("Refine prompt without an active intent.");
        string playbookId = active["playbookId"]!.GetValue<string>();
        JsonNode playbook = Catalogue(match, playbookId);
        JsonNode features = situation["features"]!;
        double own = Number(features["army"]?["armyValue"]?["now"]);
        double enemy = Number(features["enemy"]?["estimatedArmyValue"]?["now"]);
        bool ahead = enemy <= 0 || own >= enemy;

        Dictionary<string, double> current = new(StringComparer.Ordinal);
        if (active["parameters"] is JsonObject activeParameters)
        {
            foreach (KeyValuePair<string, JsonNode?> p in activeParameters) current[p.Key] = Number(p.Value);
        }
        List<DraftParameter> parameters = [];
        foreach (JsonNode? p in playbook["parameters"]?.AsArray() ?? [])
        {
            if (p is null) continue;
            string name = p["name"]!.GetValue<string>();
            double min = Number(p["min"]), max = Number(p["max"]);
            double value = current.TryGetValue(name, out double v) ? v : Number(p["default"]);
            value = Math.Clamp(Math.Round(value * (ahead ? 0.9 : 1.1), 3), min, max);
            parameters.Add(new DraftParameter { Name = name, Value = value });
        }
        List<DraftObjective> objectives = (active["objectives"]?.AsArray() ?? [])
            .Where(static o => o is not null)
            .Select(static o => new DraftObjective
            {
                Kind = o!["kind"]!.GetValue<string>(),
                RegionId = o["regionId"]?.GetValue<int>(),
                TypeId = o["typeId"]?.GetValue<string>(),
                Priority = o["priority"]!.GetValue<int>(),
            })
            .ToList();
        string posture = active["posture"]!.GetValue<string>();
        return new IntentDraft
        {
            PlaybookId = playbookId,
            Posture = posture,
            Parameters = parameters,
            Objectives = objectives,
            Budget = Budget(playbook["budget"]!),
            Composition = (playbook["composition"]?.AsArray() ?? [])
                .Select(static c => new DraftComposition { Role = c!["role"]!.GetValue<string>(), MinShare = Number(c["minShare"]), MaxShare = Number(c["maxShare"]) })
                .ToList(),
            RegionsOfInterest = [],
            AttackConditions = Conditions(playbook["attackConditions"]),
            AbortTriggers = Conditions(playbook["abortTriggers"]),
            ReplanTriggers = [],
            ExpiresInSeconds = 60,
            Confidence = 0.6,
            Assumptions = [$"fake client ({PolicyId}): refine"],
            Rationale = ahead ? "Ahead: tighten the timing." : "Behind: be more cautious.",
        };
    }

    private static DraftBudget Budget(JsonNode budget) => new()
    {
        Economy = Number(budget["economy"]),
        Army = Number(budget["army"]),
        Tech = Number(budget["tech"]),
        Defense = Number(budget["defense"]),
    };

    private static List<DraftCondition> Conditions(JsonNode? node) =>
        (node?.AsArray() ?? [])
            .Select(static c => new DraftCondition
            {
                Metric = c!["metric"]!.GetValue<string>(),
                Op = c["op"]!.GetValue<string>(),
                Threshold = Number(c["threshold"]),
                RegionId = c["regionId"]?.GetValue<int>(),
            })
            .ToList();

    private static JsonNode Catalogue(JsonNode match, string playbookId) =>
        (match["catalogue"]?.AsArray() ?? []).FirstOrDefault(p => p?["id"]?.GetValue<string>() == playbookId)
        ?? (match["catalogue"]?.AsArray() ?? []).FirstOrDefault()
        ?? throw new InvalidOperationException("Prompt has an empty playbook catalogue.");

    private static double Number(JsonNode? node)
    {
        if (node is null) return 0;
        JsonValue value = node.AsValue();
        if (value.TryGetValue(out double d)) return d;
        if (value.TryGetValue(out long l)) return l;
        if (value.TryGetValue(out int i)) return i;
        if (value.TryGetValue(out decimal m)) return (double)m;
        return value.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0;
    }
}
