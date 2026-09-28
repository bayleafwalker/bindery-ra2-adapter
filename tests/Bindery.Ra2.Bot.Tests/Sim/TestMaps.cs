// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Sim;

namespace Bindery.Ra2.Bot.Tests.Sim;

/// <summary>Small, purpose-built maps for focused Sim tests (distinct from the five authored <see cref="SimMaps"/>).</summary>
internal static class TestMaps
{
    /// <summary>Two start regions plus one shared ore region and one shared combat region, all one hop apart.</summary>
    public static SimMap TwoPlayerCombat()
    {
        Region[] regions =
        [
            new(new RegionId(0), "start-a", new Cell(0, 0), 5, IsStartLocation: true, HasOre: false, Water: false),
            new(new RegionId(1), "ore", new Cell(10, 0), 5, IsStartLocation: false, HasOre: true, Water: false),
            new(new RegionId(2), "battlefield", new Cell(20, 0), 5, IsStartLocation: false, HasOre: false, Water: false),
            new(new RegionId(3), "start-b", new Cell(30, 0), 5, IsStartLocation: true, HasOre: false, Water: false),
        ];
        RegionLink[] links =
        [
            new(new RegionId(0), new RegionId(1), 10, Ground: true, Naval: false),
            new(new RegionId(1), new RegionId(2), 10, Ground: true, Naval: false),
            new(new RegionId(2), new RegionId(3), 10, Ground: true, Naval: false),
        ];
        OreField[] ore = [new(new RegionId(1), new Cell(10, 0), 20_000, Gems: false)];
        MapInfo map = new("test-combat", 40, 10, regions, links, ore);
        return new SimMap(map, [new RegionId(0), new RegionId(3)]);
    }

    /// <summary>
    /// Two players, but the map defines no regions at all: every cell, including any rally point, is outside
    /// every region. For testing the spawn fallback when <see cref="MapInfo.RegionOf"/> legitimately finds nothing.
    /// </summary>
    public static SimMap NoRegions() =>
        new(new MapInfo("test-no-regions", 40, 40, [], [], []), [new RegionId(0), new RegionId(1)]);
}
