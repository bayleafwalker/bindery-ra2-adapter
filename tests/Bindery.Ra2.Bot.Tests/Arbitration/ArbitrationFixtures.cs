// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Tests.Arbitration;

/// <summary>Small private fakes of the contracts packages C depends on.</summary>
internal static class Fx
{
    public static readonly RegionId R0 = new(0), R1 = new(1), R2 = new(2), R3 = new(3);

    public static MapInfo Map { get; } = new(
        "test-map", 64, 64,
        [
            new Region(R0, "own-start", new Cell(5, 5), 4, true, true, false),
            new Region(R1, "centre", new Cell(30, 30), 6, false, true, false),
            new Region(R2, "enemy-start", new Cell(58, 58), 4, true, true, false),
            new Region(R3, "far-ore", new Cell(58, 5), 4, false, true, false),
        ],
        [
            new RegionLink(R0, R1, 35, true, false),
            new RegionLink(R1, R2, 35, true, false),
            new RegionLink(R1, R3, 30, true, false),
        ],
        []);

    public static FakeRules Rules { get; } = new();

    public static FakePlaybooks Playbooks { get; } = new();

    public static GameTime T(double seconds) => GameTime.FromSeconds(seconds);

    public static StrategicFeatures Features(
        double seconds,
        long version = -1,
        Faction faction = Faction.Allied,
        double ownArmy = 1000,
        double enemyArmy = 1000,
        IReadOnlyList<ThreatAssessment>? threats = null,
        IReadOnlyList<StrategicEvent>? events = null,
        IReadOnlySet<string>? knownTech = null,
        double credits = 5000,
        double income = 600)
    {
        GameTime time = T(seconds);
        return new StrategicFeatures(
            version < 0 ? time.Frame : version,
            time,
            ObservationMode.Belief,
            faction,
            new EconomyFeatures(Trend.Flat(credits), Trend.Flat(income), Trend.Flat(0), 600, 0.5, 2, 1, 0.9, new PowerState(100, 50)),
            new ArmyFeatures(Trend.Flat(ownArmy), new Dictionary<UnitRole, double>(), [], new Trend(0, 0, 0, 0), Trend.Flat(0)),
            new EnemyFeatures(Trend.Flat(enemyArmy), 0.5, new Dictionary<UnitRole, double>(), knownTech ?? new HashSet<string>(), [], 10, 20, false),
            new MapControlFeatures(new Dictionary<RegionId, RegionControl>(), [], 0.5),
            new ScoutingFeatures(0.5, new Dictionary<RegionId, double> { [R0] = 0, [R1] = 30 }, []),
            threats ?? [],
            events ?? []);
    }

    public static BeliefSnapshot Belief(double seconds, long version = -1, Faction faction = Faction.Allied, IReadOnlyList<OwnEntity>? own = null, int credits = 5000)
    {
        GameTime time = T(seconds);
        return new BeliefSnapshot(
            version < 0 ? time.Frame : version, time, ObservationMode.Belief, new PlayerId(1), faction, credits, new PowerState(100, 50),
            own ?? [new OwnEntity(new EntityId(1), "gacnst", UnitRole.Production, EntityKind.Building, new Cell(5, 5), R0, 1, 3000, true)],
            [], [], [], new Dictionary<RegionId, GameTime>(), [], Map);
    }

    public static ValidationContext Context(StrategicFeatures features, StrategicIntent? active = null, GameTime activeSince = default) =>
        new(features, Belief(features.Time.Seconds, features.SnapshotVersion, features.Faction), Rules, Playbooks, active, activeSince);

    public static StrategicIntent Intent(
        string id,
        string playbookId,
        StrategicPosture posture,
        double issuedAt = 0,
        double lifetime = 120,
        double confidence = 0.6,
        BudgetShares? budget = null,
        IReadOnlyList<Objective>? objectives = null,
        IReadOnlyDictionary<string, double>? parameters = null,
        IReadOnlyList<CompositionTarget>? composition = null,
        IReadOnlyList<RegionId>? regions = null,
        IReadOnlyList<Condition>? abort = null,
        IReadOnlyList<Condition>? replan = null,
        IntentSource source = IntentSource.Scripted,
        long version = -1)
    {
        GameTime issued = T(issuedAt);
        return new StrategicIntent(
            id, source, version < 0 ? issued.Frame : version, issued, issued.Plus(lifetime), posture, playbookId,
            parameters ?? new Dictionary<string, double>(),
            objectives ?? [],
            budget ?? new BudgetShares(0.4, 0.4, 0.1, 0.1),
            composition ?? [],
            regions ?? [],
            [],
            abort ?? [],
            replan ?? [],
            confidence,
            []);
    }

    public static ValidationResult Accepted(StrategicIntent intent) => new(true, intent, []);

    public static UnitRule Rule(string id, Faction[] factions, EntityKind kind, UnitRole role, int cost, string[][]? prereqs = null) =>
        new(id, id, factions, kind, role, kind == EntityKind.Building ? QueueKind.Building : QueueKind.Vehicle, cost, cost / 100.0, 0,
            (prereqs ?? []).Select(static g => (IReadOnlyList<string>)g).ToList(), 1, 500, ArmorClass.Heavy, 50, WeaponClass.AntiArmor, 5, 5, 6, false, false);
}

internal sealed class FakeRules : IRulesDatabase
{
    private readonly Dictionary<string, UnitRule> rules = new(StringComparer.Ordinal);

