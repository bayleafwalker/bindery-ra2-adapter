// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Sim;

/// <summary>
/// A map for the bindery region sim, not retail RA2: <see cref="Map"/> is the
/// static, shroud-independent knowledge every player has before the match,
/// plus which region each player's MCV starts in.
/// </summary>
public sealed record SimMap(MapInfo Map, IReadOnlyList<RegionId> StartRegions);

/// <summary>
/// The five authored 2-player maps used to develop and evaluate the bot
/// offline. Region layouts and distances are hand-picked approximations of
/// RA2 map archetypes (twin lanes, a river crossing, island hopping, an open
/// field, and a fortified choke), not conversions of retail maps.
/// </summary>
public static class SimMaps
{
    public static SimMap TwinValley { get; } = BuildTwinValley();

    public static SimMap RiverCrossing { get; } = BuildRiverCrossing();

    public static SimMap IslandBridges { get; } = BuildIslandBridges();

    public static SimMap OpenSteppe { get; } = BuildOpenSteppe();

    public static SimMap FortressChoke { get; } = BuildFortressChoke();

    /// <summary>Maps used for development and training-time evaluation.</summary>
    public static IReadOnlyList<SimMap> Training { get; } = [TwinValley, RiverCrossing, IslandBridges];

    /// <summary>Maps reserved for held-out evaluation; never used while developing strategists.</summary>
    public static IReadOnlyList<SimMap> HeldOut { get; } = [OpenSteppe, FortressChoke];

    public static IReadOnlyList<SimMap> All { get; } = [.. Training, .. HeldOut];

    private static Region R(int id, string name, int x, int y, int radius, bool start = false, bool ore = false, bool water = false) =>
        new(new RegionId(id), name, new Cell(x, y), radius, start, ore, water);

    private static RegionLink L(int a, int b, double distance, bool ground = true, bool naval = false) =>
        new(new RegionId(a), new RegionId(b), distance, ground, naval);

    private static OreField Ore(int region, int x, int y, int value = 12_000, bool gems = false) =>
        new(new RegionId(region), new Cell(x, y), value, gems);

    private static SimMap Build(string id, int width, int height, IReadOnlyList<Region> regions, IReadOnlyList<RegionLink> links, IReadOnlyList<OreField> ore, int start0, int start1)
    {
        MapInfo map = new(id, width, height, regions, links, ore);
        return new SimMap(map, [new RegionId(start0), new RegionId(start1)]);
    }

    // 8 regions: two symmetric lanes meeting at a contested, ore-rich centre.
    private static SimMap BuildTwinValley()
    {
        Region[] regions =
        [
            R(0, "start-west", 10, 50, 8, start: true),
            R(1, "ore-west", 25, 50, 6, ore: true),
            R(2, "lane-north", 45, 25, 7),
            R(3, "lane-south", 45, 75, 7),
            R(4, "centre", 55, 50, 9, ore: true),
            R(5, "lane-north-east", 75, 25, 7),
            R(6, "lane-south-east", 75, 75, 7),
            R(7, "start-east", 90, 50, 8, start: true),
        ];
        RegionLink[] links =
        [
            L(0, 1, 15), L(1, 2, 26), L(1, 3, 26), L(2, 4, 22), L(3, 4, 22),
            L(4, 5, 22), L(4, 6, 22), L(5, 7, 26), L(6, 7, 26),
        ];
        OreField[] ore = [Ore(1, 25, 50), Ore(4, 55, 50, 16_000)];
        return Build("twin-valley", 100, 100, regions, links, ore, 0, 7);
    }

    // 10 regions: a central river forces two crossings between symmetric halves.
    private static SimMap BuildRiverCrossing()
    {
        Region[] regions =
        [
            R(0, "start-west", 8, 50, 8, start: true),
            R(1, "ore-west", 22, 30, 6, ore: true),
            R(2, "field-west", 22, 70, 7),
            R(3, "bank-west", 42, 50, 7),
            R(4, "river", 50, 50, 5, water: true),
            R(5, "bank-east", 58, 50, 7),
            R(6, "ford-north", 50, 20, 5),
            R(7, "field-east", 78, 30, 7),
            R(8, "ore-east", 78, 70, 6, ore: true),
            R(9, "start-east", 92, 50, 8, start: true),
        ];
        RegionLink[] links =
        [
            L(0, 1, 18), L(0, 2, 18), L(1, 3, 20), L(2, 3, 20),
            L(3, 4, 10, ground: false, naval: true), L(4, 5, 10, ground: false, naval: true),
            L(3, 6, 24), L(6, 5, 24),
            L(5, 7, 20), L(5, 8, 20), L(7, 9, 18), L(8, 9, 18),
        ];
        OreField[] ore = [Ore(1, 22, 30), Ore(8, 78, 70)];
        return Build("river-crossing", 100, 100, regions, links, ore, 0, 9);
    }

