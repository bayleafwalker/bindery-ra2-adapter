// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Sim;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Sim;

public sealed class SkirmishSimulationCombatTests
{
    [Fact]
    public void A_stronger_force_destroys_a_weaker_one_in_the_same_region()
    {
        TestRules rules = new();
        SimMap map = TestMaps.TwoPlayerCombat();
        SkirmishSimulation sim = new(map, rules, SimTestHelpers.TwoPlayers(seed: 13, maxSeconds: 300));
        PlayerId strongSide = new(0);
        PlayerId weakSide = new(1);
        RegionId battlefield = new(2);
        Cell battlefieldCenter = map.Map.Regions.Single(r => r.Id == battlefield).Center;

        // Bypass the economy/build chain (irrelevant to this invariant) by dropping pre-built forces
        // directly into the shared battlefield region via a move order from each start region.
        SimTestHelpers.DeployStartingMcv(sim, strongSide);
        SimTestHelpers.BuildBuilding(sim, rules, strongSide, TestRules.WarFactory, buildSecondsBudget: 60);
        for (int i = 0; i < 3; i++)
        {
            sim.Submit(strongSide, new ProduceCommand("test", TestRules.Strong, QueueKind.Vehicle));
            sim.Advance(6);
        }

        SimTestHelpers.DeployStartingMcv(sim, weakSide);
        SimTestHelpers.BuildBuilding(sim, rules, weakSide, TestRules.Barracks, buildSecondsBudget: 60);
        for (int i = 0; i < 3; i++)
        {
            sim.Submit(weakSide, new ProduceCommand("test", TestRules.Weak, QueueKind.Infantry));
            sim.Advance(4);
        }

        List<EntityId> strongUnits = [.. sim.Observe(strongSide, ObservationMode.Oracle).Entities.Where(e => e.TypeId == TestRules.Strong).Select(e => e.Id)];
        List<EntityId> weakUnits = [.. sim.Observe(weakSide, ObservationMode.Oracle).Entities.Where(e => e.TypeId == TestRules.Weak).Select(e => e.Id)];
        Assert.Equal(3, strongUnits.Count);
        Assert.Equal(3, weakUnits.Count);

        sim.Submit(strongSide, new MoveCommand("test", strongUnits, battlefieldCenter));
        sim.Submit(weakSide, new MoveCommand("test", weakUnits, battlefieldCenter));
        sim.Advance(30);

        ObservationFrame finalWeakView = sim.Observe(weakSide, ObservationMode.Oracle);
        Assert.DoesNotContain(finalWeakView.Entities, e => e.TypeId == TestRules.Weak);
        Assert.Contains(finalWeakView.Entities, e => e.TypeId == TestRules.Strong && e.Owner == strongSide);
    }

    // Forces the failure case: an anti-air-only unit must not be able to damage a ground unit sharing its region.
    [Fact]
    public void An_anti_air_only_unit_cannot_hit_ground_targets()
    {
        TestRules rules = new();
        Assert.True(rules.TryGet(TestRules.AntiAirOnly, out UnitRule aaRule));
        Assert.True(aaRule.AntiAir);

        SimMap map = TestMaps.TwoPlayerCombat();
        SkirmishSimulation sim = new(map, rules, SimTestHelpers.TwoPlayers(seed: 17, maxSeconds: 300));
        PlayerId aaSide = new(0);
        PlayerId groundSide = new(1);

        SimTestHelpers.DeployStartingMcv(sim, aaSide);
        SimTestHelpers.BuildBuilding(sim, rules, aaSide, TestRules.WarFactory, buildSecondsBudget: 60);
        sim.Submit(aaSide, new ProduceCommand("test", TestRules.AntiAirOnly, QueueKind.Vehicle));
        sim.Advance(6);

        SimTestHelpers.DeployStartingMcv(sim, groundSide);
        SimTestHelpers.BuildBuilding(sim, rules, groundSide, TestRules.Barracks, buildSecondsBudget: 60);
        sim.Submit(groundSide, new ProduceCommand("test", TestRules.Weak, QueueKind.Infantry));
        sim.Advance(4);

        EntityId aaUnit = sim.Observe(aaSide, ObservationMode.Oracle).Entities.Single(e => e.TypeId == TestRules.AntiAirOnly).Id;
        EntityId groundUnit = sim.Observe(groundSide, ObservationMode.Oracle).Entities.Single(e => e.TypeId == TestRules.Weak).Id;
        RegionId battlefield = new(2);
        Cell battlefieldCenter = map.Map.Regions.Single(r => r.Id == battlefield).Center;

        sim.Submit(aaSide, new MoveCommand("test", [aaUnit], battlefieldCenter));
        sim.Submit(groundSide, new MoveCommand("test", [groundUnit], battlefieldCenter));
        sim.Advance(15);

        // The ground unit's weapon (anti-infantry) can still hit the AA vehicle, but the AA-only unit
        // can never damage the infantry: it must be the only unit dealing damage in the region, so the
        // infantry must still be at full health after 15 seconds sharing a region with it.
        ObservedEntity survivor = sim.Observe(groundSide, ObservationMode.Oracle).Entities.Single(e => e.Id == groundUnit);
        Assert.Equal(1.0, survivor.HealthFraction);
    }

    [Fact]
    public void Selling_a_building_refunds_half_its_cost_and_removes_it()
    {
        TestRules rules = new();
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 19, maxSeconds: 120));
        PlayerId player = new(0);
        SimTestHelpers.DeployStartingMcv(sim, player);
        SimTestHelpers.BuildBuilding(sim, rules, player, TestRules.Barracks);

        int creditsBeforeSell = sim.Observe(player, ObservationMode.Oracle).Credits;
        EntityId barracks = sim.Observe(player, ObservationMode.Oracle).Entities.Single(e => e.TypeId == TestRules.Barracks).Id;

        sim.Submit(player, new SellCommand("test", barracks));
        sim.Step();

        Assert.Equal(creditsBeforeSell + rules.Get(TestRules.Barracks).Cost / 2, sim.Observe(player, ObservationMode.Oracle).Credits);
        Assert.DoesNotContain(sim.Observe(player, ObservationMode.Oracle).Entities, e => e.TypeId == TestRules.Barracks);
    }
}
