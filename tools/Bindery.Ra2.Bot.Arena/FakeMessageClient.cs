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
/// (prompt building, structured-output parsing, mapping, validation, latency and
/// arbitration) without a credential. Results produced with it are labelled
/// <c>llm-fake</c>; they measure the pipeline, not a model.
/// </summary>
/// <remarks>
/// Policy, read from the prompt's JSON only: defend at a base threat of 1.3 or more; else,
/// with an army at least 1.5 times the (non-zero) enemy estimate, the faction's armour
/// push; else, before 240 s, the faction's mixed opening (IFV mix / flak mix); else the
/// armour push. A known enemy-held region becomes an attack objective for pressure
/// postures; otherwise it asks for a scout. In Refine mode it keeps the active playbook
/// and moves each numeric parameter 10% toward aggression when ahead and toward caution
/// when behind, within the catalogue's declared range. Token usage is estimated as
/// characters / 4 so the cost columns are exercised; it is not a measurement.
/// </remarks>
public sealed class FakeMessageClient : IMessageClient
{
    public int Calls { get; private set; }

    public Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        JsonNode match = JsonNode.Parse(request.UserContent[0].Text)!;
        JsonNode situation = JsonNode.Parse(request.UserContent[^1].Text)!;
        bool refine = string.Equals(request.SystemPrompt, IntentPromptBuilder.SystemPrompt(StrategistMode.Refine), StringComparison.Ordinal);

        IntentDraft draft = refine ? Refine(match, situation) : Strategic(match, situation);
        string text = JsonSerializer.Serialize(draft, IntentDraftSchema.ParseOptions);
        long input = (request.SystemPrompt.Length + request.UserContent.Sum(static b => b.Text.Length)) / 4;
        ModelUsage usage = new(input, text.Length / 4, 0, 0);
        return Task.FromResult(new ModelReply(text, "end_turn", null, usage, request.Model));
    }

    private static IntentDraft Strategic(JsonNode match, JsonNode situation)
    {
        string faction = match["faction"]!.GetValue<string>();
        bool allied = faction == nameof(Faction.Allied);
        JsonNode features = situation["features"]!;
        double seconds = Number(features["gameSeconds"]);
        double own = Number(features["army"]?["armyValue"]?["now"]);
        double enemy = Number(features["enemy"]?["estimatedArmyValue"]?["now"]);

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

        string playbookId;
        string rationale;
        if (threat >= 1.3)
        {
            playbookId = "generic-defend";
            rationale = $"Base threat ratio {threat:0.00}: defend.";
        }
        else if (enemy > 0 && own >= 1.5 * enemy)
        {
            playbookId = allied ? "allied-grizzly-timing" : "soviet-rhino-rush";
            rationale = $"Army {own:0} vs estimated {enemy:0}: press the advantage.";
        }
        else if (seconds < 240)
        {
            playbookId = allied ? "allied-ifv-mix" : "soviet-flak-mix";
            rationale = "Opening: mixed army while the economy comes up.";
        }
        else
        {
            playbookId = allied ? "allied-grizzly-timing" : "soviet-rhino-rush";
            rationale = "Midgame: armour timing.";
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
            objectives.Add(new DraftObjective { Kind = nameof(ObjectiveKind.AttackRegion), RegionId = target, TypeId = null, Priority = 2 });
        }
        else
        {
            objectives.Add(new DraftObjective { Kind = nameof(ObjectiveKind.Scout), RegionId = null, TypeId = null, Priority = 3 });
        }

        List<DraftParameter> parameters = (playbook["parameters"]?.AsArray() ?? [])
            .Select(static p => new DraftParameter { Name = p!["name"]!.GetValue<string>(), Value = Number(p["default"]) })
            .ToList();
        return Draft(playbook, playbookId, posture, parameters, objectives, 0.7, rationale);
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
        return Draft(playbook, playbookId, posture, parameters, objectives, 0.6, ahead ? "Ahead: tighten the timing." : "Behind: be more cautious.");
    }

    private static IntentDraft Draft(JsonNode playbook, string playbookId, string posture, List<DraftParameter> parameters, List<DraftObjective> objectives, double confidence, string rationale)
    {
        JsonNode budget = playbook["budget"]!;
        return new IntentDraft
        {
            PlaybookId = playbookId,
            Posture = posture,
            Parameters = parameters,
            Objectives = objectives,
            Budget = new DraftBudget
            {
                Economy = Number(budget["economy"]),
                Army = Number(budget["army"]),
                Tech = Number(budget["tech"]),
                Defense = Number(budget["defense"]),
            },
            Composition = (playbook["composition"]?.AsArray() ?? [])
                .Select(static c => new DraftComposition { Role = c!["role"]!.GetValue<string>(), MinShare = Number(c["minShare"]), MaxShare = Number(c["maxShare"]) })
                .ToList(),
            RegionsOfInterest = [],
            AttackConditions = Conditions(playbook["attackConditions"]),
            AbortTriggers = Conditions(playbook["abortTriggers"]),
            ReplanTriggers = [],
            ExpiresInSeconds = 60,
            Confidence = confidence,
            Assumptions = ["fake client: policy derived from the prompt features"],
            Rationale = rationale,
        };
    }

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