    // 10 regions: a chain of islands linked by naval crossings.
    private static SimMap BuildIslandBridges()
    {
        Region[] regions =
        [
            R(0, "home-west", 10, 50, 8, start: true),
            R(1, "ore-west", 15, 25, 6, ore: true),
            R(2, "dock-west", 25, 50, 6),
            R(3, "strait-west", 40, 50, 4, water: true),
            R(4, "isle-centre", 50, 50, 8, ore: true),
            R(5, "isle-north", 50, 20, 6, ore: true),
            R(6, "strait-east", 60, 50, 4, water: true),
            R(7, "dock-east", 75, 50, 6),
            R(8, "ore-east", 85, 25, 6, ore: true),
            R(9, "home-east", 90, 50, 8, start: true),
        ];
        RegionLink[] links =
        [
            // The straits are crossed by bridges (ground and naval): without them the two sides have no ground
            // route to each other, and the fixture has no naval units, so every match could only time out.
            L(0, 1, 14), L(0, 2, 14), L(2, 3, 8, ground: true, naval: true),
            L(3, 4, 8, ground: true, naval: true), L(4, 5, 18),
            L(4, 6, 8, ground: true, naval: true), L(6, 7, 8, ground: true, naval: true),
            L(7, 8, 20), L(7, 9, 14), L(9, 8, 14),
        ];
        OreField[] ore = [Ore(1, 15, 25), Ore(4, 50, 50, 14_000), Ore(5, 50, 20), Ore(8, 85, 25)];
        return Build("island-bridges", 100, 100, regions, links, ore, 0, 9);
    }

    // 12 regions: a wide, mostly-open steppe with many alternate routes.
    private static SimMap BuildOpenSteppe()
    {
        Region[] regions =
        [
            R(0, "start-west", 8, 50, 8, start: true),
            R(1, "flank-nw", 20, 15, 7),
            R(2, "ore-nw", 30, 30, 6, ore: true),
            R(3, "mid-north", 50, 20, 7),
            R(4, "flank-sw", 20, 85, 7),
            R(5, "ore-sw", 30, 70, 6, ore: true),
            R(6, "mid-south", 50, 80, 7),
            R(7, "centre", 50, 50, 9, ore: true),
            R(8, "mid-east-north", 70, 20, 7),
            R(9, "ore-ne", 70, 30, 6, ore: true),
            R(10, "mid-east-south", 70, 80, 7),
            R(11, "start-east", 92, 50, 8, start: true),
        ];
        RegionLink[] links =
        [
            L(0, 1, 20), L(0, 4, 20), L(0, 2, 24), L(0, 5, 24),
            L(1, 3, 18), L(2, 3, 16), L(2, 7, 22),
            L(4, 6, 18), L(5, 6, 16), L(5, 7, 22),
            L(3, 7, 20), L(6, 7, 20),
            L(3, 8, 20), L(6, 10, 20), L(7, 9, 20),
            L(8, 9, 16), L(10, 9, 22), L(8, 11, 24), L(10, 11, 24), L(9, 11, 22),
        ];
        OreField[] ore = [Ore(2, 30, 30), Ore(5, 30, 70), Ore(7, 50, 50, 16_000), Ore(9, 70, 30)];
        return Build("open-steppe", 100, 100, regions, links, ore, 0, 11);
    }

    // 14 regions: symmetric bases behind a single defensible choke.
    private static SimMap BuildFortressChoke()
    {
        Region[] regions =
        [
            R(0, "keep-west", 6, 50, 8, start: true),
            R(1, "yard-west", 16, 50, 7),
            R(2, "ore-west-a", 16, 25, 6, ore: true),
            R(3, "ore-west-b", 16, 75, 6, ore: true),
            R(4, "outpost-west", 30, 50, 6),
            R(5, "approach-west", 42, 50, 5),
            R(6, "choke", 50, 50, 4),
            R(7, "approach-east", 58, 50, 5),
            R(8, "outpost-east", 70, 50, 6),
            R(9, "ore-east-a", 84, 25, 6, ore: true),
            R(10, "ore-east-b", 84, 75, 6, ore: true),
            R(11, "yard-east", 84, 50, 7),
            R(12, "keep-east", 94, 50, 8, start: true),
            R(13, "overlook", 50, 20, 5),
        ];
        RegionLink[] links =
        [
            L(0, 1, 12), L(1, 2, 20), L(1, 3, 20), L(1, 4, 16),
            L(4, 5, 14), L(5, 6, 10), L(6, 13, 24), L(6, 7, 10),
            L(7, 8, 14), L(8, 11, 16), L(11, 9, 20), L(11, 10, 20), L(11, 12, 12),
            L(5, 13, 24), L(7, 13, 24),
        ];
        OreField[] ore = [Ore(2, 16, 25), Ore(3, 16, 75), Ore(9, 84, 25), Ore(10, 84, 75)];
        return Build("fortress-choke", 100, 100, regions, links, ore, 0, 12);
    }
}
