// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Sim;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Sim;

/// <summary>
/// Benchmark knobs that make the simulator harder or less deterministic: per-player starting credits and income
/// multiplier (a handicap, like a retail "brutal" AI's bonus), and seeded combat noise.
/// </summary>
public sealed class SimHandicapTests
{
    private static int IncomeAfter(double incomeMultiplier)
    {
        TestRules rules = new();
        SimSettings settings = new(5, 300,
            [new SimPlayer(new PlayerId(0), Faction.Allied, IncomeMultiplier: incomeMultiplier), new SimPlayer(new PlayerId(1), Faction.Soviet)]);
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, settings);
        PlayerId player = new(0);
        SimTestHelpers.DeployStartingMcv(sim, player);
        SimTestHelpers.BuildBuilding(sim, rules, player, TestRules.Refinery);
        sim.Submit(player, new ProduceCommand("test", TestRules.Harvester, QueueKind.Vehicle));
        sim.Advance(20);
        int before = sim.Observe(player, ObservationMode.Oracle).Credits;
        sim.Advance(120);
        return sim.Observe(player, ObservationMode.Oracle).Credits - before;
    }

    [Fact]
    public void Income_multiplier_scales_what_harvesters_bank()
    {
        int normal = IncomeAfter(1.0);
        int boosted = IncomeAfter(1.5);
        Assert.True(normal > 0);
        Assert.Equal(normal * 1.5, boosted, 0);
    }

    [Fact]
    public void Starting_credits_can_differ_per_player()
    {
        SimSettings settings = new(1, 60,
            [new SimPlayer(new PlayerId(0), Faction.Allied), new SimPlayer(new PlayerId(1), Faction.Soviet, StartingCredits: 15_000)],
            StartingCredits: 10_000);
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), new TestRules(), settings);

        Assert.Equal(10_000, sim.Observe(new PlayerId(0)).Credits);
        Assert.Equal(15_000, sim.Observe(new PlayerId(1)).Credits);
    }

    private static string FightHash(int seed, double noise)
    {
        TestRules rules = new();
        SimMap map = TestMaps.TwoPlayerCombat();
        SkirmishSimulation sim = new(map, rules, SimTestHelpers.TwoPlayers(seed, 300) with { CombatNoise = noise });
        foreach (PlayerId p in new[] { new PlayerId(0), new PlayerId(1) })
        {
            SimTestHelpers.DeployStartingMcv(sim, p);
            SimTestHelpers.BuildBuilding(sim, rules, p, TestRules.WarFactory, buildSecondsBudget: 60);
            for (int i = 0; i < 3; i++)
            {
                sim.Submit(p, new ProduceCommand("test", TestRules.Strong, QueueKind.Vehicle));
                sim.Advance(6);
            }
        }
        Cell battlefield = map.Map.Regions.Single(r => r.Id == new RegionId(2)).Center;
        foreach (PlayerId p in new[] { new PlayerId(0), new PlayerId(1) })
        {
            List<EntityId> units = [.. sim.Observe(p, ObservationMode.Oracle).Entities.Where(e => e.Owner == p && e.TypeId == TestRules.Strong).Select(e => e.Id)];
            sim.Submit(p, new MoveCommand("test", units, battlefield));
        }
        sim.Advance(10);
        return sim.ComputeStateHash();
    }

    [Fact]
    public void Combat_noise_is_seeded_so_a_seed_replays_and_seeds_differ()
    {
        Assert.Equal(FightHash(3, 0.3), FightHash(3, 0.3));
        Assert.NotEqual(FightHash(3, 0.3), FightHash(11, 0.3));
        Assert.NotEqual(FightHash(3, 0.3), FightHash(3, 0.0));
        Assert.Equal(FightHash(3, 0.0), FightHash(3, 0.0));
    }
}
