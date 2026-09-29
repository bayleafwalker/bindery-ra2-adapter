// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Operations;

/// <summary>
/// What the enemy army may be when the bot decides whether to launch an attack. An unseen enemy is not an enemy with
/// nothing: without fresh, confident sightings the estimate falls back to a prior that grows with match time, and the
/// result is raised by a margin that shrinks as evidence improves, so an attack gate reads a lower confidence bound on
/// the force ratio.
/// </summary>
public static class EnemyArmyBound
{
    /// <summary>
    /// Weight in [0, 1] of the sighted estimate: the army sighting's confidence, fading linearly to 0 as it ages, or
    /// the freshness of the last look at a known enemy base region, whichever is higher. A base seen recently with no
    /// army in it is evidence of a small army, not an absence of evidence. The base term alone is capped at
    /// <see cref="OperationalOptions.BaseSightingWeightCap"/>; the army sighting is not.
    /// </summary>
    /// <param name="baseSeenAgeSeconds">Seconds since a known enemy base region was last seen; infinity when none is known.</param>
    public static double EvidenceWeight(EnemyFeatures enemy, double baseSeenAgeSeconds, OperationalOptions options)
    {
        double army = Math.Clamp(enemy.ArmyValueConfidence, 0, 1) * Freshness(enemy.NewestObservationAgeSeconds, options);
        double baseTerm = Math.Min(Freshness(baseSeenAgeSeconds, options), Math.Clamp(options.BaseSightingWeightCap, 0, 1));
        return Math.Max(army, baseTerm);
    }

    private static double Freshness(double ageSeconds, OperationalOptions options) =>
        options.EnemyEvidenceSeconds <= 0 || double.IsNaN(ageSeconds) ? 0 : Math.Max(0, 1 - (ageSeconds / options.EnemyEvidenceSeconds));

    /// <summary>Army value an unseen enemy is assumed to have fielded by <paramref name="seconds"/>.</summary>
    public static double Prior(double seconds, OperationalOptions options) =>
        Math.Min(Math.Max(0, seconds - options.EnemyPriorStartSeconds) * options.EnemyPriorValuePerSecond, options.EnemyPriorMaxValue);

    /// <summary>Upper estimate of enemy army value: the sighting blended with the prior, plus the uncertainty margin.</summary>
    public static double Upper(EnemyFeatures enemy, double baseSeenAgeSeconds, double seconds, OperationalOptions options)
    {
        double seen = Math.Max(0, enemy.EstimatedArmyValue.Current);
        double w = EvidenceWeight(enemy, baseSeenAgeSeconds, options);
        double expected = (w * seen) + ((1 - w) * Math.Max(seen, Prior(seconds, options)));
        return expected * (1 + (options.EnemyUncertaintyMargin * (1 - w)));
    }

    /// <summary>Own army value over <see cref="Upper"/>; infinite when the bound is 0.</summary>
    public static double LowerForceRatio(double ownArmyValue, EnemyFeatures enemy, double baseSeenAgeSeconds, double seconds, OperationalOptions options)
    {
        double upper = Upper(enemy, baseSeenAgeSeconds, seconds, options);
        return upper <= 0 ? double.PositiveInfinity : ownArmyValue / upper;
    }

    /// <summary>The launch ratio required at this evidence weight: <see cref="OperationalOptions.MinAttackForceRatio"/>
    /// with no usable sighting, <see cref="OperationalOptions.SeenAttackForceRatio"/> with a fresh confident one.</summary>
    public static double RequiredForceRatio(EnemyFeatures enemy, double baseSeenAgeSeconds, OperationalOptions options)
    {
        double w = EvidenceWeight(enemy, baseSeenAgeSeconds, options);
        return options.SeenAttackForceRatio + ((options.MinAttackForceRatio - options.SeenAttackForceRatio) * (1 - w));
    }
}
