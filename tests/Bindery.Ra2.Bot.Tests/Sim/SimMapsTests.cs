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
