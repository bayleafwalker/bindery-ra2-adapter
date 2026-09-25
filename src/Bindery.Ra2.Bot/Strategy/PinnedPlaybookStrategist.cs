// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arbitration;

namespace Bindery.Ra2.Bot.Strategy;

/// <summary>
/// A scripted strategist that always proposes one playbook (with posture-derived
/// objectives from <see cref="IntentComposer"/>). The arena's opponent styles are
/// full <c>BotRuntime</c> instances driven by this strategist, so an opponent plays
/// through the same planner, tactics and command gate as the arm under test and
/// differs only in strategy.
/// </summary>
/// <remarks>
/// When base threat reaches <see cref="DefendThreatRatio"/> it proposes
/// <c>generic-defend</c> instead (when that playbook exists), because a pinned style that
/// ignores an attack on its base measures nothing but its own collapse. Pass
/// <c>double.PositiveInfinity</c> to pin unconditionally.
/// </remarks>
public sealed class PinnedPlaybookStrategist : IStrategist
{
    private readonly IReadOnlyDictionary<Faction, string> playbookByFaction;

    /// <param name="playbookByFaction">Playbook to pin, per faction of the player the strategist serves.</param>
    public PinnedPlaybookStrategist(IReadOnlyDictionary<Faction, string> playbookByFaction, string id, double defendThreatRatio = 1.3, double confidence = 0.8)
    {
        ArgumentNullException.ThrowIfNull(playbookByFaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        this.playbookByFaction = playbookByFaction;
        Id = id;
        DefendThreatRatio = defendThreatRatio;
        Confidence = confidence;
    }

    public string Id { get; }

    public IntentSource Source => IntentSource.Scripted;

    public double DefendThreatRatio { get; }

    public double Confidence { get; }

    public Task<StrategistProposal?> ProposeAsync(StrategistContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        StrategicFeatures features = context.Features;
        string? id = playbookByFaction.TryGetValue(features.Faction, out string? pinned) ? pinned : null;
        string reason = $"pinned {id}";
        double threat = ConditionEvaluator.BaseThreatRatio(features);
        if (threat >= DefendThreatRatio && context.Playbooks.TryGet("generic-defend", out _))
        {
            id = "generic-defend";
            reason = $"pinned style defends: base threat ratio {threat:0.00}";
        }
        if (id is null || !context.Playbooks.TryGet(id, out Playbook playbook)) return Task.FromResult<StrategistProposal?>(null);
        StrategicIntent intent = IntentComposer.Compose(playbook, features, $"{Id}/{features.SnapshotVersion}", Source, Confidence, reason);
        return Task.FromResult<StrategistProposal?>(new StrategistProposal(intent, new ProposalCost(0, 0, 0, 0, null), null));
    }
}
