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
    public void Cancelling_production_refunds_the_full_cost()
    {
        TestRules rules = new();
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 11, maxSeconds: 30));
        PlayerId player = new(0);
        SimTestHelpers.DeployStartingMcv(sim, player);
        int creditsBefore = sim.Observe(player, ObservationMode.Oracle).Credits;

        sim.Submit(player, new ProduceCommand("test", TestRules.Barracks, QueueKind.Building));
        sim.Step();
        Assert.Equal(creditsBefore - rules.Get(TestRules.Barracks).Cost, sim.Observe(player, ObservationMode.Oracle).Credits);

        sim.Submit(player, new CancelProductionCommand("test", TestRules.Barracks, QueueKind.Building));
        sim.Step();
        Assert.Equal(creditsBefore, sim.Observe(player, ObservationMode.Oracle).Credits);
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
}
