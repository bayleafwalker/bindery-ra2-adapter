// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Operations;

/// <summary>
/// A small internal evaluator for <see cref="Condition"/> against
/// <see cref="StrategicFeatures"/>, used for playbook attack conditions.
/// The Arbitration package (C) writes the shared, canonical evaluator used
/// for validation and abort/replan triggers concurrently with this package;
/// this copy exists only so Operations does not block on that package, and
/// <see cref="OperationalOptions.ConditionEvaluator"/> lets the integrator
/// swap it for the canonical one without touching this package's code.
/// </summary>
public static class DefaultConditionEvaluator
{
    public static bool Evaluate(Condition condition, StrategicFeatures features)
    {
        double value = MetricValue(condition, features);
        return condition.Op switch
        {
            Comparison.Lt => value < condition.Threshold,
            Comparison.Le => value <= condition.Threshold,
            Comparison.Gt => value > condition.Threshold,
            Comparison.Ge => value >= condition.Threshold,
            _ => false,
        };
    }

    private static double MetricValue(Condition condition, StrategicFeatures features) => condition.Metric switch
    {
        ConditionMetric.GameSeconds => features.Time.Seconds,
        ConditionMetric.Credits => features.Economy.Credits.Current,
        ConditionMetric.IncomePerMinute => features.Economy.IncomePerMinute.Current,
        ConditionMetric.OwnArmyValue => features.Army.ArmyValue.Current,
        ConditionMetric.EnemyArmyValueEstimate => features.Enemy.EstimatedArmyValue.Current,
        ConditionMetric.ArmyValueRatio => ArmyValueRatio(features),
        ConditionMetric.LocalForceRatio => Threat(features, condition.Region)?.LocalForceRatio ?? 0.0,
        ConditionMetric.HarvesterCount => features.Economy.Harvesters,
        ConditionMetric.ScoutingAgeSeconds => ScoutingAge(features, condition.Region),
        ConditionMetric.BaseThreatRatio => BaseThreatRatio(features),
        ConditionMetric.LossesValue15s => features.Army.LossesValue.Delta15s,
        _ => 0.0,
    };

    private static double ArmyValueRatio(StrategicFeatures features)
    {
        double enemy = features.Enemy.EstimatedArmyValue.Current;
        if (enemy <= 0) return features.Army.ArmyValue.Current > 0 ? double.PositiveInfinity : 0.0;
        return features.Army.ArmyValue.Current / enemy;
    }

    private static ThreatAssessment? Threat(StrategicFeatures features, RegionId? region)
    {
        if (region is not { } r) return null;
        foreach (ThreatAssessment threat in features.Threats)
        {
            if (threat.Region == r) return threat;
        }
        return null;
    }

    private static double ScoutingAge(StrategicFeatures features, RegionId? region)
    {
        if (region is not { } r) return double.PositiveInfinity;
        return features.Scouting.RegionAgeSeconds.TryGetValue(r, out double age) ? age : double.PositiveInfinity;
    }

    /// <summary>
    /// Worst-case enemy/own value ratio among own-base regions under threat;
    /// 0 when no base region is threatened. Higher means more dangerous, the
    /// inverse of <see cref="ThreatAssessment.LocalForceRatio"/> (own/enemy).
    /// </summary>
    private static double BaseThreatRatio(StrategicFeatures features)
    {
        double worst = 0.0;
        foreach (ThreatAssessment threat in features.Threats)
        {
            if (!threat.IsBase) continue;
            double ratio = threat.LocalForceRatio <= 0 ? double.PositiveInfinity : 1.0 / threat.LocalForceRatio;
            if (ratio > worst) worst = ratio;
        }
        return worst;
    }
}
