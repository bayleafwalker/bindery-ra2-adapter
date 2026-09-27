// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Tests.Operations;

/// <summary>Small private fake of <see cref="IRulesDatabase"/> for Operations tests only.</summary>
internal sealed class FakeRulesDatabase : IRulesDatabase
{
    private readonly Dictionary<string, UnitRule> byType;
    private readonly Dictionary<(string Attacker, string Defender), double> effectiveness;

    public FakeRulesDatabase(IEnumerable<UnitRule> rules, Dictionary<(string, string), double>? effectiveness = null)
    {
        byType = rules.ToDictionary(r => r.TypeId, StringComparer.Ordinal);
        this.effectiveness = effectiveness ?? [];
    }

    public string RulesetId => "test-fixture";

    public IReadOnlyCollection<UnitRule> All => byType.Values;

    public bool TryGet(string typeId, out UnitRule rule) => byType.TryGetValue(typeId, out rule!);

    public UnitRule Get(string typeId) => byType[typeId];

    public bool CanBuild(Faction faction, IReadOnlySet<string> ownedBuildingTypes, string typeId)
    {
        UnitRule? rule = byType.GetValueOrDefault(typeId);
        if (rule is null) return false;
        if (!rule.Factions.Contains(faction)) return false;
        foreach (IReadOnlyList<string> group in rule.Prerequisites)
        {
            if (group.Count > 0 && !group.Any(ownedBuildingTypes.Contains)) return false;
        }
        return true;
    }

    public IReadOnlyList<string>? PathTo(Faction faction, IReadOnlySet<string> ownedBuildingTypes, string typeId)
    {
        UnitRule? rule = byType.GetValueOrDefault(typeId);
        if (rule is null || !rule.Factions.Contains(faction)) return null;
        List<string> missing = [];
        foreach (IReadOnlyList<string> group in rule.Prerequisites)
        {
            if (group.Count == 0 || group.Any(ownedBuildingTypes.Contains)) continue;
            missing.Add(group[0]);
        }
        return missing;
    }

    public double Effectiveness(string attacker, string defender) =>
        effectiveness.TryGetValue((attacker, defender), out double value) ? value : 1.0;
}

/// <summary>Small private fake of <see cref="IPlaybookLibrary"/> for Operations tests only.</summary>
internal sealed class FakePlaybookLibrary(IEnumerable<Playbook> playbooks) : IPlaybookLibrary
{
    private readonly Dictionary<string, Playbook> byId = playbooks.ToDictionary(p => p.Id, StringComparer.Ordinal);

    public IReadOnlyList<Playbook> All => byId.Values.ToList();

    public bool TryGet(string id, out Playbook playbook) => byId.TryGetValue(id, out playbook!);

    public IReadOnlyList<Playbook> For(Faction faction) => byId.Values.Where(p => p.Factions.Contains(faction)).ToList();
}

/// <summary>
/// Minimal in-memory <see cref="ILeaseManager"/>: single owner per key, higher
/// priority preempts only after the holder's minimum hold time, matching the
/// contract's documented semantics closely enough for controller tests.
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
}

/// <summary>Shared minimal fixture builders for Operations tests.</summary>
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

    public static UnitRule Building(string typeId, UnitRole role, int power = 0, int cost = 1000, double buildSeconds = 20) =>
        new(typeId, typeId, [Faction.Allied], EntityKind.Building, role, QueueKind.Building, cost, buildSeconds, power,
            [], 1, 500, ArmorClass.Concrete, 0, WeaponClass.None, 0, 0, 5, false, false);

    public static UnitRule Combat(string typeId, UnitRole role, QueueKind queue, int cost, double buildSeconds, ArmorClass armor = ArmorClass.Medium) =>
        new(typeId, typeId, [Faction.Allied], queue == QueueKind.Infantry ? EntityKind.Infantry : EntityKind.Vehicle, role, queue,
            cost, buildSeconds, -5, [], 1, 300, armor, 10, WeaponClass.AntiArmor, 5, 6, 6, false, false);

    public static BeliefSnapshot Belief(
        int credits = 5000,
        PowerState? power = null,
        IReadOnlyList<OwnEntity>? own = null,
        IReadOnlyList<EnemyContact>? enemies = null,
        IReadOnlyList<ProductionQueueState>? queues = null,
        GameTime? time = null) => new(
        1,
        time ?? new GameTime(0),
        ObservationMode.Belief,
        new PlayerId(0),
        Faction.Allied,
        credits,
        power ?? new PowerState(100, 50),
        own ?? [],
        enemies ?? [],
        [],
        queues ?? [],
        new Dictionary<RegionId, GameTime>(),
        [],
        Map());

    public static StrategicFeatures Features(
        BeliefSnapshot belief,
        IReadOnlyDictionary<UnitRole, double>? valueByRole = null,
        IReadOnlyDictionary<UnitRole, double>? enemyComposition = null,
        IReadOnlySet<string>? knownTech = null,
        IReadOnlyList<ThreatAssessment>? threats = null) => new(
        belief.Version,
        belief.Time,
        belief.Mode,
        belief.Faction,
        new EconomyFeatures(Trend.Flat(belief.Credits), Trend.Flat(500), Trend.Flat(200), 60, 0.8, 2, 1, 0.9, belief.Power),
        new ArmyFeatures(Trend.Flat(0), valueByRole ?? new Dictionary<UnitRole, double>(), [], Trend.Flat(0), Trend.Flat(0)),
        new EnemyFeatures(Trend.Flat(0), 0.8, enemyComposition ?? new Dictionary<UnitRole, double>(), knownTech ?? new HashSet<string>(), [], 5, 5, false),
        new MapControlFeatures(new Dictionary<RegionId, RegionControl> { [Home] = RegionControl.Own, [Front] = RegionControl.Unknown }, [], 0.5),
        new ScoutingFeatures(0.5, new Dictionary<RegionId, double>(), []),
        threats ?? [],
        []);

    public static StrategicIntent Intent(
        IReadOnlyList<CompositionTarget>? composition = null,
        IReadOnlyList<Objective>? objectives = null,
        IReadOnlyList<Condition>? attackConditions = null,
        BudgetShares? budget = null) => new(
        "intent-1",
        IntentSource.Selector,
        1,
        new GameTime(0),
        new GameTime(1000),
        StrategicPosture.Pressure,
        "test-playbook",
        new Dictionary<string, double>(),
        objectives ?? [],
        budget ?? new BudgetShares(0.25, 0.5, 0.15, 0.1),
        composition ?? [],
        [Front],
        attackConditions ?? [],
        [],
        [],
        0.7,
        []);
}
