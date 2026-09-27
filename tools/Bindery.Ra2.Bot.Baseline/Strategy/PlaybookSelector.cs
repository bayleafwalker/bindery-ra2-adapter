// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Baseline.Arbitration;

namespace Bindery.Ra2.Bot.Baseline.Strategy;

/// <summary>Tunables for <see cref="PlaybookSelector"/>.</summary>
/// <param name="DefendThreatRatio">Base threat ratio at or above which the selector defends.</param>
/// <param name="TurtleArmyRatio">Army value ratio below which (with confident enemy evidence) the selector turtles.</param>
/// <param name="EvidenceConfidence">Enemy estimate confidence required before composition or ratio rules apply.</param>
/// <param name="ArmourHeavyShare">Enemy anti-armour share at which the selector answers with its own armour.</param>
/// <param name="AirThreatShare">Enemy air presence (0.3 per distinct aircraft type seen) that switches to an anti-air mix.</param>
/// <param name="TechAfterSeconds">Game time after which a comfortable lead turns into a tech transition.</param>
/// <param name="LifetimeSeconds">Intent lifetime; the scheduler asks again well before it expires.</param>
public sealed record PlaybookSelectorOptions(
    double DefendThreatRatio = 1.3,
    double TurtleArmyRatio = 0.35,
    double EvidenceConfidence = 0.5,
    double ArmourHeavyShare = 0.6,
    double AirThreatShare = 0.25,
    double TechAfterSeconds = 720,
    double LifetimeSeconds = 60);

/// <summary>
/// The rule-based baseline strategist and the fallback behind every other arm. It
/// reads features only, is deterministic and synchronous, and picks one playbook by
/// an ordered rule list (first match wins):
/// <list type="number">
/// <item>Base threat ratio ≥ <see cref="PlaybookSelectorOptions.DefendThreatRatio"/>: <c>generic-defend</c>.</item>
/// <item>Confident enemy estimate and army ratio &lt; <see cref="PlaybookSelectorOptions.TurtleArmyRatio"/>: the faction's turtle.</item>
/// <item>Enemy air presence ≥ <see cref="PlaybookSelectorOptions.AirThreatShare"/>: the faction's anti-air mix.</item>
/// <item>Confident enemy anti-armour share ≥ <see cref="PlaybookSelectorOptions.ArmourHeavyShare"/>: the faction's armour (Grizzly timing / Rhino rush).</item>
/// <item>Late game with a lead (ratio ≥ 1.5) and at least two refineries: the faction's tech playbook.</item>
/// <item>Otherwise the faction's mixed army (IFV mix / flak mix), the strongest pinned style in the region
/// simulator's style-versus-style matrix (see the arena notes in the spec).</item>
/// </list>
/// Playbooks missing from the library, or not serving the faction, are skipped in favour of the next rule.
/// </summary>
public sealed class PlaybookSelector : IStrategist
{
    private readonly PlaybookSelectorOptions options;

    public PlaybookSelector(PlaybookSelectorOptions? options = null, string id = "selector")
    {
        this.options = options ?? new PlaybookSelectorOptions();
        Id = id;
    }

    public string Id { get; }

    public IntentSource Source => IntentSource.Selector;

    public Task<StrategistProposal?> ProposeAsync(StrategistContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        (Playbook? playbook, string reason, double confidence) = Choose(context.Features, context.Playbooks, context.Rules);
        if (playbook is null) return Task.FromResult<StrategistProposal?>(null);
        StrategicIntent intent = IntentComposer.Compose(
            playbook, context.Features, $"{Id}/{context.Features.SnapshotVersion}", Source, confidence, reason, options.LifetimeSeconds);
        return Task.FromResult<StrategistProposal?>(new StrategistProposal(intent, new ProposalCost(0, 0, 0, 0, null), null));
    }

    /// <summary>The rule list, exposed for tests and for strategists that fall back to it.</summary>
    public (Playbook? Playbook, string Reason, double Confidence) Choose(StrategicFeatures features, IPlaybookLibrary playbooks, IRulesDatabase rules)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(playbooks);
        ArgumentNullException.ThrowIfNull(rules);
        Faction faction = features.Faction;
        bool allied = faction == Faction.Allied;

        double threat = ConditionEvaluator.BaseThreatRatio(features);
        if (threat >= options.DefendThreatRatio && Pick(playbooks, faction, "generic-defend") is { } defend)
        {
            return (defend, $"base threat ratio {threat:0.00}", 0.9);
        }

        double ratio = ConditionEvaluator.ArmyValueRatio(features);
        bool confident = features.Enemy.ArmyValueConfidence >= options.EvidenceConfidence;
        if (confident && ratio < options.TurtleArmyRatio
            && Pick(playbooks, faction, allied ? "allied-prism-turtle" : "soviet-turtle") is { } turtle)
        {
            return (turtle, $"outnumbered: army ratio {ratio:0.00}", 0.7);
        }

        double air = AirPresence(features, rules);
        if (air >= options.AirThreatShare && Pick(playbooks, faction, allied ? "allied-harass" : "soviet-flak-mix") is { } antiAir)
        {
            return (antiAir, $"enemy air presence {air:0.00}", 0.65);
        }

        double enemyTotal = features.Enemy.CompositionByRole.Values.Where(static v => v > 0).Sum();
        double armour = enemyTotal <= 0 ? 0 : features.Enemy.CompositionByRole.GetValueOrDefault(UnitRole.AntiArmor) / enemyTotal;
        if (confident && armour >= options.ArmourHeavyShare
            && Pick(playbooks, faction, allied ? "allied-grizzly-timing" : "soviet-rhino-rush") is { } armoured)
        {
            return (armoured, $"enemy armour share {armour:0.00}", 0.6);
        }

        if (features.Time.Seconds >= options.TechAfterSeconds && ratio >= 1.5 && features.Economy.Refineries >= 2
            && Pick(playbooks, faction, allied ? "allied-prism-turtle" : "soviet-apoc-tech") is { } tech)
        {
            return (tech, $"late lead: army ratio {ratio:0.00}", 0.6);
        }

        Playbook? main = Pick(playbooks, faction, allied ? "allied-ifv-mix" : "soviet-flak-mix")
            ?? playbooks.For(faction).OrderBy(static p => p.Id, StringComparer.Ordinal).FirstOrDefault();
        return (main, "default mixed army", 0.6);
    }

    /// <summary>
    /// Enemy composition is reported by role, not by kind, so air presence is judged from the enemy's seen tech:
    /// each distinct aircraft type (per the rules database) counts as a 0.3 share, capped at 1.
    /// </summary>
    private static double AirPresence(StrategicFeatures features, IRulesDatabase rules) =>
        Math.Min(1.0, 0.3 * features.Enemy.KnownTech.Count(t => rules.TryGet(t, out UnitRule rule) && rule.Kind == EntityKind.Aircraft));

    private static Playbook? Pick(IPlaybookLibrary playbooks, Faction faction, string id) =>
        playbooks.TryGet(id, out Playbook playbook) && (playbook.Factions.Count == 0 || playbook.Factions.Contains(faction)) ? playbook : null;
}
