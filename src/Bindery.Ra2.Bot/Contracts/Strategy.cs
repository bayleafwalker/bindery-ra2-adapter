// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot;

/// <summary>A compact record of an earlier strategic decision, for strategist context.</summary>
public sealed record IntentHistoryEntry(string IntentId, IntentSource Source, StrategicPosture Posture, string PlaybookId, GameTime AcceptedAt, GameTime? EndedAt, string? EndReason);

/// <summary>
/// What a strategist is given. It contains no <see cref="BeliefSnapshot"/> and
/// no engine state: strategists see compiled features, rule facts and the
/// playbook catalogue only.
/// </summary>
public sealed record StrategistContext(
    StrategicFeatures Features,
    IRulesDatabase Rules,
    IPlaybookLibrary Playbooks,
    StrategicIntent? ActiveIntent,
    IReadOnlyList<IntentHistoryEntry> History,
    string? Personality);

/// <summary>Cost and latency accounting for one proposal.</summary>
public sealed record ProposalCost(double LatencySeconds, long InputTokens, long OutputTokens, long CacheReadTokens, string? Model);

public sealed record StrategistProposal(StrategicIntent Intent, ProposalCost Cost, string? RawResponse);

/// <summary>
/// A strategy provider: deterministic selector, bandit, distilled classifier or
/// LLM. Implementations may be slow and may return null (no opinion, refusal,
/// timeout). Callers treat every result as a proposal to validate, never as a
/// command.
/// </summary>
public interface IStrategist
{
    string Id { get; }

    IntentSource Source { get; }

    Task<StrategistProposal?> ProposeAsync(StrategistContext context, CancellationToken cancellationToken = default);
}

/// <summary>Optional feedback channel for strategists that learn from outcomes (bandit, distillation).</summary>
public interface IOutcomeLearner
{
    /// <param name="reward">Outcome of an intent's tenure in [-1, 1].</param>
    void Observe(StrategicFeatures atDecision, StrategicIntent intent, double reward);
}
