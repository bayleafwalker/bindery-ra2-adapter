// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Sim;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Sim;

public sealed class SkirmishSimulationVictoryTests
{
    [Fact]
    public void A_player_with_no_buildings_and_no_mcv_is_defeated_and_the_other_wins()
    {
        TestRules rules = new();
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 23, maxSeconds: 30));
        PlayerId survivor = new(0);
        PlayerId loser = new(1);

        // Deploy both MCVs (an undeployed MCV alone would keep a player alive), then sell the loser's
        // resulting construction yard, leaving it with no buildings and no MCV.
        SimTestHelpers.DeployStartingMcv(sim, survivor);
        SimTestHelpers.DeployStartingMcv(sim, loser);
        EntityId loserYard = sim.Observe(loser, ObservationMode.Oracle).Entities.Single(e => e.Owner == loser).Id;
        sim.Submit(loser, new SellCommand("test", loserYard));
        sim.Step();

        Assert.True(sim.MatchEnded);
        Assert.Equal(survivor, sim.Winner);
        Assert.Equal("elimination", sim.EndReason);
    }

    // Forces the failure case: while a player still has a live, undeployed MCV they are not defeated,
    // even with zero buildings.
    [Fact]
    public void An_undeployed_mcv_alone_is_enough_to_avoid_defeat()
    {
        TestRules rules = new();
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 23, maxSeconds: 2));
        sim.Step();

        Assert.False(sim.MatchEnded);
    }

    [Fact]
    public void A_match_with_no_elimination_ends_at_max_seconds_as_a_timeout_scored_by_asset_value()
    {
        TestRules rules = new();
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 23, maxSeconds: 1));
        PlayerId richer = new(0);
        PlayerId poorer = new(1);
        SimTestHelpers.DeployStartingMcv(sim, richer);
        SimTestHelpers.DeployStartingMcv(sim, poorer);
        sim.Submit(poorer, new ProduceCommand("test", TestRules.Power, QueueKind.Building)); // spends credits, lowering poorer's asset value

        sim.Advance(2);

        Assert.True(sim.MatchEnded);
        Assert.Equal("timeout", sim.EndReason);
        Assert.True(sim.AssetValue(richer) >= sim.AssetValue(poorer));
        Assert.Equal(richer, sim.Winner);
    }
}
