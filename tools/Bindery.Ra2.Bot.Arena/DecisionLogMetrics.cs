// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Arbitration;
using Bindery.Ra2.Bot.Claude;

namespace Bindery.Ra2.Bot.Arena;

/// <summary>
/// The strategy and cost figures an arm is scored on, read from its decision log and split by role. The arm's
/// proposals, invalid plans, late discards and lateness are the <c>Primary</c> strategist's only: the selector
/// fallback and the emergency path answer at once and always validly, so mixing them in made a worse LLM (one that
/// fails more, leaving more to the fallback) look faster and more valid. Fallback and emergency proposals are
/// counted apart, a shadow strategist's lateness and validation (including <c>fog.*</c> rejections) are reported
/// in their own figures, and every request that billed tokens counts toward cost, failed ones included.
/// </summary>
public static class DecisionLogMetrics
{
    private const string PrimaryRole = nameof(ProposalRole.Primary);

    public static void Apply(ArenaAgentStats stats, IEnumerable<DecisionRecord> records)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(records);
        stats.Proposals = 0;
        stats.LlmAnswered = 0;
        stats.LlmFailed = 0;
        stats.Rejected = 0;
        stats.LateDiscarded = 0;
        stats.LateSeconds.Clear();
        foreach (DecisionRecord record in records)
        {
            if (record.Data.ValueKind != JsonValueKind.Object) continue;
            string? role = record.Data.TryGetProperty("role", out JsonElement r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            switch (record.Kind)
            {
                case DecisionRecordKinds.Proposal:
                    if (role == PrimaryRole)
                    {
                        stats.Proposals++;
                        if (IsModelIntent(record.Data)) stats.LlmAnswered++;
                        if (LatencySeconds(record.Data) is { } seconds) stats.LateSeconds.Add(seconds);
                    }
                    else
                    {
                        stats.FallbackProposals++;
                    }
                    AddCost(stats, record.Data, failed: false);
                    break;

                case DecisionRecordKinds.Validation:
                    stats.FogRejections += FogRejects(record.Data);
                    if (role == PrimaryRole && record.Data.TryGetProperty("accepted", out JsonElement accepted) && accepted.ValueKind == JsonValueKind.False
                        && !HasStaleReject(record.Data))
                    {
                        stats.Rejected++;
                    }
                    break;

                case DecisionRecordKinds.LateDiscarded:
                    if (role == PrimaryRole) stats.LateDiscarded++;
                    break;

                case DecisionRecordKinds.ShadowProposal:
                    // Shadow arms: the shadow strategist is the model, so each of its proposals is an answer.
                    if (IsModelIntent(record.Data)) stats.LlmAnswered++;
                    if (LatencySeconds(record.Data) is { } shadowSeconds) stats.ShadowLateSeconds.Add(shadowSeconds);
                    stats.ShadowFogRejections += FogRejects(record.Data);
                    if (record.Data.TryGetProperty("accepted", out JsonElement shadowAccepted) && shadowAccepted.ValueKind == JsonValueKind.False) stats.ShadowRejected++;
                    AddCost(stats, record.Data, failed: false);
                    break;

                case DecisionRecordKinds.ProposalFailed:
                    // Only the Claude strategist's own failure record carries a cost (the scheduler's has none).
                    AddCost(stats, record.Data, failed: true);
                    // That role-less record is also the one call that failed; the scheduler's Primary record for the
                    // same request only says no_opinion/superseded, which are normal arbitration outcomes.
                    if (role is null && LlmCallTally.IsDeliveryFailure(record.Data.TryGetProperty("code", out JsonElement code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null))
                    {
                        stats.LlmFailed++;
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// True when the proposal's intent came from the model (<c>IntentSource.Llm</c>), not the selector, the distilled
    /// model's own answer or a fallback: only those show the model delivering.
    /// </summary>
    private static bool IsModelIntent(JsonElement data) =>
        data.TryGetProperty("intent", out JsonElement intent) && intent.ValueKind == JsonValueKind.Object
        && intent.TryGetProperty("source", out JsonElement source) && source.ValueKind == JsonValueKind.String
        && source.GetString() == nameof(IntentSource.Llm);

    private static double? LatencySeconds(JsonElement data) =>
        data.TryGetProperty("latencyFrames", out JsonElement frames) && frames.ValueKind == JsonValueKind.Number
            ? frames.GetInt64() / (double)GameTime.FramesPerSecond
            : null;

    private static IEnumerable<(string Code, string? Severity)> Issues(JsonElement data)
    {
        if (!data.TryGetProperty("issues", out JsonElement issues) || issues.ValueKind != JsonValueKind.Array) yield break;
        foreach (JsonElement issue in issues.EnumerateArray())
        {
            if (issue.TryGetProperty("code", out JsonElement code) && code.GetString() is { } c)
            {
                yield return (c, issue.TryGetProperty("severity", out JsonElement severity) ? severity.GetString() : null);
            }
        }
    }

    private static int FogRejects(JsonElement data) =>
        Issues(data).Count(static i => i.Code.StartsWith("fog.", StringComparison.Ordinal) && i.Severity == nameof(ValidationSeverity.Reject));

    private static bool HasStaleReject(JsonElement data) =>
        Issues(data).Any(static i => i.Severity == nameof(ValidationSeverity.Reject) && ValidationCodes.IsStale(i.Code));

    private static void AddCost(ArenaAgentStats stats, JsonElement data, bool failed)
    {
        if (!data.TryGetProperty("cost", out JsonElement cost) || cost.ValueKind != JsonValueKind.Object) return;
        long input = cost.TryGetProperty("inputTokens", out JsonElement i) ? i.GetInt64() : 0;
        long output = cost.TryGetProperty("outputTokens", out JsonElement o) ? o.GetInt64() : 0;
        long cached = cost.TryGetProperty("cacheReadTokens", out JsonElement c) ? c.GetInt64() : 0;
        long written = cost.TryGetProperty("cacheCreationTokens", out JsonElement w) && w.ValueKind == JsonValueKind.Number ? w.GetInt64() : 0;
        double? priced = cost.TryGetProperty("usd", out JsonElement u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : null;
        string? model = cost.TryGetProperty("model", out JsonElement md) && md.ValueKind == JsonValueKind.String ? md.GetString() : null;
        stats.TokensIn += input + cached + written;
        stats.TokensOut += output;
        if (model is null) return;
        // The serving model is tallied apart from the arm's own (set from its configuration), so a reply a fallback
        // served does not relabel the arm; and a model without a price is counted, not priced at zero.
        stats.ServedBy[model] = stats.ServedBy.GetValueOrDefault(model) + 1;
        if ((priced ?? PriceTable.CostUsd(model, input, output, cached, written)) is not { } usd)
        {
            stats.UnpricedRequests++;
            return;
        }
        stats.Usd += usd;
        if (failed) stats.FailedRequestUsd += usd;
    }
}
