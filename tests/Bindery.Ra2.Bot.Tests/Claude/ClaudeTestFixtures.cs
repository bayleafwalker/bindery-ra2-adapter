// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Claude;

namespace Bindery.Ra2.Bot.Tests.Claude;

/// <summary>Minimal rules database: a handful of Allied and Soviet types with first-alternative prerequisite paths.</summary>
internal sealed class FakeRules : IRulesDatabase
{
    private readonly Dictionary<string, UnitRule> rules;

    public FakeRules()
    {
        UnitRule[] all =
        [
            Rule("GAPOWR", "Power Plant", Faction.Allied, EntityKind.Building, UnitRole.Power, 800, 10),
            Rule("GAREFN", "Ore Refinery", Faction.Allied, EntityKind.Building, UnitRole.Economy, 2000, 20, "GAPOWR"),
            Rule("GAPILE", "Barracks", Faction.Allied, EntityKind.Building, UnitRole.Production, 500, 8, "GAPOWR"),
            Rule("GAWEAP", "War Factory", Faction.Allied, EntityKind.Building, UnitRole.Production, 2000, 20, "GAREFN"),
            Rule("GATECH", "Battle Lab", Faction.Allied, EntityKind.Building, UnitRole.Tech, 2000, 25, "GAWEAP"),
            Rule("E1", "GI", Faction.Allied, EntityKind.Infantry, UnitRole.AntiInfantry, 200, 4, "GAPILE"),
            Rule("MTNK", "Grizzly Tank", Faction.Allied, EntityKind.Vehicle, UnitRole.AntiArmor, 700, 10, "GAWEAP"),
            Rule("NAPOWR", "Tesla Reactor", Faction.Soviet, EntityKind.Building, UnitRole.Power, 600, 10),
            Rule("HTNK", "Rhino Tank", Faction.Soviet, EntityKind.Vehicle, UnitRole.AntiArmor, 900, 12, "NAPOWR"),
            Rule("NAIRON", "Iron Curtain", Faction.Soviet, EntityKind.Building, UnitRole.Superweapon, 2500, 40, "NAPOWR"),
        ];
        rules = all.ToDictionary(r => r.TypeId, StringComparer.Ordinal);
    }

    public int PathToCalls { get; private set; }

    public string RulesetId => "test-rules";

    public IReadOnlyCollection<UnitRule> All => rules.Values.ToList();

    public bool TryGet(string typeId, out UnitRule rule) => rules.TryGetValue(typeId, out rule!);

    public UnitRule Get(string typeId) => rules[typeId];

    public bool CanBuild(Faction faction, IReadOnlySet<string> ownedBuildingTypes, string typeId) =>
        PathTo(faction, ownedBuildingTypes, typeId) is { Count: 0 };

    public IReadOnlyList<string>? PathTo(Faction faction, IReadOnlySet<string> ownedBuildingTypes, string typeId)
    {
        PathToCalls++;
        if (!rules.TryGetValue(typeId, out UnitRule? rule) || !rule.Factions.Contains(faction))
        {
            return null;
        }
        List<string> path = new();
        foreach (IReadOnlyList<string> group in rule.Prerequisites)
        {
            string first = group[0];
            if (ownedBuildingTypes.Contains(first))
            {
                continue;
            }
            IReadOnlyList<string>? inner = PathTo(faction, ownedBuildingTypes, first);
            if (inner is null)
            {
                return null;
            }
            foreach (string step in inner.Append(first))
            {
                if (!path.Contains(step))
                {
                    path.Add(step);
                }
            }
        }
        return path;
    }

    public double Effectiveness(string attacker, string defender) => 1;

    private static UnitRule Rule(string id, string name, Faction faction, EntityKind kind, UnitRole role, int cost, double seconds, params string[] prerequisites) =>
        new(
            id,
            name,
            [faction],
            kind,
            role,
            kind == EntityKind.Building ? QueueKind.Building : kind == EntityKind.Infantry ? QueueKind.Infantry : QueueKind.Vehicle,
            cost,
            seconds,
            0,
            prerequisites.Select(p => (IReadOnlyList<string>)[p]).ToList(),
            1,
            500,
            ArmorClass.Heavy,
            10,
            WeaponClass.AntiArmor,
            kind == EntityKind.Building ? 0 : 5,
            kind == EntityKind.Building ? 0 : 1.5,
            6,
            false,
            false);
}

