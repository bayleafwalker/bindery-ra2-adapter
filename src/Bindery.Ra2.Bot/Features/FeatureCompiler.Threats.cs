// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Features;

public sealed partial class FeatureCompiler
{
    /// <summary>
    /// One assessment per region that holds an own building or an own force
    /// cluster, per spec. <see cref="ThreatAssessment.OwnValue"/> is the
    /// player's total mobile army value (not range-limited): with
    /// <see cref="ThreatAssessment.ReinforcementSeconds"/> reported
    /// separately, a consumer can already tell how much of that value can
    /// actually arrive in time.
    /// </summary>
    private List<ThreatAssessment> CompileThreats(
        BeliefSnapshot snapshot, RegionGraph graph, IReadOnlyList<ForceCluster> clusters, double ownArmyValue)
    {
        List<RegionId> baseRegions = BaseRegions(snapshot);
        HashSet<RegionId> baseRegionSet = [.. baseRegions];
        HashSet<RegionId> regionsOfInterest = [.. baseRegionSet, .. clusters.Select(static c => c.Region)];

        // Only mobile combat units threaten a region: an enemy base that happens to lie within the search radius
        // is not an incoming attack, and counting its buildings would read as a permanent base threat.
        List<EnemyContact> alive = [.. snapshot.Enemies.Where(static c => !c.ConfirmedDestroyed && c.Kind != EntityKind.Building && CombatRoles.Contains(c.Role))];

        List<ThreatAssessment> result = [];
        foreach (RegionId region in regionsOfInterest.OrderBy(static r => r.Value))
        {
            List<EnemyContact> threatening = [.. alive.Where(c => graph.Distance(c.LastSeenRegion, region) <= options.ThreatSearchCells)];

            double enemyValue = threatening.Sum(static c => c.Value * c.Confidence);
            double localForceRatio = enemyValue <= 0 ? 10.0 : ownArmyValue / enemyValue;

            double enemyEta = threatening.Count == 0
                ? UnknownSeconds
                : Math.Min(UnknownSeconds, threatening.Min(c => graph.TravelSeconds(c.LastSeenRegion, region, options.SlowestTypicalSpeed)));

            // Ties on ETA break by contact id so the chosen path is deterministic.
            EnemyContact? nearest = threatening
                .OrderBy(c => graph.TravelSeconds(c.LastSeenRegion, region, options.SlowestTypicalSpeed))
                .ThenBy(static c => c.Id.Value)
                .FirstOrDefault();
            IReadOnlyList<RegionId> attackPath = nearest is null ? [] : graph.Path(nearest.LastSeenRegion, region);

            double reinforcement = clusters.Count == 0
                ? UnknownSeconds
                : Math.Min(UnknownSeconds, clusters.Min(c => c.Region == region ? 0.0 : graph.TravelSeconds(c.Region, region, options.SlowestTypicalSpeed)));

            double weighted = threatening.Sum(static c => c.Value * c.Confidence);
            double totalValue = threatening.Sum(static c => c.Value);
            double confidence = threatening.Count == 0
                ? 1.0
                : totalValue > 0 ? weighted / totalValue : threatening.Average(static c => c.Confidence);

            result.Add(new ThreatAssessment(
                region, enemyValue, ownArmyValue, localForceRatio, enemyEta, reinforcement,
                baseRegionSet.Contains(region), confidence, attackPath));
        }
        return result;
    }
}
