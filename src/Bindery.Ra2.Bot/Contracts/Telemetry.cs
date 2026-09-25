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

/// <summary>Shared JSON settings: camelCase, string enums, no indentation.</summary>
public static class BotJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { WriteIndented = false };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return options;
    }

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
}
