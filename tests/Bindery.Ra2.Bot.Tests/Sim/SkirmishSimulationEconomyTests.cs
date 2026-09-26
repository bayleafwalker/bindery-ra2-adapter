// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Sim;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Sim;

public sealed class SkirmishSimulationEconomyTests
{
    [Fact]
    public void A_refinery_and_harvester_generate_income_over_time()
    {
        TestRules rules = new();
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 5, maxSeconds: 200, startingCredits: 10_000));
        PlayerId player = new(0);

        SimTestHelpers.DeployStartingMcv(sim, player);
        SimTestHelpers.BuildBuilding(sim, rules, player, TestRules.Refinery);

        int creditsAfterRefinery = sim.Observe(player, ObservationMode.Oracle).Credits;
        Assert.Equal(10_000 - rules.Get(TestRules.Refinery).Cost, creditsAfterRefinery);

        sim.Submit(player, new ProduceCommand("test", TestRules.Harvester, QueueKind.Vehicle));
        sim.Advance(20); // production time budget

        int creditsAfterHarvesterBuilt = sim.Observe(player, ObservationMode.Oracle).Credits;

        // Give the harvester several full ore-run cycles (travel + 4s harvest + travel + 1s unload).
        sim.Advance(120);

        int finalCredits = sim.Observe(player, ObservationMode.Oracle).Credits;
        Assert.True(finalCredits > creditsAfterHarvesterBuilt,
            $"expected the harvester to have banked at least one load of ore; credits went from {creditsAfterHarvesterBuilt} to {finalCredits}");
    }

    // RA2 gives every refinery a free harvester (FreeUnit=): without it a base has no income until it also builds
    // a war factory and pays for a harvester, a starved economy retail never has.
    [Fact]
    public void A_placed_refinery_comes_with_a_free_harvester_that_starts_earning()
    {
        TestRules rules = new();
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 5, maxSeconds: 300, startingCredits: 10_000));
        PlayerId player = new(0);
        SimTestHelpers.DeployStartingMcv(sim, player);

        SimTestHelpers.BuildBuilding(sim, rules, player, TestRules.Refinery);

        ObservationFrame frame = sim.Observe(player, ObservationMode.Oracle);
        ObservedEntity refinery = frame.Entities.Single(e => e.Owner == player && e.TypeId == TestRules.Refinery);
        ObservedEntity harvester = Assert.Single(frame.Entities, e => e.Owner == player && e.TypeId == TestRules.Harvester);
        Assert.True(harvester.Position.DistanceTo(refinery.Position) <= 3);
        Assert.Equal(10_000 - rules.Get(TestRules.Refinery).Cost, frame.Credits); // free: nothing more is charged
        Assert.Contains(sim.Observe(player).Events, e => e.Kind == GameEventKind.EntityCreated && e.Entity == harvester.Id);

        int before = frame.Credits;
        sim.Advance(120);
        Assert.True(sim.Observe(player, ObservationMode.Oracle).Credits > before, "the free harvester must bank ore");
    }

    [Fact]
    public void The_free_harvester_can_be_switched_off()
    {
        TestRules rules = new();
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 5, maxSeconds: 300) with { FreeHarvesterWithRefinery = false });
        PlayerId player = new(0);
        SimTestHelpers.DeployStartingMcv(sim, player);

        SimTestHelpers.BuildBuilding(sim, rules, player, TestRules.Refinery);

        Assert.DoesNotContain(sim.Observe(player, ObservationMode.Oracle).Entities, e => e.TypeId == TestRules.Harvester);
    }

    // Forces the failure case: if ore never depleted, a single ore field could fund income forever from
    // the same trip without ever running low. Here we drain a small field and confirm it actually depletes.
    [Fact]
    public void Ore_fields_deplete_as_they_are_harvested()
    {
        TestRules rules = new();
        MapInfo smallField = TestMaps.TwoPlayerCombat().Map with
        {
            OreFields = [new OreField(new RegionId(1), new Cell(10, 0), InitialValue: 500, Gems: false)],
        };
        SimMap map = new(smallField, [new RegionId(0), new RegionId(3)]);
        SkirmishSimulation sim = new(map, rules, SimTestHelpers.TwoPlayers(seed: 5, maxSeconds: 200));
        PlayerId player = new(0);

        SimTestHelpers.DeployStartingMcv(sim, player);
        SimTestHelpers.BuildBuilding(sim, rules, player, TestRules.Refinery);
        sim.Submit(player, new ProduceCommand("test", TestRules.Harvester, QueueKind.Vehicle));
        sim.Advance(150);

        // A 500-credit field can fund at most one 500-credit load; a second identical field would not
        // exist to keep paying out, so credits must plateau rather than grow without bound.
        int creditsAfterFirstWindow = sim.Observe(player, ObservationMode.Oracle).Credits;
        sim.Advance(150);
        int creditsAfterSecondWindow = sim.Observe(player, ObservationMode.Oracle).Credits;

        Assert.Equal(creditsAfterFirstWindow, creditsAfterSecondWindow);
    }
}
