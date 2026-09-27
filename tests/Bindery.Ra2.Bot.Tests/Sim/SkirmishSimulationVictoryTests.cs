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
        PlayerId richer = new(0);
        PlayerId poorer = new(1);
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules,
            new SimSettings(23, 1, [new SimPlayer(richer, Faction.Allied), new SimPlayer(poorer, Faction.Soviet, StartingCredits: 9_000)]));
        SimTestHelpers.DeployStartingMcv(sim, richer);
        SimTestHelpers.DeployStartingMcv(sim, poorer);
        // Paid-for production still counts as assets (it does not make a player poorer).
        sim.Submit(poorer, new ProduceCommand("test", TestRules.Power, QueueKind.Building));

        sim.Advance(2);

        Assert.True(sim.MatchEnded);
        Assert.Equal("timeout", sim.EndReason);
        Assert.True(sim.AssetValue(richer) > sim.AssetValue(poorer));
        Assert.Equal(richer, sim.Winner);
    }

    // The timeout winner is the larger asset value, so value must not vanish when an MCV deploys (the yard is the
    // MCV) or when credits are spent on production that has not finished yet (the queue holds what was paid).
    [Fact]
    public void Asset_value_survives_deploying_the_mcv_and_paying_for_production()
    {
        TestRules rules = new();
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 3, maxSeconds: 60));
        PlayerId player = new(0);
        int start = sim.AssetValue(player);
        Assert.Equal(10_000 + rules.Get(TestRules.Mcv).Cost, start);

        SimTestHelpers.DeployStartingMcv(sim, player);
        Assert.Equal(start, sim.AssetValue(player));

        sim.Submit(player, new ProduceCommand("test", TestRules.Power, QueueKind.Building));
        sim.Step();
        Assert.Equal(start, sim.AssetValue(player));
        sim.Advance(5); // finished, waiting for placement
        Assert.Equal(start, sim.AssetValue(player));
    }
}
