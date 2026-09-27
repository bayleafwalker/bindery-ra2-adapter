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
        Dictionary<int, int> renumber = MirrorNumbering(regions, start0, start1);
        RegionId Map(RegionId r) => new(renumber[r.Value]);
        MapInfo map = new(
            id, width, height,
            [.. regions.Select(r => r with { Id = Map(r.Id) }).OrderBy(static r => r.Id.Value)],
            [.. links.Select(l => l with { A = Map(l.A), B = Map(l.B) })],
            [.. ore.Select(o => o with { Region = Map(o.Region) })]);
        return new SimMap(map, [Map(new RegionId(start0)), Map(new RegionId(start1))]);
    }

    /// <summary>
    /// Region ids in the order the planner and simulator need on a map mirrored between its starts: regions on the
    /// axis first, then one half in declared order, then each region of the other half at its twin's position.
    /// </summary>
    /// <remarks>
    /// Deterministic code breaks remaining ties by region id, and a reflection must not change the outcome of any
    /// comparison one player can make: two regions on the same side, or an axis region and a side region, must
    /// compare the same way as their twins. Hand numbering got this wrong on every map (an axis region between a
    /// start and its twin, an ore field numbered before its lane on one side and after it on the other), which made
    /// equal scouting and routing choices resolve differently for the two starts. A map without a mirror keeps its
    /// declared ids.
    /// </remarks>
    private static Dictionary<int, int> MirrorNumbering(IReadOnlyList<Region> regions, int start0, int start1)
    {
        Dictionary<int, int> identity = regions.ToDictionary(static r => r.Id.Value, static r => r.Id.Value);
        Cell a = regions.Single(r => r.Id.Value == start0).Center, b = regions.Single(r => r.Id.Value == start1).Center;
        if (a.Y != b.Y) return identity;
        int axisTwice = a.X + b.X;
        Dictionary<int, int> twin = [];
        foreach (Region r in regions)
        {
            Cell mirror = new(axisTwice - r.Center.X, r.Center.Y);
            Region? t = regions.FirstOrDefault(o => o.Center == mirror);
            if (t is null) return identity;
            twin[r.Id.Value] = t.Id.Value;
        }

        bool westIsA = a.X < b.X;
        List<Region> axis = [.. regions.Where(r => twin[r.Id.Value] == r.Id.Value).OrderBy(static r => r.Id.Value)];
        List<Region> first = [.. regions.Where(r => twin[r.Id.Value] != r.Id.Value && (r.Center.X * 2 < axisTwice) == westIsA).OrderBy(static r => r.Id.Value)];
        Dictionary<int, int> result = [];
        int next = 0;
        foreach (Region r in axis) result[r.Id.Value] = next++;
        foreach (Region r in first) result[r.Id.Value] = next++;
        foreach (Region r in first) result[twin[r.Id.Value]] = next++;
        return result;
    }

    // 9 regions: two symmetric lanes meeting at a contested, ore-rich centre; each start has its own home ore field
    // (the east start had none, so the start the arm always takes began with a 33-cell head start to income). The
    // layout is a mirror image about x = 50: units move in straight lines between region centres, so a centre at
    // x = 55 with lanes at x = 45 and 75 gave the west start 37 of 48 mirror matches although graph distances agreed.
    private static SimMap BuildTwinValley()
    {
        Region[] regions =
        [
            R(0, "start-west", 10, 50, 8, start: true),
            R(1, "ore-west", 25, 50, 6, ore: true),
            R(2, "lane-north", 40, 25, 7),
            R(3, "lane-south", 40, 75, 7),
            R(4, "centre", 50, 50, 9, ore: true),
            R(5, "lane-north-east", 60, 25, 7),
            R(6, "lane-south-east", 60, 75, 7),
            R(7, "start-east", 90, 50, 8, start: true),
            R(8, "ore-east", 75, 50, 6, ore: true),
        ];
        RegionLink[] links =
        [
            L(0, 1, 15), L(1, 2, 26), L(1, 3, 26), L(2, 4, 22), L(3, 4, 22),
            L(4, 5, 22), L(4, 6, 22), L(5, 8, 26), L(6, 8, 26), L(8, 7, 15),
        ];
        OreField[] ore = [Ore(1, 25, 50), Ore(4, 50, 50, 16_000), Ore(8, 75, 50)];
        return Build("twin-valley", 100, 100, regions, links, ore, 0, 7);
    }

    // 10 regions: a central river forces two crossings between symmetric halves, mirrored about x = 50. Both home
    // ore fields lie on the north side by the ford; with the east field in the south the west start alone had its
    // ore on the ford road.
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
            R(7, "ore-east", 78, 30, 6, ore: true),
            R(8, "field-east", 78, 70, 7),
            R(9, "start-east", 92, 50, 8, start: true),
        ];
        RegionLink[] links =
        [
            L(0, 1, 18), L(0, 2, 18), L(1, 3, 20), L(2, 3, 20),
            L(3, 4, 10, ground: false, naval: true), L(4, 5, 10, ground: false, naval: true),
            L(3, 6, 24), L(6, 5, 24),
            L(5, 7, 20), L(5, 8, 20), L(7, 9, 18), L(8, 9, 18),
        ];
        OreField[] ore = [Ore(1, 22, 30), Ore(7, 78, 30)];
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
            R(7, "ore-east", 85, 25, 6, ore: true),
            R(8, "dock-east", 75, 50, 6),
            R(9, "home-east", 90, 50, 8, start: true),
        ];
        RegionLink[] links =
        [
            // The straits are crossed by bridges (ground and naval): without them the two sides have no ground
            // route to each other, and the fixture has no naval units, so every match could only time out.
            L(0, 1, 14), L(0, 2, 14), L(2, 1, 20), L(2, 3, 8, ground: true, naval: true),
            L(3, 4, 8, ground: true, naval: true), L(4, 5, 18),
            L(4, 6, 8, ground: true, naval: true), L(6, 8, 8, ground: true, naval: true),
            L(8, 7, 20), L(8, 9, 14), L(9, 7, 14),
        ];
        OreField[] ore = [Ore(1, 15, 25), Ore(4, 50, 50, 14_000), Ore(5, 50, 20), Ore(7, 85, 25)];
        return Build("island-bridges", 100, 100, regions, links, ore, 0, 9);
    }

    // 13 regions: a wide, mostly-open steppe with many alternate routes, the east half a mirror of the west (each
    // start touches two ore fields; the east start used to touch one).
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
            R(8, "flank-ne", 80, 15, 7),
            R(9, "ore-ne", 70, 30, 6, ore: true),
            R(10, "flank-se", 80, 85, 7),
            R(11, "start-east", 92, 50, 8, start: true),
            R(12, "ore-se", 70, 70, 6, ore: true),
        ];
        RegionLink[] links =
        [
            L(0, 1, 20), L(0, 4, 20), L(0, 2, 24), L(0, 5, 24),
            L(1, 3, 18), L(2, 3, 16), L(2, 7, 22),
            L(4, 6, 18), L(5, 6, 16), L(5, 7, 22),
            L(3, 7, 20), L(6, 7, 20),
            L(11, 8, 20), L(11, 10, 20), L(11, 9, 24), L(11, 12, 24),
            L(8, 3, 18), L(9, 3, 16), L(9, 7, 22),
            L(10, 6, 18), L(12, 6, 16), L(12, 7, 22),
        ];
        OreField[] ore = [Ore(2, 30, 30), Ore(5, 30, 70), Ore(7, 50, 50, 16_000), Ore(9, 70, 30), Ore(12, 70, 70)];
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
