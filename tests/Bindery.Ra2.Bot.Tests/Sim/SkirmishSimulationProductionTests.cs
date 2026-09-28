// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Sim;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Sim;

public sealed class SkirmishSimulationProductionTests
{
    // Invariant exercised: CanBuild prerequisites are enforced by the sim itself, not just by callers
    // being well-behaved. A war factory unit ordered with no war factory built must be rejected.
    [Fact]
    public void Producing_a_unit_without_its_prerequisite_building_is_rejected()
    {
        TestRules rules = new();
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 11, maxSeconds: 30));
        PlayerId player = new(0);
        SimTestHelpers.DeployStartingMcv(sim, player);

        int creditsBefore = sim.Observe(player, ObservationMode.Oracle).Credits;
        sim.Submit(player, new ProduceCommand("test", TestRules.Strong, QueueKind.Vehicle)); // needs a war factory
        sim.Step();

        Assert.Equal(1, sim.RejectedCommandCount(player));
        Assert.Equal(creditsBefore, sim.Observe(player, ObservationMode.Oracle).Credits);
        Assert.Empty(sim.Observe(player, ObservationMode.Oracle).Queues.SelectMany(q => q.Items));
    }

    [Fact]
    public void Producing_a_unit_with_its_prerequisites_met_is_accepted_and_eventually_spawns_it()
    {
        TestRules rules = new();
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 11, maxSeconds: 60));
        PlayerId player = new(0);
        SimTestHelpers.DeployStartingMcv(sim, player);
        SimTestHelpers.BuildBuilding(sim, rules, player, TestRules.Barracks);

        sim.Submit(player, new ProduceCommand("test", TestRules.Weak, QueueKind.Infantry));
        sim.Step();
        Assert.Equal(0, sim.RejectedCommandCount(player));

        sim.Advance(10);
        Assert.Contains(sim.Observe(player, ObservationMode.Oracle).Entities, e => e.Owner == player && e.TypeId == TestRules.Weak);
    }

    [Fact]
    public void Insufficient_credits_reject_production_without_spending_anything()
    {
        TestRules rules = new();
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 11, maxSeconds: 30, startingCredits: 50));
        PlayerId player = new(0);
        SimTestHelpers.DeployStartingMcv(sim, player);

        sim.Submit(player, new ProduceCommand("test", TestRules.Barracks, QueueKind.Building)); // costs 200
        sim.Step();

        Assert.Equal(1, sim.RejectedCommandCount(player));
        Assert.Equal(50, sim.Observe(player, ObservationMode.Oracle).Credits);
    }

    [Fact]
    public void Production_is_paid_as_it_builds_and_cancelling_refunds_what_was_paid()
    {
        // RA2 debits an item's cost gradually: ordering takes nothing, each second of building takes its share, and
        // a cancel gives back exactly what was taken.
        TestRules rules = new();
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 11, maxSeconds: 30));
        PlayerId player = new(0);
        SimTestHelpers.DeployStartingMcv(sim, player);
        int creditsBefore = sim.Observe(player, ObservationMode.Oracle).Credits;
        UnitRule barracks = rules.Get(TestRules.Barracks);

        sim.Submit(player, new ProduceCommand("test", TestRules.Barracks, QueueKind.Building));
        sim.Step();
        Assert.Equal(creditsBefore, sim.Observe(player, ObservationMode.Oracle).Credits);

        sim.Advance(1);
        QueueItem item = sim.Observe(player).Queues.Single(q => q.Kind == QueueKind.Building).Items.Single();
        Assert.InRange(item.Progress, 0.01, 0.99);
        Assert.Equal(creditsBefore - (int)Math.Round(barracks.Cost * item.Progress), sim.Observe(player, ObservationMode.Oracle).Credits);

        sim.Submit(player, new CancelProductionCommand("test", TestRules.Barracks, QueueKind.Building));
        sim.Step();
        Assert.Equal(creditsBefore, sim.Observe(player, ObservationMode.Oracle).Credits);
    }

    [Fact]
    public void Production_waits_for_money_and_resumes_when_it_comes()
    {
        TestRules rules = new();
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 11, maxSeconds: 60));
        PlayerId player = new(0);
        SimTestHelpers.DeployStartingMcv(sim, player);
        UnitRule barracks = rules.Get(TestRules.Barracks);
        int credits = sim.Observe(player, ObservationMode.Oracle).Credits;
        sim.Submit(player, new ProduceCommand("test", TestRules.Barracks, QueueKind.Building));
        sim.Step();
        // Leave a quarter of the price: the item builds to a quarter and stops there, with the purse at zero.
        sim.DebugAdjustCredits(player, (barracks.Cost / 4) - credits);
        sim.Advance(barracks.BuildSeconds * 2);
        QueueItem stalled = sim.Observe(player).Queues.Single(q => q.Kind == QueueKind.Building).Items.Single();
        Assert.Equal(0, sim.Observe(player, ObservationMode.Oracle).Credits);
        Assert.Equal(0.25, stalled.Progress, 2);
        Assert.False(stalled.Ready);

        sim.DebugAdjustCredits(player, barracks.Cost);
        sim.Advance(barracks.BuildSeconds + 1);
        Assert.True(sim.Observe(player).Queues.Single(q => q.Kind == QueueKind.Building).Items.Single().Ready);
        Assert.Equal(barracks.Cost / 4, sim.Observe(player, ObservationMode.Oracle).Credits);
    }

    // A refund for a building that is ready to place must take the placement away with it, or the building is free.
    [Fact]
    public void Cancelling_a_building_ready_to_place_refunds_it_and_it_can_no_longer_be_placed()
    {
        TestRules rules = new();
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 11, maxSeconds: 60));
        PlayerId player = new(0);
        SimTestHelpers.DeployStartingMcv(sim, player);
        int creditsBefore = sim.Observe(player, ObservationMode.Oracle).Credits;
        sim.Submit(player, new ProduceCommand("test", TestRules.Power, QueueKind.Building));
        sim.Advance(5);
        Assert.True(sim.Observe(player).Queues.Single(q => q.Kind == QueueKind.Building).Items.Single().Ready);

        sim.Submit(player, new CancelProductionCommand("test", TestRules.Power, QueueKind.Building));
        sim.Step();
        Assert.Equal(creditsBefore, sim.Observe(player, ObservationMode.Oracle).Credits);

        ObservedEntity yard = sim.Observe(player).Entities.Single(e => e.Owner == player && e.TypeId == TestRules.ConYard);
        sim.Submit(player, new PlaceBuildingCommand("test", TestRules.Power, new Cell(yard.Position.X + 2, yard.Position.Y)));
        sim.Step();

        Assert.Equal(1, sim.RejectedCommandCount(player));
        Assert.DoesNotContain(sim.Observe(player).Entities, e => e.TypeId == TestRules.Power);
    }

    // A queue is sped up only by factories of its own kind (RA2), and stops when its last factory is gone.
    [Fact]
    public void Only_a_queues_own_factories_speed_it_and_losing_them_stops_it()
    {
        TestRules rules = new();
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 11, maxSeconds: 120));
        PlayerId player = new(0);
        SimTestHelpers.DeployStartingMcv(sim, player);
        SimTestHelpers.BuildBuilding(sim, rules, player, TestRules.Power, buildSecondsBudget: 3);
        SimTestHelpers.BuildBuilding(sim, rules, player, TestRules.Barracks, buildSecondsBudget: 3);
        SimTestHelpers.BuildBuilding(sim, rules, player, TestRules.WarFactory, buildSecondsBudget: 3);
        Assert.Equal(3, sim.Observe(player).Entities.Count(e => rules.Get(e.TypeId).Role == UnitRole.Production));
        Assert.False(sim.Observe(player).Power.LowPower);

        // A 2 s power plant with one construction yard: half done after one second, whatever else stands.
        sim.Submit(player, new ProduceCommand("test", TestRules.Power, QueueKind.Building));
        sim.Step();
        sim.Advance(1);
        QueueItem item = sim.Observe(player).Queues.Single(q => q.Kind == QueueKind.Building).Items.Single();
        Assert.Equal(0.5, item.Progress, 3);
        Assert.Equal(1, sim.Observe(player).Queues.Single(q => q.Kind == QueueKind.Building).Factories);

        // Without its war factory the vehicle queue makes no progress and nothing appears.
        EntityId warFactory = sim.Observe(player).Entities.Single(e => e.TypeId == TestRules.WarFactory).Id;
        sim.Submit(player, new SellCommand("test", warFactory));
        sim.Step();
        sim.DebugEnqueue(player, QueueKind.Vehicle, TestRules.Strong);
        sim.Advance(5);
        Assert.Equal(0, sim.Observe(player).Queues.Single(q => q.Kind == QueueKind.Vehicle).Items.Single().Progress);
        Assert.DoesNotContain(sim.Observe(player).Entities, e => e.TypeId == TestRules.Strong);
    }

    // Invariant: a not-owned entity ID must be rejected, never quietly re-attributed to the caller.
    [Fact]
    public void Commands_on_an_entity_the_player_does_not_own_are_rejected()
    {
        TestRules rules = new();
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 11, maxSeconds: 30));
        PlayerId attacker = new(0);
        PlayerId victim = new(1);

        ObservationFrame oracle = sim.Observe(attacker, ObservationMode.Oracle);
        EntityId enemyMcv = oracle.Entities.Single(e => e.Owner == victim).Id;

        sim.Submit(attacker, new DeployCommand("test", enemyMcv));
        sim.Step();

        Assert.Equal(1, sim.RejectedCommandCount(attacker));
        Assert.False(sim.Observe(victim, ObservationMode.Oracle).Entities.Single(e => e.Id == enemyMcv).Deployed);
    }

    // Invariant: a unit whose rally cell lands outside every region (the map defines none at all) must still spawn
    // in its OWN player's start region, never in player 0's just because that is where the old fallback looked.
    [Fact]
    public void A_finished_unit_whose_rally_cell_is_outside_every_region_spawns_in_its_own_players_start_region()
    {
        NoRegionRules rules = new();
        SimMap map = TestMaps.NoRegions();
        SkirmishSimulation sim = new(map, rules, SimTestHelpers.TwoPlayers(seed: 11, maxSeconds: 30));
        PlayerId first = new(0);
        PlayerId second = new(1);

        // The rules declare no MCV and no weapon or sight on anything, so construction never resolves a region and
        // a match never needs fog or combat to resolve one either — the only thing this map's empty region list has
        // to support is the production spawn under test. Seed each side's base by hand: player 0 only needs to stay
        // alive (undefeated), player 1 needs a factory to build its infantry queue from.
        sim.DebugSpawnAt(first, NoRegionRules.Factory, new Cell(1, 1), map.StartRegions[0]);
        sim.DebugSpawnAt(second, NoRegionRules.Factory, new Cell(30, 30), map.StartRegions[1]);
        sim.DebugEnqueue(second, QueueKind.Infantry, NoRegionRules.Grunt);

        sim.Advance(3);

        ObservedEntity spawned = sim.Observe(second, ObservationMode.Oracle).Entities.Single(e => e.TypeId == NoRegionRules.Grunt);
        Assert.Equal(sim.StartRegionOf(second), sim.DebugRegionOf(spawned.Id));
        Assert.NotEqual(map.StartRegions[0], sim.DebugRegionOf(spawned.Id));
    }

    /// <summary>
    /// A minimal rules fixture with no MCV, one production building and one unit it can build, neither with a
    /// weapon or any sight — so a match on it never needs fog or combat resolution, both of which (unlike the spawn
    /// fallback under test) assume every live entity's region is one <see cref="MapInfo.RegionOf"/> can find again.
    /// Lets a test run to completion on a map that defines no regions at all.
    /// </summary>
    private sealed class NoRegionRules : IRulesDatabase
    {
        public const string Factory = "test-no-region-factory";
        public const string Grunt = "test-no-region-grunt";

        private static readonly IReadOnlyList<Faction> AnyFaction = [Faction.Allied, Faction.Soviet, Faction.Yuri];

        private readonly Dictionary<string, UnitRule> byType = new UnitRule[]
        {
            new(Factory, Factory, AnyFaction, EntityKind.Building, UnitRole.Production, QueueKind.Building,
                0, 1, 0, [], 0, 100, ArmorClass.Concrete, 0, WeaponClass.None, 0, 0, 0, AntiAir: false, Deployable: false),
            new(Grunt, Grunt, AnyFaction, EntityKind.Infantry, UnitRole.AntiInfantry, QueueKind.Infantry,
                10, 1, 0, [[Factory]], 1, 10, ArmorClass.None, 0, WeaponClass.None, 0, 3, 0, AntiAir: false, Deployable: false),
        }.ToDictionary(r => r.TypeId);

        public string RulesetId => "test-no-region-rules:v1";
        public IReadOnlyCollection<UnitRule> All => byType.Values;
        public bool TryGet(string typeId, out UnitRule rule) => byType.TryGetValue(typeId, out rule!);
        public UnitRule Get(string typeId) => byType[typeId];
        public bool CanBuild(Faction faction, IReadOnlySet<string> ownedBuildingTypes, string typeId) =>
            TryGet(typeId, out UnitRule rule) && rule.Prerequisites.All(group => group.Any(ownedBuildingTypes.Contains));
        public IReadOnlyList<string>? PathTo(Faction faction, IReadOnlySet<string> ownedBuildingTypes, string typeId) => null;
        public double Effectiveness(string attacker, string defender) => 1.0;
    }
}
