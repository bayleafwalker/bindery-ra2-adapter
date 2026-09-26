// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Baseline.Arbitration;

namespace Bindery.Ra2.Bot.Baseline.Strategy;

/// <summary>
/// Builds a complete intent from a chosen playbook and the current features: the
/// playbook's defaults plus objectives that follow from its posture. Every
/// deterministic strategist (selector, bandit, distilled, pinned) uses it, so they
/// differ only in which playbook they pick.
/// </summary>
/// <remarks>
/// Objectives are derived from features only (the strategist never sees the map or
/// belief):
/// <list type="bullet">
/// <item>Defend the most threatened own base region (priority 1 while the base threat
/// ratio is at least 1, else 5), for every posture.</item>
/// <item>Pressure, AllIn: attack a known enemy-controlled region (priority 2).
/// Harass: harass it (priority 2).</item>
/// <item>Scout (priority 3) when no enemy region is known and the posture is aggressive,
/// or when scouting coverage is below 40% or the compiler reports important unknowns.</item>
/// <item>Expand: expand toward the nearest expansion candidate (priority 3).</item>
/// <item>The playbook's tech goals as <see cref="ObjectiveKind.TechTo"/> (priority 10+).</item>
/// </list>
/// The target enemy region is the enemy-controlled region with the smallest id: features
/// carry no per-region enemy value, and the operational planner re-targets toward
/// remaining enemy buildings once a region is cleared.
/// </remarks>
public static class IntentComposer
{
    public const double DefaultLifetimeSeconds = 60;

    public static StrategicIntent Compose(
        Playbook playbook,
        StrategicFeatures features,
        string intentId,
        IntentSource source,
        double confidence,
        string rationale,
        double lifetimeSeconds = DefaultLifetimeSeconds,
        IReadOnlyDictionary<string, double>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(playbook);
        ArgumentNullException.ThrowIfNull(features);
        StrategicIntent intent = PlaybookIntents.FromPlaybook(playbook, features, intentId, source, lifetimeSeconds, Math.Clamp(confidence, 0, 1), rationale);

        SortedDictionary<string, double> merged = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, double> pair in intent.PlaybookParameters) merged[pair.Key] = pair.Value;
        if (parameters is not null)
        {
            foreach (KeyValuePair<string, double> pair in parameters) merged[pair.Key] = pair.Value;
        }

        List<Objective> objectives = [.. Objectives(playbook.Posture, features), .. intent.Objectives];
        return intent with
        {
            PlaybookParameters = merged,
            Objectives = objectives,
            Assumptions = Assumptions(features),
        };
    }

    /// <summary>Posture-driven objectives; see the type remarks.</summary>
    public static IReadOnlyList<Objective> Objectives(StrategicPosture posture, StrategicFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);
        List<Objective> objectives = [];
        double threat = ConditionEvaluator.BaseThreatRatio(features);

        ThreatAssessment? baseThreat = features.Threats
            .Where(static t => t.IsBase)
            .OrderBy(static t => t.LocalForceRatio)
            .ThenBy(static t => t.Region.Value)
            .FirstOrDefault();
        if (baseThreat is not null)
        {
            objectives.Add(new Objective(ObjectiveKind.DefendRegion, baseThreat.Region, null, threat >= 1 ? 1 : 5));
        }

        RegionId? enemyRegion = EnemyTarget(features);
        bool aggressive = posture is StrategicPosture.Pressure or StrategicPosture.AllIn or StrategicPosture.Harass;
        if (aggressive && enemyRegion is { } target)
        {
            objectives.Add(new Objective(posture == StrategicPosture.Harass ? ObjectiveKind.Harass : ObjectiveKind.AttackRegion, target, null, 2));
        }

        bool scoutingPoor = features.Scouting.CoverageFraction < 0.4 || features.Scouting.ImportantUnknowns.Count > 0;
        if ((aggressive && enemyRegion is null) || scoutingPoor)
        {
            objectives.Add(new Objective(ObjectiveKind.Scout, null, null, 3));
        }

        if (posture == StrategicPosture.Expand && features.MapControl.ExpansionCandidates.Count > 0)
        {
            objectives.Add(new Objective(ObjectiveKind.Expand, features.MapControl.ExpansionCandidates[0], null, 3));
        }
        return objectives;
    }

    /// <summary>The enemy-controlled region to act against, or null when none is known.</summary>
    public static RegionId? EnemyTarget(StrategicFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);
        foreach (KeyValuePair<RegionId, RegionControl> pair in features.MapControl.Control.OrderBy(static p => p.Key.Value))
        {
            if (pair.Value == RegionControl.Enemy) return pair.Key;
        }
        return null;
    }

    private static IReadOnlyList<string> Assumptions(StrategicFeatures features)
    {
        List<string> assumptions = [];
        if (features.Enemy.ArmyValueConfidence < 0.3) assumptions.Add("enemy army estimate is low-confidence");
        if (EnemyTarget(features) is null) assumptions.Add("no enemy-held region currently known");
        return assumptions;
    }
}
