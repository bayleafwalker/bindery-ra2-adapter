// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Tests.Belief;

/// <summary>Small fixed maps shared by Belief and Features tests.</summary>
internal static class TestMaps
{
    public static readonly RegionId Home = new(0);
    public static readonly RegionId Middle = new(1);
    public static readonly RegionId EnemyStart = new(2);

    /// <summary>Two start locations either side of one ore-bearing middle region, linked in a line.</summary>
    public static MapInfo Simple()
    {
        Region home = new(Home, "Home", new Cell(10, 10), 8, IsStartLocation: true, HasOre: false, Water: false);
        Region middle = new(Middle, "Middle", new Cell(50, 50), 10, IsStartLocation: false, HasOre: true, Water: false);
        Region enemy = new(EnemyStart, "Enemy Start", new Cell(90, 90), 8, IsStartLocation: true, HasOre: false, Water: false);

        RegionLink a = new(home.Id, middle.Id, 40, Ground: true, Naval: false);
        RegionLink b = new(middle.Id, enemy.Id, 40, Ground: true, Naval: false);

        return new MapInfo(
            "test-map", 100, 100,
            [home, middle, enemy],
            [a, b],
            [new OreField(Middle, middle.Center, 10000, Gems: false)]);
    }
}
