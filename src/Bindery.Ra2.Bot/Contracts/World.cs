// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
namespace Bindery.Ra2.Bot;

/// <summary>
/// Game time in simulation frames. Yuri's Revenge advances the simulation at a
/// nominal 15 frames per second at the default multiplayer game speed; every
/// cadence in the bot is expressed in frames so a paused or slowed game does not
/// make decisions go stale on the wall clock.
/// </summary>
public readonly record struct GameTime(long Frame) : IComparable<GameTime>
{
    public const int FramesPerSecond = 15;

    public double Seconds => Frame / (double)FramesPerSecond;

    public static GameTime FromSeconds(double seconds) => new((long)Math.Round(seconds * FramesPerSecond));

    public GameTime Plus(double seconds) => new(Frame + (long)Math.Round(seconds * FramesPerSecond));

    public double SecondsSince(GameTime earlier) => (Frame - earlier.Frame) / (double)FramesPerSecond;

    public int CompareTo(GameTime other) => Frame.CompareTo(other.Frame);

    public static bool operator <(GameTime a, GameTime b) => a.Frame < b.Frame;
    public static bool operator >(GameTime a, GameTime b) => a.Frame > b.Frame;
    public static bool operator <=(GameTime a, GameTime b) => a.Frame <= b.Frame;
    public static bool operator >=(GameTime a, GameTime b) => a.Frame >= b.Frame;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Seconds:0.0}s");
}

/// <summary>Stable identity of a game object for as long as it exists.</summary>
public readonly record struct EntityId(uint Value)
{
    public override string ToString() => $"e{Value}";
}

public readonly record struct PlayerId(int Value)
{
    public override string ToString() => $"p{Value}";
}

public readonly record struct RegionId(int Value)
{
    public override string ToString() => $"r{Value}";
}

/// <summary>A map cell in game cell coordinates.</summary>
public readonly record struct Cell(int X, int Y)
{
    public double DistanceTo(Cell other)
    {
        double dx = X - other.X, dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

public enum Faction { Allied, Soviet, Yuri }

public enum EntityKind { Infantry, Vehicle, Aircraft, Naval, Building }

/// <summary>
/// What an object is for. Roles are assigned by the rules database, never
/// guessed from names at decision time.
/// </summary>
public enum UnitRole
{
    Harvester,
    Mcv,
    Scout,
    AntiArmor,
    AntiInfantry,
    AntiAir,
    Artillery,
    Engineer,
    Support,
    Defense,
    Production,
    Power,
    Economy,
    Tech,
    Superweapon,
}

public enum QueueKind { Building, Defense, Infantry, Vehicle, Aircraft, Naval }

/// <summary>
/// A coarse map region: a start location, an ore field, a choke or an open
/// area. Strategy and operations reason about regions; only tactics touches cells.
/// </summary>
public sealed record Region(RegionId Id, string Name, Cell Center, int Radius, bool IsStartLocation, bool HasOre, bool Water);

/// <summary>An undirected passable link between regions with its ground travel cost in cells.</summary>
public sealed record RegionLink(RegionId A, RegionId B, double Distance, bool Ground, bool Naval);

public sealed record OreField(RegionId Region, Cell Center, int InitialValue, bool Gems);

/// <summary>Static map knowledge a player legitimately has before the match (shroud-independent).</summary>
public sealed record MapInfo(
    string MapId,
    int Width,
    int Height,
    IReadOnlyList<Region> Regions,
    IReadOnlyList<RegionLink> Links,
    IReadOnlyList<OreField> OreFields)
{
    /// <summary>The region whose centre is nearest <paramref name="cell"/>.</summary>
    /// <remarks>
    /// A cell equally near two centres goes to the larger region, then to the one whose centre is farther from the
    /// map centre, then to the lower id. The first two keys are the same for a region and its mirror image, so both
    /// halves of a mirrored map split their borders alike; resolving ties by list order put a west border cell in the
    /// forward region and its mirror cell in the rear one.
    /// </remarks>
    public Region? RegionOf(Cell cell)
    {
        Region? best = null;
        double bestDistance = double.MaxValue;
        foreach (Region region in Regions)
        {
            double d = region.Center.DistanceTo(cell);
            if (best is null || d < bestDistance - 1e-9 || (Math.Abs(d - bestDistance) <= 1e-9 && WinsTie(region, best)))
            {
                best = region;
                bestDistance = d;
            }
        }
        return best;
    }

    private bool WinsTie(Region challenger, Region incumbent)
    {
        if (challenger.Radius != incumbent.Radius) return challenger.Radius > incumbent.Radius;
        double c = FromMapCentre(challenger.Center), i = FromMapCentre(incumbent.Center);
        if (Math.Abs(c - i) > 1e-9) return c > i;
        return challenger.Id.Value < incumbent.Id.Value;
    }

    private double FromMapCentre(Cell c)
    {
        double dx = c.X - (Width / 2.0), dy = c.Y - (Height / 2.0);
        return (dx * dx) + (dy * dy);
    }
}

public sealed record PowerState(int Produced, int Drained)
{
    public bool LowPower => Drained > Produced;
    public int Surplus => Produced - Drained;
}
