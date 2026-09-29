// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Claude;

/// <summary>Which <see cref="ConditionMetric"/> values the model is offered.</summary>
public static class ConditionMetrics
{
    /// <summary>The metrics added after the first prompt; offered only when <see cref="ClaudeStrategistOptions.ExtendedConditionMetrics"/> is on.</summary>
    public static readonly IReadOnlyList<ConditionMetric> Extended =
    [
        ConditionMetric.EnemyAirShare,
        ConditionMetric.EnemyVehicleShare,
        ConditionMetric.EnemyInfantryShare,
        ConditionMetric.EnemyArmyConfidence,
        ConditionMetric.OwnedRegions,
    ];

    /// <summary>Every metric in declaration order, minus <see cref="Extended"/> unless asked for.</summary>
    public static IEnumerable<ConditionMetric> Offered(bool extended) =>
        Enum.GetValues<ConditionMetric>().Where(m => extended || !Extended.Contains(m));
}