internal sealed class FakePlaybooks : IPlaybookLibrary
{
    public FakePlaybooks()
    {
        All =
        [
            Book("allied-boom", Faction.Allied, StrategicPosture.Boom, "GATECH"),
            Book("allied-harass", Faction.Allied, StrategicPosture.Harass, "GAWEAP"),
            Book("soviet-rhino-rush", Faction.Soviet, StrategicPosture.Pressure, "NAIRON"),
        ];
    }

    public IReadOnlyList<Playbook> All { get; }

    public bool TryGet(string id, out Playbook playbook)
    {
        playbook = All.FirstOrDefault(p => p.Id == id)!;
        return playbook is not null;
    }

    public IReadOnlyList<Playbook> For(Faction faction) => All.Where(p => p.Factions.Contains(faction)).ToList();

    private static Playbook Book(string id, Faction faction, StrategicPosture posture, string techGoal) =>
        new(
            id,
            $"{id} description",
            [faction],
            posture,
            new BudgetShares(0.4, 0.3, 0.2, 0.1),
            [new CompositionTarget(UnitRole.AntiArmor, 0.4, 0.8)],
            [techGoal],
            [new Condition(ConditionMetric.ArmyValueRatio, Comparison.Gt, 1.2)],
            [new Condition(ConditionMetric.BaseThreatRatio, Comparison.Gt, 1.5)],
            [new PlaybookParameter("aggression", 0, 1, 0.5, "how early to attack"), new PlaybookParameter("harvesters", 2, 8, 4, "target harvester count")],
            45);
}

/// <summary>Scripted <see cref="IMessageClient"/>: returns queued replies or runs a handler; records every request.</summary>
internal sealed class FakeMessageClient : IMessageClient
{
    private readonly Func<ModelRequest, CancellationToken, Task<ModelReply>> handler;

    public FakeMessageClient(Func<ModelRequest, CancellationToken, Task<ModelReply>> handler)
    {
        this.handler = handler;
    }

    public List<ModelRequest> Requests { get; } = new();

    public static FakeMessageClient Replying(string text, string stopReason = "end_turn", ModelUsage? usage = null, string? modelId = "claude-opus-5") =>
        new((_, _) => Task.FromResult(new ModelReply(text, stopReason, null, usage ?? new ModelUsage(1200, 300, 800, 0), modelId)));

    public static FakeMessageClient Throwing(Exception exception) => new((_, _) => Task.FromException<ModelReply>(exception));

    public Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return handler(request, cancellationToken);
    }
}

internal static class ClaudeFixtures
{
    public static StrategicFeatures Features(long version = 7, double seconds = 300, Faction faction = Faction.Allied, IReadOnlySet<string>? enemyTech = null, double credits = 2500) =>
        new(
            SnapshotVersion: version,
            Time: GameTime.FromSeconds(seconds),
            Mode: ObservationMode.Belief,
            Faction: faction,
            Economy: new EconomyFeatures(
                new Trend(credits, 50, -100, 400),
                new Trend(1400, 0, 20, 100),
                Trend.Flat(1200),
                double.PositiveInfinity,
                0.75,
                3,
                2,
                0.8,
                new PowerState(300, 200)),
            Army: new ArmyFeatures(
                Trend.Flat(5000),
                new Dictionary<UnitRole, double> { [UnitRole.AntiInfantry] = 1000, [UnitRole.AntiArmor] = 4000 },
                [new ForceCluster(new RegionId(2), 8, 5000, 0.9)],
                Trend.Flat(700),
                Trend.Flat(1500)),
            Enemy: new EnemyFeatures(
                new Trend(4000, 0, 200, 900),
                0.6,
                new Dictionary<UnitRole, double> { [UnitRole.AntiArmor] = 0.7, [UnitRole.AntiInfantry] = 0.3 },
                enemyTech ?? new HashSet<string>(StringComparer.Ordinal) { "HTNK" },
                ["HTNK"],
                12,
                45,
                false),
            MapControl: new MapControlFeatures(
                new Dictionary<RegionId, RegionControl> { [new RegionId(3)] = RegionControl.Enemy, [new RegionId(1)] = RegionControl.Own, [new RegionId(2)] = RegionControl.Contested },
                [new RegionId(4)],
                0.4),
            Scouting: new ScoutingFeatures(
                0.55,
                new Dictionary<RegionId, double> { [new RegionId(3)] = 40, [new RegionId(1)] = 0 },
                ["enemy tech level"]),
            Threats: [new ThreatAssessment(new RegionId(1), 1200, 3000, 2.5, 35, 10, true, 0.7, [new RegionId(3), new RegionId(2), new RegionId(1)])],
            Events: [new StrategicEvent(StrategicEventKind.NewEnemyTech, GameTime.FromSeconds(seconds - 20), 0.6, "HTNK seen", new RegionId(3))]);

