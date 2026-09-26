// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Tests.Tactics;

/// <summary>
/// Small private fake of <see cref="IRulesDatabase"/> for Tactics tests only.
/// Types listed in <paramref name="armedTypes"/> resolve to an armed
/// <see cref="UnitRule"/>; every other type is unknown. Tests of force-ratio
/// logic must name the enemy type as armed, because the squad controller only
/// counts armed enemies as opposing force (an all-unknown fake makes every
/// local force ratio +infinity and silently disables retreat).
/// </summary>
internal sealed class FakeRulesDatabase(
    Dictionary<(string, string), double>? effectiveness = null,
    IEnumerable<string>? armedTypes = null) : IRulesDatabase
{
    private readonly Dictionary<(string, string), double> effectiveness = effectiveness ?? [];
    private readonly SortedDictionary<string, UnitRule> byTypeId = new(
        (armedTypes ?? []).ToDictionary(static t => t, Armed, StringComparer.Ordinal),
        StringComparer.Ordinal);

    public string RulesetId => "test-fixture";

    public IReadOnlyCollection<UnitRule> All => byTypeId.Values;

    public bool TryGet(string typeId, out UnitRule rule) => byTypeId.TryGetValue(typeId, out rule!);

    public UnitRule Get(string typeId) => byTypeId.TryGetValue(typeId, out UnitRule? rule) ? rule : throw new KeyNotFoundException(typeId);

    /// <summary>An armed vehicle rule (non-zero damage, anti-armor weapon) for <paramref name="typeId"/>.</summary>
    public static UnitRule Armed(string typeId) => new(
        typeId, typeId, [Faction.Soviet], EntityKind.Vehicle, UnitRole.AntiArmor, QueueKind.Vehicle, 800, 10.0, 0, [],
        1, 500, ArmorClass.Heavy, 25, WeaponClass.AntiArmor, 5, 4.0, 5, AntiAir: false, Deployable: false);

    public bool CanBuild(Faction faction, IReadOnlySet<string> ownedBuildingTypes, string typeId) => true;

    public IReadOnlyList<string>? PathTo(Faction faction, IReadOnlySet<string> ownedBuildingTypes, string typeId) => [];

    public double Effectiveness(string attacker, string defender) =>
        effectiveness.TryGetValue((attacker, defender), out double value) ? value : 1.0;
}

/// <summary>
/// Minimal in-memory <see cref="ILeaseManager"/>, identical in behaviour to the
/// Operations package's private fake but duplicated here so each package's
/// tests own their fakes independently.
/// </summary>
internal sealed class FakeLeaseManager : ILeaseManager
{
    private readonly Dictionary<LeaseKey, Lease> leases = [];

    public Lease? TryAcquire(LeaseKey key, string owner, int priority, GameTime now, double minHoldSeconds, double ttlSeconds)
    {
        Lease? existing = leases.GetValueOrDefault(key);
        if (existing is not null)
        {
            if (existing.Owner == owner)
            {
                Lease renewed = existing with { ExpiresAt = now.Plus(ttlSeconds) };
                leases[key] = renewed;
                return renewed;
            }
            bool expired = existing.ExpiresAt < now;
            bool canPreempt = priority > existing.Priority && now >= existing.MinHoldUntil;
            if (!expired && !canPreempt) return null;
        }
        Lease lease = new(key, owner, priority, now, now.Plus(minHoldSeconds), now.Plus(ttlSeconds));
        leases[key] = lease;
        return lease;
    }

    public bool Renew(LeaseKey key, string owner, GameTime now, double ttlSeconds)
    {
        Lease? existing = leases.GetValueOrDefault(key);
        if (existing is null || existing.Owner != owner) return false;
        leases[key] = existing with { ExpiresAt = now.Plus(ttlSeconds) };
        return true;
    }

    public void Release(LeaseKey key, string owner)
    {
        Lease? existing = leases.GetValueOrDefault(key);
        if (existing is not null && existing.Owner == owner) leases.Remove(key);
    }

    public string? OwnerOf(LeaseKey key, GameTime now)
    {
        Lease? lease = leases.GetValueOrDefault(key);
        return lease is not null && lease.ExpiresAt >= now ? lease.Owner : null;
    }

    public IReadOnlyList<Lease> HeldBy(string owner, GameTime now) =>
        leases.Values.Where(l => l.Owner == owner && l.ExpiresAt >= now).ToList();

    /// <summary>Test helper: grants a lease directly without going through acquisition rules.</summary>
    public void Grant(LeaseKey key, string owner, GameTime now, double ttlSeconds) =>
        leases[key] = new Lease(key, owner, 0, now, now, now.Plus(ttlSeconds));
}

internal static class Fixture
{
    public static readonly RegionId Home = new(1);
    public static readonly RegionId Front = new(2);

    public static MapInfo Map() => new(
        "test-map",
        100,
        100,
        [
            new Region(Home, "Home", new Cell(10, 10), 8, true, true, false),
            new Region(Front, "Front", new Cell(50, 50), 8, false, false, false),
        ],
        [new RegionLink(Home, Front, 40, true, false)],
        [new OreField(Home, new Cell(12, 8), 10000, false)]);

    public static BeliefSnapshot Belief(
        IReadOnlyList<OwnEntity>? own = null,
        IReadOnlyList<EnemyContact>? enemies = null,
        GameTime? time = null) => new(
        1,
        time ?? new GameTime(0),
        ObservationMode.Belief,
        new PlayerId(0),
        Faction.Allied,
        5000,
        new PowerState(100, 50),
        own ?? [],
        enemies ?? [],
        [],
        [],
        new Dictionary<RegionId, GameTime>(),
        [],
        Map());

    public static OwnEntity Unit(uint id, RegionId region, Cell position, UnitRole role = UnitRole.AntiArmor, int value = 800) =>
        new(new EntityId(id), "tank", role, EntityKind.Vehicle, position, region, 1.0, value, false);

    public static EnemyContact Enemy(uint id, Cell position, RegionId region, GameTime lastSeen, double confidence = 0.9, double health = 1.0, int value = 800) =>
        new(new EntityId(id), new PlayerId(1), "enemy", UnitRole.AntiArmor, EntityKind.Vehicle, position, region, lastSeen, health, value, confidence, false);
}
