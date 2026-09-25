// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace Bindery.Ra2.Bot;

/// <summary>
/// Decision-log record kinds. The log is the evidence for every metric the
/// arena reports; nothing is scored from memory.
/// </summary>
public static class DecisionRecordKinds
{
    public const string Proposal = "strategy.proposal";
    public const string ProposalFailed = "strategy.proposal_failed";
    public const string Validation = "strategy.validation";
    public const string IntentActivated = "strategy.intent_activated";
    public const string IntentEnded = "strategy.intent_ended";
    public const string ShadowProposal = "strategy.shadow";
    public const string LateDiscarded = "strategy.late_discarded";
    public const string Plan = "operations.plan";
    public const string CommandDropped = "command.dropped";
    public const string LeasePreempted = "lease.preempted";
    public const string MatchResult = "match.result";
}

/// <param name="Data">Kind-specific payload, serialised with <see cref="BotJson.Options"/>.</param>
public sealed record DecisionRecord(string Kind, GameTime Time, long SnapshotVersion, JsonElement Data);

public interface IDecisionLog
{
    void Write(DecisionRecord record);

    IReadOnlyList<DecisionRecord> Records { get; }
}

/// <summary>
/// Shared JSON settings: camelCase, string enums, no indentation. Non-finite doubles are written as the
/// named literals <c>"Infinity"</c>/<c>"NaN"</c> (several features use infinity for "never observed"), and
/// <see cref="RegionId"/> works as a dictionary key (written as its number), so every contract record,
/// including <see cref="StrategicFeatures"/> and <see cref="BeliefSnapshot"/>, serialises.
/// </summary>
public static class BotJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        options.Converters.Add(new RegionIdJsonConverter());
        return options;
    }

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    /// <summary>
    /// Keeps the default value shape (<c>{"value":3}</c>) and adds dictionary-key support (<c>"3"</c>).
    /// </summary>
    private sealed class RegionIdJsonConverter : System.Text.Json.Serialization.JsonConverter<RegionId>
    {
        public override RegionId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number) return new RegionId(reader.GetInt32());
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("RegionId must be an object or a number.");
            int? value = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Malformed RegionId.");
                bool isValue = string.Equals(reader.GetString(), "value", StringComparison.OrdinalIgnoreCase);
                reader.Read();
                if (isValue) value = reader.GetInt32();
                else reader.Skip();
            }
            return new RegionId(value ?? throw new JsonException("RegionId has no value."));
        }

        public override void Write(Utf8JsonWriter writer, RegionId value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("value", value.Value);
            writer.WriteEndObject();
        }

        public override RegionId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            int.TryParse(reader.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int id)
                ? new RegionId(id)
                : throw new JsonException($"'{reader.GetString()}' is not a region id.");

        public override void WriteAsPropertyName(Utf8JsonWriter writer, RegionId value, JsonSerializerOptions options) =>
            writer.WritePropertyName(value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
