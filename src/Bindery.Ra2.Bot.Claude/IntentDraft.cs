// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Bindery.Ra2.Bot.Claude;

/// <summary>One playbook parameter override. An array of pairs rather than a map keeps the schema closed (<c>additionalProperties: false</c>).</summary>
public sealed record DraftParameter
{
    public required string Name { get; init; }

    public required double Value { get; init; }
}

/// <summary>Wire form of <see cref="Objective"/>; enum values are the C# member names.</summary>
public sealed record DraftObjective
{
    public required string Kind { get; init; }

    public required int? RegionId { get; init; }

    public required string? TypeId { get; init; }

    public required int Priority { get; init; }
}

/// <summary>Wire form of <see cref="BudgetShares"/>. Normalisation is the validator's job.</summary>
public sealed record DraftBudget
{
    public required double Economy { get; init; }

    public required double Army { get; init; }

    public required double Tech { get; init; }

    public required double Defense { get; init; }
}

/// <summary>Wire form of <see cref="CompositionTarget"/>.</summary>
public sealed record DraftComposition
{
    public required string Role { get; init; }

    public required double MinShare { get; init; }

    public required double MaxShare { get; init; }
}

/// <summary>Wire form of <see cref="Condition"/>.</summary>
public sealed record DraftCondition
{
    public required string Metric { get; init; }

    public required string Op { get; init; }

    public required double Threshold { get; init; }

    public required int? RegionId { get; init; }
}

/// <summary>
/// What the model returns: a structurally complete but unvalidated intent. It is
/// a separate type from <see cref="StrategicIntent"/> so the wire format can stay
/// JSON-schema friendly (string enums, arrays instead of maps, relative expiry)
/// and so nothing the model wrote is trusted until the mapper and the validator
/// have both seen it. Every property is required, matching the schema, so a
/// missing field is a parse failure rather than a silent default.
/// </summary>
public sealed record IntentDraft
{
    public required string PlaybookId { get; init; }

    public required string Posture { get; init; }

    public required IReadOnlyList<DraftParameter> Parameters { get; init; }

    public required IReadOnlyList<DraftObjective> Objectives { get; init; }

    public required DraftBudget Budget { get; init; }

    public required IReadOnlyList<DraftComposition> Composition { get; init; }

    public required IReadOnlyList<int> RegionsOfInterest { get; init; }

    public required IReadOnlyList<DraftCondition> AttackConditions { get; init; }

    public required IReadOnlyList<DraftCondition> AbortTriggers { get; init; }

    public required IReadOnlyList<DraftCondition> ReplanTriggers { get; init; }

    public required double ExpiresInSeconds { get; init; }

    public required double Confidence { get; init; }

    public required IReadOnlyList<string> Assumptions { get; init; }

    public required string Rationale { get; init; }
}

/// <summary>
/// The JSON schema for <see cref="IntentDraft"/> and the strict parser for it.
/// The schema uses only features structured outputs support: every object is
/// closed (<c>additionalProperties: false</c>) with all properties required,
/// nullable fields are <c>anyOf [T, null]</c>, enums list the C# member names,
/// and there are no numeric bounds (unsupported; ranges are stated in the prompt
/// and enforced by the validator).
/// </summary>
public static class IntentDraftSchema
{
    /// <summary>Parser settings: camelCase, unknown members rejected, numbers must be JSON numbers.</summary>
    public static readonly JsonSerializerOptions ParseOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        WriteIndented = false,
    };

    /// <summary>The schema as compact JSON text; identical on every call so the API's schema cache and the prompt cache both hit.</summary>
    public static string Json { get; } = Build().ToJsonString();

    /// <summary>A fresh mutable copy of the schema tree.</summary>
    public static JsonObject Build()
    {
        JsonObject condition = Closed(
            ("metric", StringEnum<ConditionMetric>()),
            ("op", StringEnum<Comparison>()),
            ("threshold", Type("number")),
            ("regionId", Nullable("integer")));

        return Closed(
            ("playbookId", Type("string")),
            ("posture", StringEnum<StrategicPosture>()),
            ("parameters", ArrayOf(Closed(
                ("name", Type("string")),
                ("value", Type("number"))))),
            ("objectives", ArrayOf(Closed(
                ("kind", StringEnum<ObjectiveKind>()),
                ("regionId", Nullable("integer")),
                ("typeId", Nullable("string")),
                ("priority", Type("integer"))))),
            ("budget", Closed(
                ("economy", Type("number")),
                ("army", Type("number")),
                ("tech", Type("number")),
                ("defense", Type("number")))),
            ("composition", ArrayOf(Closed(
                ("role", StringEnum<UnitRole>()),
                ("minShare", Type("number")),
                ("maxShare", Type("number"))))),
            ("regionsOfInterest", ArrayOf(Type("integer"))),
            ("attackConditions", ArrayOf(condition.DeepClone().AsObject())),
            ("abortTriggers", ArrayOf(condition.DeepClone().AsObject())),
            ("replanTriggers", ArrayOf(condition.DeepClone().AsObject())),
            ("expiresInSeconds", Type("number")),
            ("confidence", Type("number")),
            ("assumptions", ArrayOf(Type("string"))),
            ("rationale", Type("string")));
    }

    /// <summary>
    /// Parses model output. Returns null with a reason instead of throwing:
    /// malformed JSON, a missing required field, an unknown field, or a null where
    /// a value is required are all ordinary model failures, not bugs.
    /// </summary>
    public static IntentDraft? TryParse(string text, out string? error)
    {
        try
        {
            IntentDraft? draft = JsonSerializer.Deserialize<IntentDraft>(text, ParseOptions);
            if (draft is null)
            {
                error = "parse.null_document";
                return null;
            }
            error = null;
            return draft;
        }
        catch (JsonException ex)
        {
            error = $"parse.invalid_json: {ex.Message}";
            return null;
        }
    }

    private static JsonObject Type(string type) => new() { ["type"] = type };

    private static JsonObject Nullable(string type) => new()
    {
        ["anyOf"] = new JsonArray(Type(type), Type("null")),
    };

    private static JsonObject ArrayOf(JsonObject items) => new() { ["type"] = "array", ["items"] = items };

    private static JsonObject StringEnum<TEnum>()
        where TEnum : struct, Enum
    {
        JsonArray values = new();
        foreach (string name in Enum.GetNames<TEnum>())
        {
            values.Add(name);
        }
        return new JsonObject { ["type"] = "string", ["enum"] = values };
    }

    private static JsonObject Closed(params (string Name, JsonObject Schema)[] properties)
    {
        JsonObject props = new();
        JsonArray required = new();
        foreach ((string name, JsonObject schema) in properties)
        {
            props[name] = schema;
            required.Add(name);
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
    }
}
