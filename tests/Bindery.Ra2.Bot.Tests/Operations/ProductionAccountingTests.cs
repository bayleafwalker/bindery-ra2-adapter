// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Operations;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Operations;

/// <summary>
/// The planner's money and queues as the game reports them: where production is paid as it builds, credits on hand
/// still include what queued items owe; and where the source reports no queues, an absent queue is not an idle one.
/// </summary>
public sealed class ProductionAccountingTests
{
    private static readonly UnitRule Barracks = Fixture.Building("barracks", UnitRole.Production, cost: 500);
    private static readonly UnitRule Refinery = Fixture.Building("refinery", UnitRole.Economy, cost: 2000, buildSeconds: 30);
    private static readonly UnitRule Harvester = Fixture.Combat("harv", UnitRole.Harvester, QueueKind.Vehicle, 1400, 20) with { Prerequisites = [["refinery"]] };

    private static OperationalPlanner Planner(bool chargedWhileBuilding = true) =>
        new(new FakeRulesDatabase([Barracks, Refinery, Harvester]), new FakePlaybookLibrary([]),
            new OperationalOptions(ProductionChargedWhileBuilding: chargedWhileBuilding));

    private static OperationalPlan Plan(OperationalPlanner planner, BeliefSnapshot belief)
    {
        StrategicFeatures features = Fixture.Features(belief);
        features = features with { Economy = features.Economy with { Refineries = 0, Harvesters = 0, IncomePerMinute = Trend.Flat(500) } };
        return planner.Plan(belief, features, Fixture.Intent(), new FakeLeaseManager());
    }

    [Fact]
    public void Credits_owed_to_queued_production_are_not_spent_again()
    {
        // 2400 on hand, but a half-built harvester still owes 700: only 1700 is free, less than the refinery.
        IReadOnlyList<ProductionQueueState> queues =
        [
            new(QueueKind.Building, [], Factories: 1),
            new(QueueKind.Vehicle, [new QueueItem("harv", 0.5, false, false)], Factories: 1),
        ];
        BeliefSnapshot belief = Fixture.Belief(credits: 2400, queues: queues);

        Assert.Empty(Plan(Planner(), belief).ProductionCommands.OfType<ProduceCommand>());
        // A source that debits the whole price on order has nothing more to subtract.
        Assert.Equal("refinery", Assert.Single(Plan(Planner(chargedWhileBuilding: false), belief).ProductionCommands.OfType<ProduceCommand>()).TypeId);
    }

    [Fact]
    public void Unreported_queues_are_ordered_into_once_per_build_time_not_every_pass()
    {
        OperationalPlanner planner = Planner();
        BeliefSnapshot At(double seconds) => Fixture.Belief(credits: 2400, time: GameTime.FromSeconds(seconds)) with { QueuesKnown = false };

        Assert.Equal("refinery", Assert.Single(Plan(planner, At(0)).ProductionCommands.OfType<ProduceCommand>()).TypeId);
        // Nothing reported still means nothing known: the planner's own order keeps the building queue busy.
        Assert.Empty(Plan(planner, At(1)).ProductionCommands.OfType<ProduceCommand>());
        Assert.Empty(Plan(planner, At(Refinery.BuildSeconds)).ProductionCommands.OfType<ProduceCommand>());
        // Once the item should long be done, the queue is free again.
        Assert.Single(Plan(planner, At(Refinery.BuildSeconds + 10)).ProductionCommands.OfType<ProduceCommand>());
    }

    [Fact]
    public void Reported_empty_queues_are_free()
    {
        OperationalPlanner planner = Planner();
        BeliefSnapshot At(double seconds) => Fixture.Belief(credits: 2400, time: GameTime.FromSeconds(seconds), queues: [new(QueueKind.Building, [], Factories: 1)]);

        Assert.Single(Plan(planner, At(0)).ProductionCommands.OfType<ProduceCommand>());
        Assert.Single(Plan(planner, At(1)).ProductionCommands.OfType<ProduceCommand>());
    }

    [Fact]
    public void Unpaid_production_is_cost_times_what_is_left_of_each_unfinished_item()
    {
        FakeRulesDatabase rules = new([Barracks, Refinery, Harvester]);
        IReadOnlyList<ProductionQueueState> queues =
        [
            new(QueueKind.Building, [new QueueItem("refinery", 1.0, true, false), new QueueItem("barracks", 0.2, false, true)], 1),
            new(QueueKind.Vehicle, [new QueueItem("harv", 0.25, false, false), new QueueItem("unknown", 0, false, true)], 1),
        ];

        Assert.Equal(400 + 1050, ProductionDebt.Unpaid(rules, queues));
    }
}