    public FakeRules()
    {
        Faction[] allied = [Faction.Allied];
        Faction[] soviet = [Faction.Soviet];
        Faction[] both = [Faction.Allied, Faction.Soviet];
        Add(Fx.Rule("gacnst", allied, EntityKind.Building, UnitRole.Production, 3000));
        Add(Fx.Rule("gapowr", allied, EntityKind.Building, UnitRole.Power, 800, [["gacnst"]]));
        Add(Fx.Rule("gaweap", allied, EntityKind.Building, UnitRole.Production, 2000, [["gapowr"]]));
        Add(Fx.Rule("gatech", allied, EntityKind.Building, UnitRole.Tech, 2500, [["gaweap"]]));
        Add(Fx.Rule("gaprism", allied, EntityKind.Building, UnitRole.Defense, 1500, [["gatech"]]));
        Add(Fx.Rule("gachrono", allied, EntityKind.Building, UnitRole.Superweapon, 5000, [["gamissing"]]));
        Add(Fx.Rule("mtnk", allied, EntityKind.Vehicle, UnitRole.AntiArmor, 900, [["gaweap"]]));
        Add(Fx.Rule("harv", both, EntityKind.Vehicle, UnitRole.Harvester, 1400));
        Add(Fx.Rule("htnk", soviet, EntityKind.Vehicle, UnitRole.AntiArmor, 900));
        Add(Fx.Rule("apoc", soviet, EntityKind.Vehicle, UnitRole.AntiArmor, 1750));
    }

    public string RulesetId => "test-fixture";

    public IReadOnlyCollection<UnitRule> All => rules.Values;

    public bool TryGet(string typeId, out UnitRule rule) => rules.TryGetValue(typeId, out rule!);

    public UnitRule Get(string typeId) => rules[typeId];

    public bool CanBuild(Faction faction, IReadOnlySet<string> ownedBuildingTypes, string typeId) =>
        PathTo(faction, ownedBuildingTypes, typeId) is { Count: 0 };

    public IReadOnlyList<string>? PathTo(Faction faction, IReadOnlySet<string> ownedBuildingTypes, string typeId)
    {
        List<string> path = [];
        return Walk(faction, ownedBuildingTypes, typeId, path, 0) ? path : null;
    }

    public double Effectiveness(string attacker, string defender) => 1;

    private bool Walk(Faction faction, IReadOnlySet<string> owned, string typeId, List<string> path, int depth)
    {
        if (depth > 10 || !rules.TryGetValue(typeId, out UnitRule? rule) || !rule.Factions.Contains(faction)) return false;
        foreach (IReadOnlyList<string> group in rule.Prerequisites)
        {
            if (group.Any(owned.Contains) || group.Any(path.Contains)) continue;
            string first = group[0];
            if (!Walk(faction, owned, first, path, depth + 1)) return false;
            path.Add(first);
        }
        return true;
    }

    private void Add(UnitRule rule) => rules[rule.TypeId] = rule;
}

internal sealed class FakePlaybooks : IPlaybookLibrary
{
    private readonly Dictionary<string, Playbook> playbooks = new(StringComparer.Ordinal);

    public FakePlaybooks()
    {
        PlaybookParameter[] parameters =
        [
            new("aggression", 0, 1, 0.5, "How eagerly to attack."),
            new("expandAt", 1, 4, 2, "Refineries before expanding."),
        ];
        CompositionTarget[] mix = [new(UnitRole.AntiArmor, 0.4, 0.8), new(UnitRole.AntiAir, 0.1, 0.3)];
        Add(new Playbook("allied-boom", "Economy first.", [Faction.Allied], StrategicPosture.Boom, new BudgetShares(0.6, 0.2, 0.1, 0.1), mix, ["gatech"],
            [new Condition(ConditionMetric.ArmyValueRatio, Comparison.Gt, 1.3)], [], parameters, 45));
        Add(new Playbook("allied-expand", "Take more ore.", [Faction.Allied], StrategicPosture.Boom, new BudgetShares(0.7, 0.2, 0, 0.1), mix, [],
            [], [], [], 45));
        Add(new Playbook("allied-turtle", "Defend and tech.", [Faction.Allied], StrategicPosture.Turtle, new BudgetShares(0.3, 0.2, 0.2, 0.3), mix, ["gaprism"],
            [], [], [], 45));
        Add(new Playbook("allied-pressure", "Constant pressure.", [Faction.Allied], StrategicPosture.Pressure, new BudgetShares(0.2, 0.7, 0, 0.1), mix, [],
            [new Condition(ConditionMetric.ArmyValueRatio, Comparison.Gt, 1.0)],
            [new Condition(ConditionMetric.ArmyValueRatio, Comparison.Lt, 0.5)], [], 45));
        Add(new Playbook("soviet-rush", "Rhino rush.", [Faction.Soviet], StrategicPosture.AllIn, new BudgetShares(0.2, 0.8, 0, 0), mix, [],
            [], [], [], 30));
        Add(new Playbook("generic-defend", "Hold the base.", [Faction.Allied, Faction.Soviet, Faction.Yuri], StrategicPosture.Defend,
            new BudgetShares(0.3, 0.4, 0, 0.3), mix, [], [], [], [], 0));
    }

    public IReadOnlyList<Playbook> All => playbooks.Values.OrderBy(static p => p.Id, StringComparer.Ordinal).ToList();

    public bool TryGet(string id, out Playbook playbook) => playbooks.TryGetValue(id, out playbook!);

    public IReadOnlyList<Playbook> For(Faction faction) => All.Where(p => p.Factions.Contains(faction)).ToList();

    private void Add(Playbook playbook) => playbooks[playbook.Id] = playbook;
}
