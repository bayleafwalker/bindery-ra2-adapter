// SPDX-License-Identifier: GPL-3.0-or-later
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Sim;

/// <summary>
/// Equal-length routes must resolve the same way whatever order the map declares its links in, and the same way on
/// both halves of a mirrored map. Dijkstra kept whichever predecessor the priority queue happened to settle first, so
/// on twin-valley a west scout took the south lane and an east scout the north lane, and the west start won 35 of 48
/// mirror matches.
/// </summary>
public sealed class RegionGraphPathTests
{
    // A diamond with a tail on each side: 0 - {1, 2} - 3 - {4, 5} - 6, every link the same length.
    private static MapInfo Diamond(bool reversedLinks)
    {
        Region[] regions = [.. Enumerable.Range(0, 7).Select(static i => new Region(new RegionId(i), $"r{i}", new Cell(i * 10, 50), 3, i is 0 or 6, false, false))];
        RegionLink[] links =
        [
            L(0, 1), L(0, 2), L(1, 3), L(2, 3), L(3, 4), L(3, 5), L(4, 6), L(5, 6),
        ];
        return new MapInfo("diamond", 100, 100, regions, reversedLinks ? [.. links.Reverse()] : links, []);

        static RegionLink L(int a, int b) => new(new RegionId(a), new RegionId(b), 10, true, false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Equal_routes_go_through_the_lowest_numbered_region_whatever_the_link_order(bool reversedLinks)
    {
        RegionGraph graph = new(Diamond(reversedLinks));

        Assert.Equal([0, 1, 3, 4, 6], graph.Path(new RegionId(0), new RegionId(6)).Select(static r => r.Value));
        Assert.Equal([6, 4, 3, 1, 0], graph.Path(new RegionId(6), new RegionId(0)).Select(static r => r.Value));
    }
}
