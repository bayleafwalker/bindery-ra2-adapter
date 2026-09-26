// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Sim;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Sim;

/// <summary>
/// Simulator rules that decide whether the arena exercises the bot's real code paths: repair happens only at a
/// service depot, buildings may not overlap, factories are the ones the rules declare, oracle frames keep other
/// players' kills theirs, and every frame names the enemies.
/// </summary>
public sealed class SimRulesFidelityTests
{
    private static readonly PlayerId P0 = new(0);
    private static readonly PlayerId P1 = new(1);

    private static SkirmishSimulation NewSim(IRulesDatabase rules, double maxSeconds = 120) =>
        new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 11, maxSeconds: maxSeconds));

    [Fact]
    public void Repair_needs_a_service_depot_and_heals_only_beside_it()
    {
        TestRules rules = new();
        SkirmishSimulation sim = NewSim(rules);
        SimTestHelpers.DeployStartingMcv(sim, P0);
        EntityId tank = sim.DebugSpawnAt(P0, TestRules.Strong, new Cell(10, 0));
        sim.DebugSetHealth(tank, 100);

        // No depot: the order is refused and nothing heals.
        int rejected = sim.RejectedCommandCount(P0);
        sim.Submit(P0, new RepairCommand("test", tank, null));
        sim.Advance(3);
        Assert.Equal(rejected + 1, sim.RejectedCommandCount(P0));
        Assert.Equal(100, sim.DebugHealthOf(tank));

        // With a depot the unit drives there and heals beside it, to full.
        EntityId depot = sim.DebugSpawnAt(P0, TestRules.Depot, new Cell(2, 0));
        sim.Submit(P0, new RepairCommand("test", tank, depot));
        sim.Step();
        Assert.Equal(rejected + 1, sim.RejectedCommandCount(P0));
        sim.Advance(60);
        Assert.Equal(rules.Get(TestRules.Strong).Strength, sim.DebugHealthOf(tank));
        ObservedEntity healed = sim.Observe(P0).Entities.Single(e => e.Id == tank);
        Assert.True(healed.Position.DistanceTo(new Cell(2, 0)) <= 3, $"healed at {healed.Position}, away from the depot");
    }

    [Fact]
    public void A_building_cannot_be_placed_on_another_building()
    {
        TestRules rules = new();
        SkirmishSimulation sim = NewSim(rules);
        SimTestHelpers.DeployStartingMcv(sim, P0);
        Cell yard = sim.Observe(P0).Entities.Single(e => e.TypeId == TestRules.ConYard).Position;
        sim.Submit(P0, new ProduceCommand("test", TestRules.Power, QueueKind.Building));
        sim.Advance(4);

        int rejected = sim.RejectedCommandCount(P0);
        sim.Submit(P0, new PlaceBuildingCommand("test", TestRules.Power, yard));
        sim.Step();
        Assert.Equal(rejected + 1, sim.RejectedCommandCount(P0));
        Assert.DoesNotContain(sim.Observe(P0).Entities, e => e.TypeId == TestRules.Power);

        sim.Submit(P0, new PlaceBuildingCommand("test", TestRules.Power, new Cell(yard.X, yard.Y + 3)));
        sim.Step();
        Assert.Contains(sim.Observe(P0).Entities, e => e.TypeId == TestRules.Power);
    }

    [Fact]
    public void A_queue_runs_on_the_factories_the_rules_declare_for_it()
    {
        // The barracks declares the aircraft queue (and the war factory does not), although the aircraft's
        // prerequisite is the war factory: the declaration decides, and without it the queue has no factory.
        TestRules test = new();
        OverrideRules rules = new(test,
            test.Get(TestRules.ConYard) with { Produces = [QueueKind.Building, QueueKind.Defense] },
            test.Get(TestRules.Barracks) with { Produces = [QueueKind.Infantry, QueueKind.Aircraft] },
            test.Get(TestRules.WarFactory) with { Produces = [QueueKind.Vehicle] });
        SkirmishSimulation sim = NewSim(rules);
        SimTestHelpers.DeployStartingMcv(sim, P0);
        SimTestHelpers.BuildBuilding(sim, test, P0, TestRules.Power, buildSecondsBudget: 3);
        SimTestHelpers.BuildBuilding(sim, test, P0, TestRules.Barracks, buildSecondsBudget: 3);
        SimTestHelpers.BuildBuilding(sim, test, P0, TestRules.WarFactory, buildSecondsBudget: 3);
        sim.DebugEnqueue(P0, QueueKind.Aircraft, TestRules.Aircraft);
        sim.Step();
        Assert.Equal(1, sim.Observe(P0).Queues.Single(q => q.Kind == QueueKind.Aircraft).Factories);

        EntityId barracks = sim.Observe(P0).Entities.Single(e => e.TypeId == TestRules.Barracks).Id;
        sim.Submit(P0, new SellCommand("test", barracks));
        sim.Step();
        Assert.Equal(0, sim.Observe(P0).Queues.Single(q => q.Kind == QueueKind.Aircraft).Factories);
    }

    [Fact]
    public void Oracle_frames_carry_only_the_players_own_kills_and_name_the_enemies()
    {
        TestRules rules = new();
        SkirmishSimulation sim = NewSim(rules, maxSeconds: 60);
        SimTestHelpers.DeployStartingMcv(sim, P0);
        SimTestHelpers.DeployStartingMcv(sim, P1);
        EntityId victim = sim.DebugSpawnAt(P0, TestRules.Weak, new Cell(20, 0));
        sim.DebugSpawnAt(P1, TestRules.Strong, new Cell(21, 0));

        List<GameEvent> p0Events = [], p1Events = [];
        for (int i = 0; i < 20 * GameTime.FramesPerSecond && sim.DebugHealthOf(victim) is not null; i++)
        {
            sim.Step();
            p0Events.AddRange(sim.Observe(P0, ObservationMode.Oracle).Events);
            p1Events.AddRange(sim.Observe(P1, ObservationMode.Oracle).Events);
        }

        Assert.Null(sim.DebugHealthOf(victim));
        Assert.Contains(p0Events, e => e.Kind == GameEventKind.EntityDestroyed && e.Entity == victim);
        Assert.DoesNotContain(p0Events, e => e.Kind == GameEventKind.EntityKilledByUs);
        GameEvent kill = Assert.Single(p1Events, e => e.Kind == GameEventKind.EntityKilledByUs);
        Assert.Equal(P1, kill.Owner);
        Assert.Equal(victim, kill.Entity);

        Assert.Equal([P1], sim.Observe(P0).Enemies!);
        Assert.Equal([P0], sim.Observe(P1, ObservationMode.Oracle).Enemies!);
    }

    /// <summary>A rules database with some rules replaced.</summary>
    private sealed class OverrideRules(IRulesDatabase inner, params UnitRule[] overrides) : IRulesDatabase
    {
        private readonly Dictionary<string, UnitRule> byType = inner.All.ToDictionary(static r => r.TypeId)
            .Concat(overrides.ToDictionary(static r => r.TypeId))
            .GroupBy(static kv => kv.Key)
            .ToDictionary(static g => g.Key, static g => g.Last().Value);

        public string RulesetId => inner.RulesetId + "+overrides";

        public IReadOnlyCollection<UnitRule> All => byType.Values;

        public bool TryGet(string typeId, out UnitRule rule) => byType.TryGetValue(typeId, out rule!);

        public UnitRule Get(string typeId) => byType[typeId];

        public bool CanBuild(Faction faction, IReadOnlySet<string> ownedBuildingTypes, string typeId) => inner.CanBuild(faction, ownedBuildingTypes, typeId);

        public IReadOnlyList<string>? PathTo(Faction faction, IReadOnlySet<string> ownedBuildingTypes, string typeId) => inner.PathTo(faction, ownedBuildingTypes, typeId);

        public double Effectiveness(string attacker, string defender) => inner.Effectiveness(attacker, defender);
    }
}
