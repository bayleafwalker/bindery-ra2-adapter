// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
namespace Bindery.Ra2.Bot.Features;

public sealed partial class FeatureCompiler
{
    /// <summary>Confidence above which a stale enemy contact still counts as "present" for map control.</summary>
    private const double PresenceConfidenceThreshold = 0.2;

    private MapControlFeatures CompileMapControl(BeliefSnapshot snapshot)
    {
        HashSet<RegionId> ownPresent = [.. snapshot.Own.Select(static e => e.Region)];
        HashSet<RegionId> enemyPresent = [.. snapshot.Enemies
            .Where(c => !c.ConfirmedDestroyed && c.Confidence >= PresenceConfidenceThreshold)
            .Select(static c => c.LastSeenRegion)];

        Dictionary<RegionId, RegionControl> control = [];
        foreach (Region region in snapshot.Map.Regions)
        {
            bool own = ownPresent.Contains(region.Id);
            bool enemy = enemyPresent.Contains(region.Id);
            control[region.Id] = (own, enemy) switch
            {
                (true, true) => RegionControl.Contested,
                (true, false) => RegionControl.Own,
                (false, true) => RegionControl.Enemy,
                _ => snapshot.RegionLastSeen.ContainsKey(region.Id) ? RegionControl.Neutral : RegionControl.Unknown,
            };
        }

        // An ore field is taken by the enemy only when it holds an enemy building; a unit passing through makes
        // the region Enemy-controlled for the moment but does not take the field away as an expansion.
        HashSet<RegionId> enemyHeld = EnemyBuildingRegions(snapshot);
        List<Region> oreRegions = [.. snapshot.Map.Regions.Where(static r => r.HasOre)];
        List<RegionId> expansionCandidates = [.. oreRegions
            .Where(r => !enemyHeld.Contains(r.Id))
            .Select(r => (Region: r, Distance: DistanceFromBase(snapshot, r.Id)))
            .OrderBy(static t => t.Distance)
            .ThenBy(static t => t.Region.Id.Value)
            .Select(static t => t.Region.Id)];

        double ownedOreFraction = oreRegions.Count == 0
            ? 0.0
            : oreRegions.Count(r => control[r.Id] == RegionControl.Own) / (double)oreRegions.Count;

        return new MapControlFeatures(control, expansionCandidates, ownedOreFraction);
    }

    /// <summary>Regions holding a live enemy building remembered with at least presence confidence.</summary>
    private static HashSet<RegionId> EnemyBuildingRegions(BeliefSnapshot snapshot) =>
        [.. snapshot.Enemies
            .Where(static c => c.Kind == EntityKind.Building && !c.ConfirmedDestroyed && c.Confidence >= PresenceConfidenceThreshold)
            .Select(static c => c.LastSeenRegion)];

    private static List<RegionId> BaseRegions(BeliefSnapshot snapshot) =>
        [.. snapshot.Own.Where(static e => e.Kind == EntityKind.Building).Select(static e => e.Region).Distinct().OrderBy(static r => r.Value)];

    private double DistanceFromBase(BeliefSnapshot snapshot, RegionId target)
    {
        List<RegionId> bases = BaseRegions(snapshot);
        if (bases.Count == 0) return double.PositiveInfinity;
        RegionGraph graph = GraphFor(snapshot.Map);
        double best = double.PositiveInfinity;
        foreach (RegionId b in bases)
        {
            double d = graph.Distance(b, target);
            if (d < best) best = d;
        }
        return best;
    }

    private ScoutingFeatures CompileScouting(BeliefSnapshot snapshot, EnemyFeatures enemy)
    {
        int total = snapshot.Map.Regions.Count;
        int covered = 0;
        Dictionary<RegionId, double> ages = [];
        foreach (Region region in snapshot.Map.Regions)
        {
            if (snapshot.RegionLastSeen.TryGetValue(region.Id, out GameTime lastSeen))
            {
                double age = snapshot.Time.SecondsSince(lastSeen);
                ages[region.Id] = age;
                if (age <= options.ScoutingWindowSeconds) covered++;
            }
            else
            {
                ages[region.Id] = UnknownSeconds;
            }
        }
        double coverage = total == 0 ? 0.0 : covered / (double)total;

        List<string> unknowns = [];
        bool pastGrace = snapshot.Time.Seconds >= options.EnemyStartUnscoutedGraceSeconds;
        foreach (EnemyPlayerBelief p in snapshot.EnemyPlayers.OrderBy(static p => p.Player.Value))
        {
            if (pastGrace && p.SuspectedStart is null) unknowns.Add($"enemy {p.Player} start unscouted");

            double lastSeenAge = p.LastSeenAnything is { } t ? snapshot.Time.SecondsSince(t) : UnknownSeconds;
            if (lastSeenAge >= options.EnemyArmyUnseenThresholdSeconds)
                unknowns.Add(string.Create(CultureInfo.InvariantCulture, $"enemy {p.Player} army not seen for {options.EnemyArmyUnseenThresholdSeconds:0}s"));
            if (lastSeenAge >= options.EnemyTechUnknownThresholdSeconds)
                unknowns.Add(string.Create(CultureInfo.InvariantCulture, $"enemy {p.Player} tech unknown for {options.EnemyTechUnknownThresholdSeconds:0}s"));
        }

        return new ScoutingFeatures(coverage, ages, unknowns);
    }
}
