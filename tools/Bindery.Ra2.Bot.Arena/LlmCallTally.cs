// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bindery.Ra2.Bot.Claude;

namespace Bindery.Ra2.Bot.Arena;

/// <summary>
/// What a live LLM arm delivered in a match: Primary requests the model answered (<c>strategy.proposal</c>) and
/// requests that failed in transport or in the model (timeout, server error, refusal, unparseable reply, ...). A
/// request that only lost arbitration (<c>superseded</c>, <c>no_opinion</c>) is neither. When most calls fail the
/// selector fallback plays the gaps and the arm's results are the selector's, not the model's.
/// </summary>
public sealed record LlmCallTally(int Answered, int Failed)
{
    public double FailureRate => Answered + Failed == 0 ? 0 : Failed / (double)(Answered + Failed);

    /// <summary>
    /// True for a failure code of the Claude strategist's own failure record. Cancellations are the strategist
    /// abandoning a request it no longer needs, and the <c>claude.refine_*</c> codes are refine mode declining a
    /// request it has no plan to refine (a no-op by design), not failed deliveries.
    /// </summary>
    public static bool IsDeliveryFailure(string? code) =>
        !string.IsNullOrEmpty(code) && code != ClaudeFailureCodes.Cancelled && !code.StartsWith("claude.refine_", StringComparison.Ordinal);

    /// <summary>The tally pooled over an arm's matches (matches without one, such as fake or non-LLM, add nothing).</summary>
    public static LlmCallTally? Pool(IEnumerable<MatchRecord> matches)
    {
        List<LlmCallTally> tallies = [.. matches.Select(static m => m.Players.TryGetValue("arm", out PlayerMatchMetrics? p) ? p.LlmCalls : null).OfType<LlmCallTally>()];
        return tallies.Count == 0 ? null : new LlmCallTally(tallies.Sum(static t => t.Answered), tallies.Sum(static t => t.Failed));
    }

    /// <summary>True when the pooled failure rate is above <paramref name="maxRate"/>.</summary>
    public bool Exceeds(double maxRate) => FailureRate > maxRate;

    /// <summary>
    /// The (arm, split) cells whose pooled failure rate exceeds <paramref name="maxRate"/>, with the tally. The
    /// threshold applies per split so a failing training split cannot taint a clean held-out one, or the reverse.
    /// </summary>
    public static IReadOnlyDictionary<(string Arm, string Split), LlmCallTally> Unreliable(IEnumerable<MatchRecord> matches, double maxRate) =>
        matches.GroupBy(static m => (m.Arm, m.Split))
            .Select(static g => (g.Key, Tally: Pool(g)))
            .Where(x => x.Tally is { } t && t.Exceeds(maxRate))
            .ToDictionary(static x => x.Key, static x => x.Tally!);

    public string Describe() =>
        string.Create(CultureInfo.InvariantCulture, $"{Answered} answered, {Failed} failed ({FailureRate:P1} failure rate)");

    public string Warning(string arm, string split, double maxRate) =>
        string.Create(CultureInfo.InvariantCulture, $"Warning: {arm} ({split} maps): not a model result. {Failed} of {Answered + Failed} model calls failed ({FailureRate:P1}, above --max-llm-failure-rate {maxRate:0.##}); the selector fallback played the gaps.");
}
