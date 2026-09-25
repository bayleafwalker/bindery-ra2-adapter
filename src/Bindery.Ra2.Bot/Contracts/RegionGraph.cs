// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot;

/// <summary>
/// Shortest paths over a map's region links. Shared by features (threat ETA,
/// reinforcement time), operations (squad routing) and the simulator (movement).
/// </summary>
public sealed class RegionGraph
{
    private readonly Dictionary<RegionId, List<(RegionId To, double Distance)>> ground = [];
    private readonly Dictionary<RegionId, Dictionary<RegionId, double>> distanceCache = [];

    public RegionGraph(MapInfo map)
    {
        ArgumentNullException.ThrowIfNull(map);
        Map = map;
        foreach (Region region in map.Regions) ground[region.Id] = [];
        foreach (RegionLink link in map.Links)
        {
            if (!link.Ground || !ground.ContainsKey(link.A) || !ground.ContainsKey(link.B)) continue;
            ground[link.A].Add((link.B, link.Distance));
            ground[link.B].Add((link.A, link.Distance));
        }
    }

    public MapInfo Map { get; }

    public IReadOnlyList<RegionId> Neighbours(RegionId region) =>
        ground.TryGetValue(region, out List<(RegionId To, double Distance)>? edges) ? edges.Select(static e => e.To).ToList() : [];

    /// <summary>Ground distance in cells, or <see cref="double.PositiveInfinity"/> when unreachable.</summary>
    public double Distance(RegionId from, RegionId to) =>
        DistancesFrom(from).TryGetValue(to, out double d) ? d : double.PositiveInfinity;

    /// <summary>Travel time in seconds at <paramref name="cellsPerSecond"/>; infinity when unreachable or immobile.</summary>
    public double TravelSeconds(RegionId from, RegionId to, double cellsPerSecond) =>
        cellsPerSecond <= 0 ? double.PositiveInfinity : Distance(from, to) / cellsPerSecond;

    /// <summary>Regions along the shortest ground path, inclusive of both ends; empty when unreachable.</summary>
    public IReadOnlyList<RegionId> Path(RegionId from, RegionId to)
    {
        if (!ground.ContainsKey(from) || !ground.ContainsKey(to)) return [];
        Dictionary<RegionId, double> best = new() { [from] = 0 };
        Dictionary<RegionId, RegionId> previous = [];
        PriorityQueue<RegionId, double> frontier = new();
        frontier.Enqueue(from, 0);
        while (frontier.TryDequeue(out RegionId current, out double distance))
        {
            if (distance > best[current]) continue;
            if (current == to) break;
            foreach ((RegionId next, double step) in ground[current])
            {
                double candidate = distance + step;
                if (best.TryGetValue(next, out double known) && known <= candidate) continue;
                best[next] = candidate;
                previous[next] = current;
                frontier.Enqueue(next, candidate);
            }
        }
        if (!best.ContainsKey(to)) return [];
        List<RegionId> path = [to];
        for (RegionId cursor = to; cursor != from; cursor = previous[cursor]) path.Add(previous[cursor]);
        path.Reverse();
        return path;
    }

    public IReadOnlyDictionary<RegionId, double> DistancesFrom(RegionId from)
    {
        if (distanceCache.TryGetValue(from, out Dictionary<RegionId, double>? cached)) return cached;
        Dictionary<RegionId, double> best = [];
        if (ground.ContainsKey(from))
        {
            best[from] = 0;
            PriorityQueue<RegionId, double> frontier = new();
            frontier.Enqueue(from, 0);
            while (frontier.TryDequeue(out RegionId current, out double distance))
            {
                if (distance > best[current]) continue;
                foreach ((RegionId next, double step) in ground[current])
                {
                    double candidate = distance + step;
                    if (best.TryGetValue(next, out double known) && known <= candidate) continue;
                    best[next] = candidate;
                    frontier.Enqueue(next, candidate);
                }
            }
        }
        distanceCache[from] = best;
        return best;
    }
}
