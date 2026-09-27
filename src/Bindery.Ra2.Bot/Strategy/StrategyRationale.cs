// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bindery.Ra2.Bot.Arbitration;

namespace Bindery.Ra2.Bot.Strategy;

/// <summary>
/// The rationale every deterministic strategist attaches to its intent: the playbook, the rule or model that chose
/// it, and the evidence the choice rested on, in one fixed format so post-game timelines read the same whoever
/// decided. Built from features only (fog-safe) with invariant formatting (deterministic).
/// </summary>
public static class StrategyRationale
{
    /// <summary><c>&lt;playbook&gt;: &lt;decision&gt;; evidence: t=…, base threat …, army ratio … (own vs estimate at confidence …), …</c></summary>
    public static string Explain(string playbookId, string decision, StrategicFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);
        return string.Create(CultureInfo.InvariantCulture,
            $"{playbookId}: {decision}; evidence: t={features.Time.Seconds:0} s, base threat {ConditionEvaluator.BaseThreatRatio(features):0.00}, " +
            $"army ratio {ConditionEvaluator.ArmyValueRatio(features):0.00} ({features.Army.ArmyValue.Current:0} vs {features.Enemy.EstimatedArmyValue.Current:0} " +
            $"at confidence {features.Enemy.ArmyValueConfidence:0.00}), {features.Economy.Refineries} refineries, {features.Economy.Harvesters} harvesters, " +
            $"scouting {features.Scouting.CoverageFraction:0.00}");
    }
}
