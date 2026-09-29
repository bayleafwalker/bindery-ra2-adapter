// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Arbitration;

/// <summary>
/// Outcome of evaluating one <see cref="Condition"/>. <see cref="Value"/> is the
/// measured metric (NaN when it could not be measured); <see cref="Issue"/> says why
/// a condition could not be measured, in which case <see cref="Holds"/> is false.
/// </summary>
public sealed record ConditionResult(Condition Condition, bool Holds, double Value, ValidationIssue? Issue);

/// <summary>
/// Deterministic evaluation of <see cref="Condition"/> predicates over
/// <see cref="StrategicFeatures"/>. Attack conditions, abort triggers and replan
/// triggers are all evaluated here so the arbiter, the planner and any offline
/// analysis agree on what a condition means.
/// </summary>
/// <remarks>
/// Metric definitions (every one reads only compiled features, so the fog
/// invariant carries over automatically):
/// <list type="bullet">
/// <item><see cref="ConditionMetric.GameSeconds"/>: <c>Time.Seconds</c>.</item>
/// <item><see cref="ConditionMetric.Credits"/>: <c>Economy.Credits.Current</c>.</item>
/// <item><see cref="ConditionMetric.IncomePerMinute"/>: <c>Economy.IncomePerMinute.Current</c>.</item>
/// <item><see cref="ConditionMetric.OwnArmyValue"/>: <c>Army.ArmyValue.Current</c>.</item>
/// <item><see cref="ConditionMetric.EnemyArmyValueEstimate"/>: <c>Enemy.EstimatedArmyValue.Current</c>.</item>
/// <item><see cref="ConditionMetric.ArmyValueRatio"/>: own army value / estimated enemy army value,
/// capped at <see cref="RatioCap"/>; an enemy estimate of zero (or less) yields <see cref="RatioCap"/>
/// because "nothing seen" must not divide by zero and reads as overwhelming superiority.</item>
/// <item><see cref="ConditionMetric.LocalForceRatio"/>: requires <see cref="Condition.Region"/>; the
/// <see cref="ThreatAssessment.LocalForceRatio"/> of that region's threat entry (own responding value
/// over enemy value, capped at <see cref="RatioCap"/>). A region with no threat entry has no known
/// enemy presence and yields <see cref="RatioCap"/>. Without a region the condition is false and
/// carries a <c>condition.region_required</c> issue.</item>
/// <item><see cref="ConditionMetric.HarvesterCount"/>: <c>Economy.Harvesters</c>.</item>
/// <item><see cref="ConditionMetric.ScoutingAgeSeconds"/>: requires a region;
/// <c>Scouting.RegionAgeSeconds[region]</c>, or positive infinity when the region has never been seen.</item>
/// <item><see cref="ConditionMetric.BaseThreatRatio"/>: the maximum over threats with
/// <see cref="ThreatAssessment.IsBase"/> of enemy value / own value, capped at <see cref="RatioCap"/>
/// (enemy value with zero own value yields the cap; zero enemy value yields 0). No base threats yields 0.</item>
/// <item><see cref="ConditionMetric.LossesValue15s"/>: <c>Army.LossesValue.Delta15s</c>, the value lost
/// in the last 15 seconds.</item>
/// <item><see cref="ConditionMetric.EnemyAirShare"/>, <see cref="ConditionMetric.EnemyVehicleShare"/>,
/// <see cref="ConditionMetric.EnemyInfantryShare"/>: <c>Enemy.ValueByClass</c> for Aircraft, Vehicle, Infantry
/// over the sum of all classes (naval included in the denominator); 0 when nothing is seen. The class is the
/// contact's <see cref="EntityKind"/>, and value is confidence-weighted like the army estimate.</item>
/// <item><see cref="ConditionMetric.EnemyArmyConfidence"/>: <c>Enemy.ArmyValueConfidence</c> (0 with no army contact).</item>
/// <item><see cref="ConditionMetric.OwnedRegions"/>: regions with <see cref="RegionControl.Own"/> in
/// <c>MapControl.Control</c>, i.e. we have an entity there and no live enemy contact is remembered there.</item>
/// </list>
/// A NaN measurement makes the condition false with a <c>condition.nan</c> issue: an
/// undefined quantity must never satisfy an attack condition or fire a trigger.
/// </remarks>
public static class ConditionEvaluator
{
    /// <summary>Upper bound for every ratio metric, so "no enemy seen" is a finite, comparable number.</summary>
    public const double RatioCap = 10.0;

    public const string RegionRequiredCode = "condition.region_required";
    public const string NotANumberCode = "condition.nan";

    /// <summary>True when the metric is only defined for a specific region.</summary>
    public static bool RequiresRegion(ConditionMetric metric) =>
        metric is ConditionMetric.LocalForceRatio or ConditionMetric.ScoutingAgeSeconds;

