// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arbitration;

namespace Bindery.Ra2.Bot.Strategy;

/// <summary>Tunables for <see cref="PlaybookSelector"/>.</summary>
/// <param name="DefendThreatRatio">Base threat ratio at or above which the selector defends.</param>
/// <param name="TurtleArmyRatio">Army value ratio below which (with confident enemy evidence) the selector turtles.</param>
/// <param name="TurtleMinConfidence">Enemy estimate confidence required before a low ratio causes turtling.</param>
/// <param name="InfantryHeavyShare">Enemy anti-infantry-plus-engineer share that switches to an anti-infantry mix.</param>
/// <param name="AirThreatShare">Enemy air presence (0.3 per distinct aircraft type seen) that switches to an anti-air mix.</param>
/// <param name="TechAfterSeconds">Game time after which a comfortable lead turns into a tech transition.</param>
/// <param name="LifetimeSeconds">Intent lifetime; the scheduler asks again well before it expires.</param>
public sealed record PlaybookSelectorOptions(
    double DefendThreatRatio = 1.3,
    double TurtleArmyRatio = 0.5,
    double TurtleMinConfidence = 0.5,
    double InfantryHeavyShare = 0.5,
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
/// <item>Enemy anti-infantry share ≥ <see cref="PlaybookSelectorOptions.InfantryHeavyShare"/>: the faction's anti-infantry mix.</item>
/// <item>Late game with a lead (ratio ≥ 1.5) and at least two refineries: the faction's tech playbook.</item>
/// <item>Otherwise the faction's main armour timing (Grizzly timing / Rhino rush).</item>
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
        if (features.Enemy.ArmyValueConfidence >= options.TurtleMinConfidence && ratio < options.TurtleArmyRatio
            && Pick(playbooks, faction, allied ? "allied-prism-turtle" : "soviet-turtle") is { } turtle)
        {
            return (turtle, $"outnumbered: army ratio {ratio:0.00}", 0.7);
        }

        double enemyTotal = features.Enemy.CompositionByRole.Values.Where(static v => v > 0).Sum();
        double Share(params UnitRole[] roles) =>
            enemyTotal <= 0 ? 0 : roles.Sum(r => features.Enemy.CompositionByRole.GetValueOrDefault(r)) / enemyTotal;

        double air = AirPresence(features, rules);
        if (air >= options.AirThreatShare && Pick(playbooks, faction, allied ? "allied-harass" : "soviet-flak-mix") is { } antiAir)
        {
            return (antiAir, $"enemy air share {air:0.00}", 0.65);
        }

        double infantry = Share(UnitRole.AntiInfantry, UnitRole.Engineer);
        if (infantry >= options.InfantryHeavyShare && Pick(playbooks, faction, allied ? "allied-ifv-mix" : "soviet-rhino-rush") is { } antiInfantry)
        {
            return (antiInfantry, $"enemy infantry share {infantry:0.00}", 0.6);
        }

        if (features.Time.Seconds >= options.TechAfterSeconds && ratio >= 1.5 && features.Economy.Refineries >= 2
            && Pick(playbooks, faction, allied ? "allied-prism-turtle" : "soviet-apoc-tech") is { } tech)
        {
            return (tech, $"late lead: army ratio {ratio:0.00}", 0.6);
        }

        Playbook? main = Pick(playbooks, faction, allied ? "allied-grizzly-timing" : "soviet-rhino-rush")
            ?? playbooks.For(faction).OrderBy(static p => p.Id, StringComparer.Ordinal).FirstOrDefault();
        return (main, "default armour timing", 0.6);
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