    public static StrategicIntent ActiveIntent(string playbookId = "allied-boom", double issuedAt = 280) =>
        new(
            IntentId: "selector/5",
            Source: IntentSource.Selector,
            BasedOnSnapshotVersion: 5,
            IssuedAt: GameTime.FromSeconds(issuedAt),
            ExpiresAt: GameTime.FromSeconds(issuedAt + 120),
            Posture: StrategicPosture.Boom,
            PlaybookId: playbookId,
            PlaybookParameters: new Dictionary<string, double> { ["aggression"] = 0.3 },
            Objectives: [new Objective(ObjectiveKind.Expand, new RegionId(4), null, 1)],
            Budget: new BudgetShares(0.5, 0.2, 0.2, 0.1),
            Composition: [new CompositionTarget(UnitRole.AntiArmor, 0.5, 0.9)],
            RegionsOfInterest: [new RegionId(4)],
            AttackConditions: [],
            AbortTriggers: [new Condition(ConditionMetric.BaseThreatRatio, Comparison.Gt, 1.5)],
            ReplanTriggers: [],
            Confidence: 0.6,
            Assumptions: ["enemy teching"]);

    public static StrategistContext Context(StrategicFeatures? features = null, StrategicIntent? active = null, string? personality = null, FakeRules? rules = null) =>
        new(
            features ?? Features(),
            rules ?? new FakeRules(),
            new FakePlaybooks(),
            active,
            active is null ? [] : [new IntentHistoryEntry(active.IntentId, active.Source, active.Posture, active.PlaybookId, active.IssuedAt, null, null)],
            personality);

    public static IntentDraft Draft(string playbookId = "allied-boom", string posture = "Boom", double aggression = 0.7) =>
        new()
        {
            PlaybookId = playbookId,
            Posture = posture,
            Parameters = [new DraftParameter { Name = "aggression", Value = aggression }, new DraftParameter { Name = "harvesters", Value = 6 }],
            Objectives =
            [
                new DraftObjective { Kind = "Expand", RegionId = 4, TypeId = null, Priority = 1 },
                new DraftObjective { Kind = "TechTo", RegionId = null, TypeId = "GATECH", Priority = 2 },
            ],
            Budget = new DraftBudget { Economy = 0.5, Army = 0.25, Tech = 0.15, Defense = 0.1 },
            Composition = [new DraftComposition { Role = "AntiArmor", MinShare = 0.5, MaxShare = 0.9 }],
            RegionsOfInterest = [4, 2],
            AttackConditions = [new DraftCondition { Metric = "ArmyValueRatio", Op = "Ge", Threshold = 1.3, RegionId = null }],
            AbortTriggers = [new DraftCondition { Metric = "BaseThreatRatio", Op = "Gt", Threshold = 1.5, RegionId = null }],
            ReplanTriggers = [new DraftCondition { Metric = "LocalForceRatio", Op = "Lt", Threshold = 0.6, RegionId = 2 }],
            ExpiresInSeconds = 120,
            Confidence = 0.72,
            Assumptions = ["enemy is teching to heavy armor"],
            Rationale = "Economy lead; enemy army estimate is 12 s old.",
        };

    public static string DraftJson(IntentDraft? draft = null) => JsonSerializer.Serialize(draft ?? Draft(), IntentDraftSchema.ParseOptions);
}
