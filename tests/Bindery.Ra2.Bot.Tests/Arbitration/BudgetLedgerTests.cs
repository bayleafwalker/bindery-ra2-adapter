// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arbitration;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arbitration;

public sealed class BudgetLedgerTests
{
    private static readonly BudgetShares Even = new(0.25, 0.25, 0.25, 0.25);

    [Fact]
    public void Capacity_is_credits_plus_one_period_of_forecast_income()
    {
        BudgetLedger ledger = new(new LedgerOptions(PlanningPeriodSeconds: 30));
        ledger.BeginPeriod(Fx.T(0), 1000, 600, new BudgetShares(0.5, 0.3, 0.2, 0));
        Assert.Equal(1300, ledger.Capacity);
        Assert.Equal(650, ledger.PoolCapacity(BudgetPools.Economy));
        Assert.Equal(390, ledger.PoolCapacity(BudgetPools.Army));
        Assert.Equal(260, ledger.PoolCapacity(BudgetPools.Tech));
        Assert.Equal(0, ledger.PoolCapacity(BudgetPools.Defense));
    }

    [Fact]
    public void Reserve_grants_at_most_what_is_available_and_try_reserve_is_all_or_nothing()
    {
        BudgetLedger ledger = new();
        ledger.BeginPeriod(Fx.T(0), 1000, 0, Even);
        Assert.Equal(250, ledger.Reserve(BudgetPools.Army, "ops", 400));
        Assert.Equal(0, ledger.Reserve(BudgetPools.Army, "ops", 1));
        Assert.False(ledger.TryReserve(BudgetPools.Tech, "ops", 251));
        Assert.Equal(0, ledger.Reserved(BudgetPools.Tech));
        Assert.True(ledger.TryReserve(BudgetPools.Tech, "ops", 250));
    }

    [Fact]
    public void Ledger_never_over_reserves_under_any_call_sequence()
    {
        Random random = new(12345);
        string[] controllers = ["ops", "tactics", "rogue"];
        for (int round = 0; round < 50; round++)
        {
            BudgetLedger ledger = new(new LedgerOptions(PlanningPeriodSeconds: random.Next(0, 20)));
            int credits = random.Next(-500, 4000);
            double income = random.Next(-100, 3000);
            ledger.BeginPeriod(Fx.T(round), credits, income,
                new BudgetShares(random.NextDouble(), random.NextDouble(), random.NextDouble(), random.NextDouble()));
            int limit = Math.Max(0, credits) + (int)Math.Floor(Math.Max(0, income) * ledger.Options.PlanningPeriodSeconds / 60);
            Assert.Equal(limit, ledger.Capacity);

            for (int step = 0; step < 200; step++)
            {
                string pool = BudgetPools.All[random.Next(4)];
                string controller = controllers[random.Next(controllers.Length)];
                int amount = random.Next(0, 2000);
                switch (random.Next(5))
                {
                    case 0: ledger.Reserve(pool, controller, amount); break;
                    case 1: ledger.TryReserve(pool, controller, amount); break;
                    case 2: ledger.Spend(pool, controller, amount); break;
                    case 3: ledger.TrySpendAny(controller, pool, amount); break;
                    default: ledger.Release(pool, controller, amount); break;
                }
                Assert.True(ledger.TotalReserved + ledger.TotalSpent <= limit,
                    $"round {round} step {step}: reserved {ledger.TotalReserved} + spent {ledger.TotalSpent} > {limit}");
                foreach (string p in BudgetPools.All)
                {
                    Assert.True(ledger.Reserved(p) + ledger.Spent(p) <= ledger.PoolCapacity(p));
                }
            }
        }
    }

    [Fact]
    public void Spending_cannot_exceed_the_reservation()
    {
        BudgetLedger ledger = new();
        ledger.BeginPeriod(Fx.T(0), 2000, 0, Even);
        ledger.Reserve(BudgetPools.Army, "ops", 500);
        Assert.False(ledger.Spend(BudgetPools.Army, "ops", 501));
        Assert.True(ledger.Spend(BudgetPools.Army, "ops", 500));
        Assert.False(ledger.Spend(BudgetPools.Army, "ops", 1));
        Assert.False(ledger.Spend(BudgetPools.Army, "other", 0));
        Assert.Equal(0, ledger.Available(BudgetPools.Army));
    }

    [Fact]
    public void Spend_any_uses_preferred_pool_first_then_the_others()
    {
        BudgetLedger ledger = new();
        ledger.BeginPeriod(Fx.T(0), 4000, 0, Even);
        ledger.Reserve(BudgetPools.Army, "ops", 500);
        ledger.Reserve(BudgetPools.Economy, "ops", 1000);
        Assert.True(ledger.TrySpendAny("ops", BudgetPools.Army, 900));
        Assert.Equal(0, ledger.Reserved(BudgetPools.Army, "ops"));
        Assert.Equal(600, ledger.Reserved(BudgetPools.Economy, "ops"));
        Assert.False(ledger.TrySpendAny("ops", BudgetPools.Army, 601));
        Assert.Equal(600, ledger.ReservedBy("ops"));
    }

    [Fact]
    public void Budget_leases_make_pools_single_owner()
    {
        LeaseManager leases = new();
        BudgetLedger ledger = new(null, leases);
        leases.TryAcquire(LeaseKey.Budget(BudgetPools.Army), "ops", 10, Fx.T(0), 0, 5);
        ledger.BeginPeriod(Fx.T(0), 4000, 0, Even);

        Assert.Equal(0, ledger.Reserve(BudgetPools.Army, "rogue", 100));
        Assert.False(ledger.HasAccount("rogue"));
        Assert.Equal(100, ledger.Reserve(BudgetPools.Army, "ops", 100));
        Assert.Equal(0, ledger.Reserve(BudgetPools.Tech, "ops", 100));
    }

    [Fact]
    public void New_period_clears_reservations_and_unknown_pool_throws()
    {
        BudgetLedger ledger = new();
        ledger.BeginPeriod(Fx.T(0), 1000, 0, Even);
        ledger.Reserve(BudgetPools.Army, "ops", 100);
        ledger.BeginPeriod(Fx.T(1), 1000, 0, Even);
        Assert.Equal(0, ledger.TotalReserved);
        Assert.False(ledger.HasAccount("ops"));
        Assert.Throws<ArgumentException>(() => ledger.Reserve("navy", "ops", 1));
        Assert.Equal(BudgetPools.Army, BudgetPools.Normalise("ARMY"));
    }
}