    /// <summary>Measures a metric. Returns false with an issue when the metric cannot be measured.</summary>
    public static bool TryMeasure(ConditionMetric metric, RegionId? region, StrategicFeatures features, out double value, out ValidationIssue? issue)
    {
        ArgumentNullException.ThrowIfNull(features);
        issue = null;
        if (RequiresRegion(metric) && region is null)
        {
            value = double.NaN;
            issue = new ValidationIssue(RegionRequiredCode, ValidationSeverity.Warning, $"{metric} needs a region.");
            return false;
        }

        value = metric switch
        {
            ConditionMetric.GameSeconds => features.Time.Seconds,
            ConditionMetric.Credits => features.Economy.Credits.Current,
            ConditionMetric.IncomePerMinute => features.Economy.IncomePerMinute.Current,
            ConditionMetric.OwnArmyValue => features.Army.ArmyValue.Current,
            ConditionMetric.EnemyArmyValueEstimate => features.Enemy.EstimatedArmyValue.Current,
            ConditionMetric.ArmyValueRatio => ArmyValueRatio(features),
            ConditionMetric.LocalForceRatio => LocalForceRatio(features, region!.Value),
            ConditionMetric.HarvesterCount => features.Economy.Harvesters,
            ConditionMetric.ScoutingAgeSeconds => features.Scouting.RegionAgeSeconds.TryGetValue(region!.Value, out double age) ? age : double.PositiveInfinity,
            ConditionMetric.BaseThreatRatio => BaseThreatRatio(features),
            ConditionMetric.LossesValue15s => features.Army.LossesValue.Delta15s,
            ConditionMetric.EnemyAirShare => ClassShare(features, EntityKind.Aircraft),
            ConditionMetric.EnemyVehicleShare => ClassShare(features, EntityKind.Vehicle),
            ConditionMetric.EnemyInfantryShare => ClassShare(features, EntityKind.Infantry),
            ConditionMetric.EnemyArmyConfidence => features.Enemy.ArmyValueConfidence,
            ConditionMetric.OwnedRegions => features.MapControl.Control.Values.Count(static c => c == RegionControl.Own),
            _ => double.NaN,
        };

        if (double.IsNaN(value))
        {
            issue = new ValidationIssue(NotANumberCode, ValidationSeverity.Warning, $"{metric} is undefined.");
            return false;
        }
        return true;
    }

    /// <summary>Value share of one enemy class among all seen enemy army value; 0 when nothing is seen.</summary>
    public static double ClassShare(StrategicFeatures features, EntityKind kind)
    {
        ArgumentNullException.ThrowIfNull(features);
        IReadOnlyDictionary<EntityKind, double>? byClass = features.Enemy.ValueByClass;
        if (byClass is null) return 0;
        double total = 0;
        foreach (double v in byClass.Values) total += Math.Max(v, 0);
        if (total <= 0) return 0;
        return Math.Max(byClass.GetValueOrDefault(kind), 0) / total;
    }

    /// <summary>Own army value over the enemy estimate, capped; see the type remarks.</summary>
    public static double ArmyValueRatio(StrategicFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);
        return Ratio(features.Army.ArmyValue.Current, features.Enemy.EstimatedArmyValue.Current);
    }

    /// <summary>Worst enemy/own ratio over base threats; see the type remarks.</summary>
    public static double BaseThreatRatio(StrategicFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);
        double worst = 0;
        foreach (ThreatAssessment threat in features.Threats)
        {
            if (!threat.IsBase) continue;
            double ratio = threat.EnemyValue <= 0 ? 0 : Ratio(threat.EnemyValue, threat.OwnValue);
            if (ratio > worst) worst = ratio;
        }
        return worst;
    }

    /// <summary>Local force ratio for a region; see the type remarks.</summary>
    public static double LocalForceRatio(StrategicFeatures features, RegionId region)
    {
        ArgumentNullException.ThrowIfNull(features);
        foreach (ThreatAssessment threat in features.Threats)
        {
            if (threat.Region == region) return Math.Min(threat.LocalForceRatio, RatioCap);
        }
        return RatioCap;
    }

    public static ConditionResult Evaluate(Condition condition, StrategicFeatures features)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (double.IsNaN(condition.Threshold))
        {
            return new ConditionResult(condition, false, double.NaN,
                new ValidationIssue(NotANumberCode, ValidationSeverity.Warning, $"{condition.Metric} threshold is NaN."));
        }
        if (!TryMeasure(condition.Metric, condition.Region, features, out double value, out ValidationIssue? issue))
        {
            return new ConditionResult(condition, false, value, issue);
        }
        bool holds = condition.Op switch
        {
            Comparison.Lt => value < condition.Threshold,
            Comparison.Le => value <= condition.Threshold,
            Comparison.Gt => value > condition.Threshold,
            Comparison.Ge => value >= condition.Threshold,
            _ => false,
        };
        return new ConditionResult(condition, holds, value, null);
    }

    public static bool Holds(Condition condition, StrategicFeatures features) => Evaluate(condition, features).Holds;

    /// <summary>True when every condition holds. An empty list holds (no precondition).</summary>
    public static bool AllOf(IEnumerable<Condition> conditions, StrategicFeatures features)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        foreach (Condition condition in conditions)
        {
            if (!Holds(condition, features)) return false;
        }
        return true;
    }

    /// <summary>True when at least one condition holds. An empty list does not hold (no trigger).</summary>
    public static bool AnyOf(IEnumerable<Condition> conditions, StrategicFeatures features)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        foreach (Condition condition in conditions)
        {
            if (Holds(condition, features)) return true;
        }
        return false;
    }

    /// <summary>The first condition that holds, or null; used to name which trigger fired.</summary>
    public static Condition? FirstHolding(IEnumerable<Condition> conditions, StrategicFeatures features)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        foreach (Condition condition in conditions)
        {
            if (Holds(condition, features)) return condition;
        }
        return null;
    }

    public static IReadOnlyList<ConditionResult> EvaluateAll(IEnumerable<Condition> conditions, StrategicFeatures features)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        return conditions.Select(c => Evaluate(c, features)).ToList();
    }

    private static double Ratio(double numerator, double denominator)
    {
        if (double.IsNaN(numerator) || double.IsNaN(denominator)) return double.NaN;
        if (denominator <= 0) return RatioCap;
        return Math.Min(Math.Max(numerator, 0) / denominator, RatioCap);
    }
}
