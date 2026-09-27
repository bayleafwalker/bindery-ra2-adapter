// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Sim;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Sim;

public sealed class SimMapsTests
{
    [Fact]
    public void There_are_five_maps_split_three_training_two_held_out_with_no_overlap()
    {
        Assert.Equal(5, SimMaps.All.Count);
        Assert.Equal(3, SimMaps.Training.Count);
        Assert.Equal(2, SimMaps.HeldOut.Count);
        Assert.Empty(SimMaps.Training.Select(m => m.Map.MapId).Intersect(SimMaps.HeldOut.Select(m => m.Map.MapId)));
    }

    [Theory]
    [MemberData(nameof(AllMaps))]
    public void Every_map_has_two_start_regions_ore_and_is_fully_connected(SimMap map)
    {
        Assert.InRange(map.Map.Regions.Count, 8, 14);
        Assert.Equal(2, map.StartRegions.Count);
        Assert.All(map.StartRegions, id => Assert.Contains(map.Map.Regions, r => r.Id == id && r.IsStartLocation));
        Assert.NotEmpty(map.Map.OreFields);
        AssertFullyConnected(map);
    }

    // The arm always starts in the first start region, so a map must give both starts the same economy and the
    // same roads: equal distances to every ore field (sorted) and to the enemy start. Otherwise a win rate carries a
    // positional edge the arm did not earn.
    [Theory]
    [MemberData(nameof(AllMaps))]
    public void Both_starts_have_the_same_distances_to_ore_and_to_each_other(SimMap map)
    {
        RegionGraph graph = new(map.Map);
        IReadOnlyDictionary<RegionId, double> fromWest = graph.DistancesFrom(map.StartRegions[0]);
        IReadOnlyDictionary<RegionId, double> fromEast = graph.DistancesFrom(map.StartRegions[1]);
        double[] OreDistances(IReadOnlyDictionary<RegionId, double> d) =>
            [.. map.Map.OreFields.Select(f => Math.Round(d.TryGetValue(f.Region, out double v) ? v : double.PositiveInfinity, 3)).Order()];
        int[] OreValues(IReadOnlyDictionary<RegionId, double> d) =>
            [.. map.Map.OreFields.OrderBy(f => d.TryGetValue(f.Region, out double v) ? v : double.PositiveInfinity).ThenBy(static f => f.InitialValue).Select(static f => f.InitialValue)];

        Assert.Equal(OreDistances(fromWest), OreDistances(fromEast));
        Assert.Equal(OreValues(fromWest), OreValues(fromEast));
        Assert.Equal(fromWest[map.StartRegions[1]], fromEast[map.StartRegions[0]]);
    }

    // Graph distances are not what the simulator moves by: units travel in straight lines between region centres,
    // and placement, harvesting and combat use cells. Twin-valley passed the graph test with its centre at x=55 and
    // its lanes at x=45 and x=75, and the west start won 37 of 48 mirror matches. Each map must be its own mirror
    // image: some reflection swaps the two starts and maps every region (centre, radius, flags), link and ore field
    // onto one of the same.
    [Theory]
    [MemberData(nameof(AllMaps))]
    public void Each_map_is_a_geometric_mirror_image_that_swaps_the_starts(SimMap map)
    {
        Cell a = map.Map.Regions.Single(r => r.Id == map.StartRegions[0]).Center;
        Cell b = map.Map.Regions.Single(r => r.Id == map.StartRegions[1]).Center;
        List<(string Name, Func<Cell, Cell> Map)> reflections =
        [
            ("vertical axis", c => new Cell(a.X + b.X - c.X, c.Y)),
            ("horizontal axis", c => new Cell(c.X, a.Y + b.Y - c.Y)),
            ("point", c => new Cell(a.X + b.X - c.X, a.Y + b.Y - c.Y)),
            ("diagonal", c => new Cell(c.Y + (b.X - a.Y), c.X - (b.X - a.Y))),
            ("anti-diagonal", c => new Cell(a.X + b.Y - c.Y, a.Y + b.X - c.X)),
        ];
        List<string> failures = [];
        foreach ((string name, Func<Cell, Cell> reflect) in reflections)
        {
            if (reflect(a) != b || reflect(b) != a) continue;
            if (MirrorFailure(map.Map, reflect) is not { } failure) return;
            failures.Add($"{name}: {failure}");
        }
        Assert.Fail($"{map.Map.MapId} has no reflection that swaps the starts: {string.Join("; ", failures)}");
    }

    private static string? MirrorFailure(MapInfo map, Func<Cell, Cell> reflect)
    {
        Dictionary<RegionId, RegionId> image = [];
        foreach (Region region in map.Regions)
        {
            Cell target = reflect(region.Center);
            Region? twin = map.Regions.FirstOrDefault(r => r.Center == target);
            if (twin is null) return $"region {region.Name} at {region.Center} has no twin at {target}";
            if (twin.Radius != region.Radius || twin.HasOre != region.HasOre || twin.Water != region.Water || twin.IsStartLocation != region.IsStartLocation)
                return $"region {region.Name} and its twin {twin.Name} differ";
            image[region.Id] = twin.Id;
        }
        foreach (RegionLink link in map.Links)
        {
            RegionId ia = image[link.A], ib = image[link.B];
            if (!map.Links.Any(l => ((l.A == ia && l.B == ib) || (l.A == ib && l.B == ia))
                    && Math.Abs(l.Distance - link.Distance) < 1e-9 && l.Ground == link.Ground && l.Naval == link.Naval))
                return $"link {link.A.Value}-{link.B.Value} has no twin";
        }
        foreach (OreField field in map.OreFields)
        {
            Cell target = reflect(field.Center);
            if (!map.OreFields.Any(o => o.Center == target && o.Region == image[field.Region] && o.InitialValue == field.InitialValue && o.Gems == field.Gems))
                return $"ore field at {field.Center} has no twin at {target}";
        }
        return null;
    }

    // Full connectivity (checked below, from one start region over an undirected link graph) already
    // implies every region is reachable from both start regions using some combination of ground and
    // naval links; an "island" region reachable only by sea is fine, one reachable by neither is a map bug.
    private static void AssertFullyConnected(SimMap map)
    {
        HashSet<RegionId> reachable = ReachableRegions(map, map.StartRegions[0]);
        Assert.Equal(map.Map.Regions.Count, reachable.Count);
    }

    private static HashSet<RegionId> ReachableRegions(SimMap map, RegionId from)
    {
        Dictionary<RegionId, List<RegionId>> adjacency = map.Map.Regions.ToDictionary(r => r.Id, _ => new List<RegionId>());
        foreach (RegionLink link in map.Map.Links)
        {
            adjacency[link.A].Add(link.B);
            adjacency[link.B].Add(link.A);
        }
        HashSet<RegionId> visited = [from];
        Queue<RegionId> frontier = new([from]);
        while (frontier.TryDequeue(out RegionId current))
        {
            foreach (RegionId next in adjacency[current])
            {
                if (visited.Add(next)) frontier.Enqueue(next);
            }
        }
        return visited;
    }

    public static IEnumerable<object[]> AllMaps() => SimMaps.All.Select(m => new object[] { m });
}
