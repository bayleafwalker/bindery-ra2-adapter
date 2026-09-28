// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arbitration;

namespace Bindery.Ra2.Bot.Strategy;

/// <summary>
/// The shared numeric encoding of <see cref="StrategicFeatures"/> used by every
/// learned strategist (bandit, distilled classifier) and written into the decision
/// log with each intent activation, so a dataset exported from a log and a model
/// trained on it read the same numbers.
/// </summary>
/// <remarks>
/// The order below is the contract; changing it, adding to it or changing a scale
/// requires a new <see cref="Version"/>. Every component is finite and roughly in
/// [0, 4]; ratio metrics use <see cref="ConditionEvaluator"/> so the vector and the
/// playbook conditions share one definition.
/// <list type="number">
/// <item><c>bias</c>: always 1.</item>
/// <item><c>gameMinutes10</c>: game seconds / 600, capped at 4.</item>
/// <item><c>credits5k</c>: credits on hand / 5000, capped at 4.</item>
/// <item><c>income2k</c>: income per minute / 2000, capped at 4.</item>
/// <item><c>harvesters4</c>: harvesters / 4, capped at 4.</item>
/// <item><c>refineries3</c>: refineries / 3, capped at 4.</item>
/// <item><c>lowPower</c>: 1 when drained exceeds produced power.</item>
/// <item><c>ownArmy5k</c>: own army value / 5000, capped at 4.</item>
/// <item><c>enemyArmy5k</c>: estimated enemy army value / 5000, capped at 4.</item>
/// <item><c>armyRatio</c>: <see cref="ConditionEvaluator.ArmyValueRatio"/> / 10 (the ratio cap).</item>
/// <item><c>enemyConfidence</c>: <see cref="EnemyFeatures.ArmyValueConfidence"/>.</item>
/// <item><c>baseThreat</c>: <see cref="ConditionEvaluator.BaseThreatRatio"/> / 10.</item>
/// <item><c>losses15s2k</c>: value lost in the last 15 s / 2000, capped at 4.</item>
/// <item><c>kills15s2k</c>: value killed in the last 15 s / 2000, capped at 4.</item>
/// <item><c>scoutingCoverage</c>: fraction of regions seen within the scouting window.</item>
/// <item><c>ownedOreFraction</c>: fraction of ore regions under own control.</item>
/// <item><c>enemyAntiArmor</c>, <c>enemyAntiInfantry</c>, <c>enemyAntiAir</c>, <c>enemyArtillery</c>: shares of
/// the enemy's estimated composition by role (0 when unknown).</item>
/// <item><c>enemySuperweapon</c>: 1 when an enemy superweapon has been seen.</item>
/// <item><c>enemyTech10</c>: number of distinct enemy types seen / 10, capped at 4.</item>
/// <item><c>factionAllied</c>: 1 for Allied, 0 otherwise.</item>
/// <item><c>enemyObservationAge2m</c>: seconds since the newest enemy observation / 120, capped at 4.</item>
/// <item><c>productionUtilization</c>: <see cref="EconomyFeatures.ProductionUtilization"/>, clamped to [0, 1].</item>
/// </list>
/// </remarks>
public static class FeatureVector
{
    // fv2: spending (and so income2k) and productionUtilization count one item per production queue, sped up
    // by its factories, instead of one item per factory; fv1 datasets are not comparable and are skipped.
    public const string Version = "fv2";

    private const double Cap = 4.0;

    public static IReadOnlyList<string> Names { get; } =
    [
        "bias",
        "gameMinutes10",
        "credits5k",
        "income2k",
        "harvesters4",
        "refineries3",
        "lowPower",
        "ownArmy5k",
        "enemyArmy5k",
        "armyRatio",
        "enemyConfidence",
        "baseThreat",
        "losses15s2k",
        "kills15s2k",
        "scoutingCoverage",
        "ownedOreFraction",
        "enemyAntiArmor",
        "enemyAntiInfantry",
        "enemyAntiAir",
        "enemyArtillery",
        "enemySuperweapon",
        "enemyTech10",
        "factionAllied",
        "enemyObservationAge2m",
        "productionUtilization",
    ];

    public static int Dimension => Names.Count;

    public static double[] Encode(StrategicFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);
        double enemyTotal = features.Enemy.CompositionByRole.Values.Where(static v => v > 0 && double.IsFinite(v)).Sum();
        double Share(UnitRole role) =>
            enemyTotal <= 0 ? 0 : Math.Clamp(features.Enemy.CompositionByRole.GetValueOrDefault(role) / enemyTotal, 0, 1);

        double[] v =
        [
            1.0,
            Scale(features.Time.Seconds, 600),
            Scale(features.Economy.Credits.Current, 5000),
            Scale(features.Economy.IncomePerMinute.Current, 2000),
            Scale(features.Economy.Harvesters, 4),
            Scale(features.Economy.Refineries, 3),
            features.Economy.Power.LowPower ? 1 : 0,
            Scale(features.Army.ArmyValue.Current, 5000),
            Scale(features.Enemy.EstimatedArmyValue.Current, 5000),
            Scale(ConditionEvaluator.ArmyValueRatio(features), ConditionEvaluator.RatioCap),
            Unit(features.Enemy.ArmyValueConfidence),
            Scale(ConditionEvaluator.BaseThreatRatio(features), ConditionEvaluator.RatioCap),
            Scale(features.Army.LossesValue.Delta15s, 2000),
            Scale(features.Army.KillsValue.Delta15s, 2000),
            Unit(features.Scouting.CoverageFraction),
            Unit(features.MapControl.OwnedOreFraction),
            Share(UnitRole.AntiArmor),
            Share(UnitRole.AntiInfantry),
            Share(UnitRole.AntiAir),
            Share(UnitRole.Artillery),
            features.Enemy.SuperweaponKnown ? 1 : 0,
            Scale(features.Enemy.KnownTech.Count, 10),
            features.Faction == Faction.Allied ? 1 : 0,
            Scale(features.Enemy.NewestObservationAgeSeconds, 120),
            Unit(features.Economy.ProductionUtilization),
        ];
        return v;
    }

    private static double Scale(double value, double divisor)
    {
        if (!double.IsFinite(value)) return value > 0 ? Cap : 0;
        return Math.Clamp(value / divisor, 0, Cap);
    }

    private static double Unit(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}
