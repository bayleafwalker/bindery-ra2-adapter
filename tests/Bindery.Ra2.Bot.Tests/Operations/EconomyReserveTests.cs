// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Operations;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Operations;

/// <summary>
/// The economy reserve protects the first refinery; it must never be what stops the refinery being built. With
/// money for the refinery but not for barracks plus refinery, the barracks-first opening yields to the refinery
/// instead of being refused every plan while nothing is queued (no income ever arrives then).
/// </summary>
public sealed class EconomyReserveTests
{
    private static OperationalPlan Plan(int credits, double income = 500)
    {
        UnitRule barracks = Fixture.Building("barracks", UnitRole.Production, cost: 500);
        UnitRule refinery = Fixture.Building("refinery", UnitRole.Economy, cost: 2000);
        UnitRule gi = Fixture.Combat("gi", UnitRole.AntiInfantry, QueueKind.Infantry, 200, 5) with { Prerequisites = [["barracks"]] };
        UnitRule harvester = Fixture.Combat("harv", UnitRole.Harvester, QueueKind.Vehicle, 1400, 20) with { Prerequisites = [["refinery"]] };
        FakeRulesDatabase rules = new([barracks, refinery, gi, harvester]);
        OperationalPlanner planner = new(rules, new FakePlaybookLibrary([]), new OperationalOptions());

        BeliefSnapshot belief = Fixture.Belief(credits: credits, queues: [new ProductionQueueState(QueueKind.Building, [], Factories: 1)]);
        StrategicFeatures features = Fixture.Features(belief);
        features = features with { Economy = features.Economy with { Refineries = 0, Harvesters = 0, IncomePerMinute = Trend.Flat(income) } };
        return planner.Plan(belief, features, Fixture.Intent(), new FakeLeaseManager());
    }

    [Fact]
    public void With_money_for_the_refinery_only_the_refinery_is_queued_not_nothing()
    {
        OperationalPlan plan = Plan(credits: 2400);

        Assert.Equal("refinery", Assert.Single(plan.ProductionCommands.OfType<ProduceCommand>()).TypeId);
    }

    [Fact]
    public void With_money_for_both_the_barracks_still_opens()
    {
        OperationalPlan plan = Plan(credits: 2600);

        Assert.Equal("barracks", Assert.Single(plan.ProductionCommands.OfType<ProduceCommand>()).TypeId);
    }

    [Fact]
    public void A_reserve_that_can_never_be_paid_does_not_idle_the_base()
    {
        // Less than the refinery costs and no income: saving for it would wait forever.
        OperationalPlan plan = Plan(credits: 1700, income: 0);

        Assert.Equal("barracks", Assert.Single(plan.ProductionCommands.OfType<ProduceCommand>()).TypeId);
    }
}
