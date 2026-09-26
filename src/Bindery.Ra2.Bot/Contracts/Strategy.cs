// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot;

/// <summary>A compact record of an earlier strategic decision, for strategist context.</summary>
public sealed record IntentHistoryEntry(string IntentId, IntentSource Source, StrategicPosture Posture, string PlaybookId, GameTime AcceptedAt, GameTime? EndedAt, string? EndReason);

/// <summary>
/// What a strategist is given. It contains no <see cref="BeliefSnapshot"/> and
/// no engine state: strategists see compiled features, rule facts and the
/// playbook catalogue only.
/// </summary>
/// <param name="OwnedBuildingTypes">
/// Building types the player owns now (its own state, so fog-safe), so prerequisite paths and time-to-tech are
/// measured from the actual base; null in hand-built contexts that do not say.
/// </param>
/// <param name="Trigger">
/// Why the scheduler asked: <c>initial</c>, <c>cadence</c>, <c>event:&lt;kind&gt;</c> (a major event) or
/// <c>replan:&lt;reason&gt;</c> (the arbiter asked), or a fallback reason; null in hand-built contexts. A request
/// deferred behind an in-flight one keeps the trigger it was started for, although its features may no longer
/// show the event.
/// </param>
/// <param name="ActiveRole">
/// The slot that installed <paramref name="ActiveIntent"/>. <see cref="Arbitration.ProposalRole.Fallback"/> and
/// <see cref="Arbitration.ProposalRole.Emergency"/> intents are placeholders: the arbiter lets any primary proposal replace
/// them at once, with no commitment window. Null when there is no active intent or the context does not say.
/// </param>
/// <param name="ActiveSince">
/// When the arbiter's commitment clock for the active intent started (renewals do not restart it); null when there
/// is no active intent or the context does not say.
/// </param>
/// <param name="BaseThreatOverrideSpent">
/// Whether the arbiter has already spent its base-threat override on the current threat episode
/// (<see cref="Arbitration.IntentArbiter.BaseThreatOverrideSpent"/>); null when there is no active intent or the
/// context does not say.
/// </param>
public sealed record StrategistContext(
    StrategicFeatures Features,
    IRulesDatabase Rules,
    IPlaybookLibrary Playbooks,
    StrategicIntent? ActiveIntent,
    IReadOnlyList<IntentHistoryEntry> History,
    string? Personality,
    IReadOnlySet<string>? OwnedBuildingTypes = null,
    string? Trigger = null,
    Arbitration.ProposalRole? ActiveRole = null,
    GameTime? ActiveSince = null,
    bool? BaseThreatOverrideSpent = null);

/// <summary>Cost and latency accounting for one proposal.</summary>
/// <param name="Model">The model that served the reply (a server-side fallback may differ from the one requested).</param>
/// <param name="CacheCreationTokens">Prompt-cache write tokens (priced above plain input).</param>
/// <param name="Usd">
/// The request's price when the strategist could price each billed attempt at its own model's rate (a declined
/// attempt and the fallback that served the reply may be different models); null when it could not, and then the
/// tokens are priced at <paramref name="Model"/>'s rate.
/// </param>
public sealed record ProposalCost(
    double LatencySeconds,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    string? Model,
    long CacheCreationTokens = 0,
    double? Usd = null);

/// <param name="RefinesIntentId">
/// Set by a strategist that only re-parameterises the active intent (a refine mode): the id of the intent it
/// refines. The scheduler discards the answer when that intent is no longer active on arrival (it was aborted or
/// replaced while the request was in flight), since a refinement of an ended plan would otherwise reinstate it.
/// </param>
public sealed record StrategistProposal(StrategicIntent Intent, ProposalCost Cost, string? RawResponse, string? RefinesIntentId = null);

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
