// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Features;

public sealed partial class FeatureCompiler
{
    private EnemyFeatures CompileEnemy(BeliefSnapshot snapshot, out double armyValueCurrent)
    {
        List<EnemyContact> alive = [.. snapshot.Enemies.Where(static c => !c.ConfirmedDestroyed)];

        double weightedValue = 0, totalValue = 0;
        Dictionary<UnitRole, double> composition = [];
        foreach (EnemyContact c in alive)
        {
            double contribution = c.Value * c.Confidence;
            weightedValue += contribution;
            totalValue += c.Value;
            composition[c.Role] = composition.GetValueOrDefault(c.Role) + contribution;
        }
        armyValueCurrent = weightedValue;

        double confidence = alive.Count == 0
            ? 1.0
            : totalValue > 0
                ? weightedValue / totalValue
                : alive.Average(static c => c.Confidence);

        HashSet<string> knownTech = new(StringComparer.Ordinal);
        foreach (EnemyPlayerBelief p in snapshot.EnemyPlayers) knownTech.UnionWith(p.SeenTech);

        List<string> knownProduction = [.. knownTech
            .Where(t => rules.TryGet(t, out UnitRule r) && r.Role == UnitRole.Production)
            .OrderBy(static t => t, StringComparer.Ordinal)];

        double newestAge = UnknownSeconds, medianAge = UnknownSeconds;
        if (alive.Count > 0)
        {
            List<double> ages = [.. alive.Select(c => snapshot.Time.SecondsSince(c.LastSeenAt)).OrderBy(static a => a)];
            newestAge = ages[0];
            medianAge = ages[ages.Count / 2];
        }

        bool superweaponBuildingSeen = knownTech.Any(t => rules.TryGet(t, out UnitRule r) && r.Role == UnitRole.Superweapon);
        bool superweaponKnown = superweaponBuildingSeen || superweaponEverLaunched;

        return new EnemyFeatures(
            Trend.Flat(armyValueCurrent), confidence, composition, knownTech, knownProduction,
            newestAge, medianAge, superweaponKnown);
    }
}
