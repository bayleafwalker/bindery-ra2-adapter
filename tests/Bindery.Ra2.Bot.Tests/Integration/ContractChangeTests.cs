// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Arbitration;
using Bindery.Ra2.Bot.Belief;
using Bindery.Ra2.Bot.Features;
using Bindery.Ra2.Bot.Sim;
using Bindery.Ra2.Bot.Tests.Arbitration;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Integration;

/// <summary>Tests for the integration-time contract and ledger changes.</summary>
public sealed class ContractChangeTests
{
    [Fact]
    public void BotJson_serialises_region_keyed_dictionaries_and_non_finite_numbers()
    {
        StrategicFeatures features = Fx.Features(100) with
        {
            MapControl = new MapControlFeatures(new Dictionary<RegionId, RegionControl> { [Fx.R1] = RegionControl.Enemy }, [Fx.R2], 0.5),
            Scouting = new ScoutingFeatures(0.5, new Dictionary<RegionId, double> { [Fx.R3] = double.PositiveInfinity }, []),
        };
        string json = JsonSerializer.Serialize(features, BotJson.Options);
        Assert.Contains("\"1\":\"Enemy\"", json, StringComparison.Ordinal);
        Assert.Contains("\"3\":\"Infinity\"", json, StringComparison.Ordinal);
        // Keys and named literals read back too (StrategicFeatures itself holds an IReadOnlySet, which
        // System.Text.Json cannot construct, so the parts are read individually).
        using JsonDocument doc = JsonDocument.Parse(json);
        Dictionary<RegionId, RegionControl> control = doc.RootElement.GetProperty("mapControl").GetProperty("control").Deserialize<Dictionary<RegionId, RegionControl>>(BotJson.Options)!;
        Assert.Equal(RegionControl.Enemy, control[Fx.R1]);
        Dictionary<RegionId, double> ages = doc.RootElement.GetProperty("scouting").GetProperty("regionAgeSeconds").Deserialize<Dictionary<RegionId, double>>(BotJson.Options)!;
        Assert.True(double.IsPositiveInfinity(ages[Fx.R3]));
        Assert.Equal(Fx.R2, doc.RootElement.GetProperty("mapControl").GetProperty("expansionCandidates")[0].Deserialize<RegionId>(BotJson.Options));
    }

    [Fact]
    public void Visible_ore_is_remembered_in_belief_and_drives_the_ore_remaining_fraction()
    {
        SimMap map = SimMaps.TwinValley;
        SkirmishSimulation sim = new(map, MatchHarness.Rules, new SimSettings(1, 600, [new SimPlayer(new PlayerId(0), Faction.Allied), new SimPlayer(new PlayerId(1), Faction.Soviet)]));
        ObservationFrame frame = sim.Observe(new PlayerId(0));
        Assert.NotNull(frame.OreRemaining);
        Assert.All(frame.OreRemaining!.Keys, r => Assert.Contains(r, frame.VisibleRegions));

        BeliefModel belief = new(MatchHarness.Rules, new BeliefOptions());
        OreField field = map.Map.OreFields[0];
        ObservationFrame depleted = frame with { OreRemaining = new Dictionary<RegionId, int> { [field.Region] = 0 } };
        BeliefSnapshot snapshot = belief.Apply(depleted);
        Assert.Equal(0, snapshot.OreLastSeen![field.Region]);
        // Out of sight on the next frame: the memory stays.
        snapshot = belief.Apply(depleted with { Time = depleted.Time.Plus(1), OreRemaining = new Dictionary<RegionId, int>() });
        Assert.Equal(0, snapshot.OreLastSeen![field.Region]);

        StrategicFeatures features = new FeatureCompiler(MatchHarness.Rules, new FeatureOptions()).Compile(snapshot);
        double total = map.Map.OreFields.Sum(static o => (double)o.InitialValue);
        double expected = (total - map.Map.OreFields.Where(o => o.Region == field.Region).Sum(static o => (double)o.InitialValue)) / total;
        Assert.Equal(expected, features.Economy.OreRemainingFraction, 6);
    }

    [Fact]
    public void Accruing_ledger_saves_each_pool_its_share_of_new_credits_and_charges_spending_to_the_pool()
    {
        BudgetLedger ledger = new(new LedgerOptions(1.0, AccrueByShare: true));
        BudgetShares shares = new(0.5, 0.5, 0, 0);
        ledger.BeginPeriod(Fx.T(0), 1000, 0, shares);
        Assert.Equal(500, ledger.PoolCapacity(BudgetPools.Economy));
        Assert.Equal(500, ledger.PoolCapacity(BudgetPools.Army));

        Assert.Equal(400, ledger.Reserve(BudgetPools.Army, "ops", 400));
        Assert.True(ledger.Spend(BudgetPools.Army, "ops", 400));
        // The game charges 400; 200 of income arrives, split by share.
        ledger.BeginPeriod(Fx.T(1), 800, 0, shares);
        Assert.Equal(600, ledger.PoolCapacity(BudgetPools.Economy));
        Assert.Equal(200, ledger.PoolCapacity(BudgetPools.Army));
        Assert.Equal(800, BudgetPools.All.Sum(ledger.PoolCapacity));
    }

    [Fact]
    public void Borrowing_moves_unreserved_balance_without_exceeding_capacity()
    {
        BudgetLedger ledger = new(new LedgerOptions(1.0, AccrueByShare: true));
        ledger.BeginPeriod(Fx.T(0), 1000, 0, new BudgetShares(0.2, 0.8, 0, 0));
        Assert.Equal(200, ledger.Reserve(BudgetPools.Economy, "ops", 700));
        Assert.Equal(500, ledger.ReserveWithBorrowing(BudgetPools.Economy, "ops", 500));
        Assert.Equal(700, ledger.Reserved(BudgetPools.Economy, "ops"));
        Assert.Equal(300, ledger.ReserveWithBorrowing(BudgetPools.Army, "ops", 999));
        Assert.Equal(1000, ledger.TotalReserved);
        Assert.Equal(0, ledger.ReserveWithBorrowing(BudgetPools.Tech, "ops", 1));
    }

    [Fact]
    public void A_timeout_with_equal_assets_is_a_draw()
    {
        SkirmishSimulation sim = new(SimMaps.TwinValley, MatchHarness.Rules, new SimSettings(1, 2, [new SimPlayer(new PlayerId(0), Faction.Allied), new SimPlayer(new PlayerId(1), Faction.Allied)]));
        while (!sim.MatchEnded) sim.Step();
        Assert.Equal("timeout", sim.EndReason);
        Assert.Null(sim.Winner);
    }
}
