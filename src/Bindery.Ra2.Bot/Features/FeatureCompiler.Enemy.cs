// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Features;

public sealed partial class FeatureCompiler
{
    private EnemyFeatures CompileEnemy(BeliefSnapshot snapshot, out double armyValueCurrent)
    {
        List<EnemyContact> alive = [.. snapshot.Enemies.Where(static c => !c.ConfirmedDestroyed)];

        // The estimate uses the same definition as own army value (mobile units in combat roles), so the
        // two are comparable in ratios; enemy buildings and harvesters are economy, not force.
        List<EnemyContact> army = [.. alive.Where(static c => c.Kind != EntityKind.Building && CombatRoles.Contains(c.Role))];
        double weightedValue = 0, totalValue = 0;
        Dictionary<UnitRole, double> composition = [];
        foreach (EnemyContact c in army)
        {
            double contribution = c.Value * c.Confidence;
            weightedValue += contribution;
            totalValue += c.Value;
            composition[c.Role] = composition.GetValueOrDefault(c.Role) + contribution;
        }
        armyValueCurrent = weightedValue;

        // No army contact at all is no evidence: confidence 0, not certainty that the enemy has nothing.
        double confidence = army.Count == 0
            ? 0.0
            : totalValue > 0
                ? weightedValue / totalValue
                : army.Average(static c => c.Confidence);

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

        // Age per item: a tech seen by several enemy players counts its newest sighting.
        SortedDictionary<string, double> techAges = new(StringComparer.Ordinal);
        foreach (EnemyPlayerBelief p in snapshot.EnemyPlayers)
        {
            foreach (string tech in p.SeenTech)
            {
                double age = p.TechLastSeen is { } times && times.TryGetValue(tech, out GameTime seen)
                    ? snapshot.Time.SecondsSince(seen)
                    : UnknownSeconds;
                techAges[tech] = techAges.TryGetValue(tech, out double other) ? Math.Min(other, age) : age;
            }
        }

        return new EnemyFeatures(
            Trend.Flat(armyValueCurrent), confidence, composition, knownTech, knownProduction,
            newestAge, medianAge, superweaponKnown, techAges);
    }
}
