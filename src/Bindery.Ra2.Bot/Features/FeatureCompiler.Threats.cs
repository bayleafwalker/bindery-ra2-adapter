// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Features;

public sealed partial class FeatureCompiler
{
    /// <summary>
    /// One assessment per region that holds an own building or an own force
    /// cluster, per spec. Both sides are measured the same way: enemy value is
    /// the contacts within <see cref="FeatureOptions.ThreatSearchCells"/> of
    /// ground travel, and <see cref="ThreatAssessment.OwnValue"/> is the own
    /// force clusters within the same radius, the value that can respond. The
    /// whole army's value would hide a base being razed while the army is
    /// across the map (it stays in <see cref="ArmyFeatures.ArmyValue"/>).
    ///
    /// A contact whose nearest region centre is water (a tank on a river bank,
    /// a ship offshore) is placed in the nearest land region for this purpose:
    /// the region graph has ground links only, so from the water region it
    /// would be unreachable and silently dropped from every assessment.
    /// </summary>
    private List<ThreatAssessment> CompileThreats(
        BeliefSnapshot snapshot, RegionGraph graph, IReadOnlyList<ForceCluster> clusters)
    {
        List<RegionId> baseRegions = BaseRegions(snapshot);
        HashSet<RegionId> baseRegionSet = [.. baseRegions];
        HashSet<RegionId> regionsOfInterest = [.. baseRegionSet, .. clusters.Select(static c => c.Region)];

        // Only mobile combat units threaten a region: an enemy base that happens to lie within the search radius
        // is not an incoming attack, and counting its buildings would read as a permanent base threat.
        List<EnemyContact> alive = [.. snapshot.Enemies.Where(static c => !c.ConfirmedDestroyed && c.Kind != EntityKind.Building && CombatRoles.Contains(c.Role))];

        Dictionary<EntityId, RegionId> from = alive.ToDictionary(static c => c.Id, c => ThreatRegionOf(snapshot.Map, c));

        List<ThreatAssessment> result = [];
        foreach (RegionId region in regionsOfInterest.OrderBy(static r => r.Value))
        {
            List<EnemyContact> threatening = [.. alive.Where(c => graph.Distance(from[c.Id], region) <= options.ThreatSearchCells)];

            double enemyValue = threatening.Sum(static c => c.Value * c.Confidence);
            double ownValue = clusters.Where(c => graph.Distance(c.Region, region) <= options.ThreatSearchCells).Sum(static c => c.Value);
            double localForceRatio = enemyValue <= 0 ? 10.0 : ownValue / enemyValue;

            double enemyEta = threatening.Count == 0
                ? UnknownSeconds
                : Math.Min(UnknownSeconds, threatening.Min(c => graph.TravelSeconds(from[c.Id], region, options.SlowestTypicalSpeed)));

            // Ties on ETA break by contact id so the chosen path is deterministic.
            EnemyContact? nearest = threatening
                .OrderBy(c => graph.TravelSeconds(from[c.Id], region, options.SlowestTypicalSpeed))
                .ThenBy(static c => c.Id.Value)
                .FirstOrDefault();
            IReadOnlyList<RegionId> attackPath = nearest is null ? [] : graph.Path(from[nearest.Id], region);

            double reinforcement = clusters.Count == 0
                ? UnknownSeconds
                : Math.Min(UnknownSeconds, clusters.Min(c => c.Region == region ? 0.0 : graph.TravelSeconds(c.Region, region, options.SlowestTypicalSpeed)));

            double weighted = threatening.Sum(static c => c.Value * c.Confidence);
            double totalValue = threatening.Sum(static c => c.Value);
            double confidence = threatening.Count == 0
                ? 1.0
                : totalValue > 0 ? weighted / totalValue : threatening.Average(static c => c.Confidence);

            result.Add(new ThreatAssessment(
                region, enemyValue, ownValue, localForceRatio, enemyEta, reinforcement,
                baseRegionSet.Contains(region), confidence, attackPath));
        }
        return result;
    }

    /// <summary>
    /// The region a contact threatens from: its last-seen region, or, when that is water, the land region whose
    /// centre is nearest its last-seen cell (ties by id). A map with no land region keeps the water region.
    /// </summary>
    private static RegionId ThreatRegionOf(MapInfo map, EnemyContact contact)
    {
        Region? seen = map.Regions.FirstOrDefault(r => r.Id == contact.LastSeenRegion);
        if (seen is not { Water: true }) return contact.LastSeenRegion;
        Region? land = map.Regions
            .Where(static r => !r.Water)
            .OrderBy(r => r.Center.DistanceTo(contact.LastSeenPosition))
            .ThenBy(static r => r.Id.Value)
            .FirstOrDefault();
        return land?.Id ?? contact.LastSeenRegion;
    }
}
