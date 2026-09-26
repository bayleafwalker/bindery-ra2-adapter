// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Operations;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Operations;

/// <summary>The vehicle queue keeps building harvesters up to the playbook's <c>harvesterTarget</c> (default three per refinery), and no further.</summary>
public sealed class HarvesterTargetTests
{
    private static string? Queued(int harvesters, double? harvesterTarget)
    {
        UnitRule refinery = Fixture.Building("refinery", UnitRole.Economy, cost: 2000);
        UnitRule factory = Fixture.Building("factory", UnitRole.Production, cost: 2000);
        UnitRule harvester = Fixture.Combat("harv", UnitRole.Harvester, QueueKind.Vehicle, 1400, 20) with { Prerequisites = [["refinery"], ["factory"]] };
        UnitRule tank = Fixture.Combat("tank", UnitRole.AntiArmor, QueueKind.Vehicle, 800, 10) with { Prerequisites = [["factory"]] };
        FakeRulesDatabase rules = new([refinery, factory, harvester, tank]);
        OperationalPlanner planner = new(rules, new FakePlaybookLibrary([]), new OperationalOptions());

        List<OwnEntity> own =
        [
            new(new EntityId(1), "refinery", UnitRole.Economy, EntityKind.Building, new Cell(10, 10), Fixture.Home, 1.0, 2000, false),
            new(new EntityId(2), "factory", UnitRole.Production, EntityKind.Building, new Cell(12, 10), Fixture.Home, 1.0, 2000, false),
        ];
        BeliefSnapshot belief = Fixture.Belief(credits: 20_000, own: own, queues: [new ProductionQueueState(QueueKind.Vehicle, [], Factories: 1)]);
        StrategicFeatures features = Fixture.Features(belief);
        features = features with { Economy = features.Economy with { Refineries = 1, Harvesters = harvesters } };
        StrategicIntent intent = Fixture.Intent(composition: [new CompositionTarget(UnitRole.AntiArmor, 0.5, 1.0)]);
        if (harvesterTarget is { } t) intent = intent with { PlaybookParameters = new Dictionary<string, double> { ["harvesterTarget"] = t } };
        return planner.Plan(belief, features, intent, new FakeLeaseManager())
            .ProductionCommands.OfType<ProduceCommand>().SingleOrDefault(static c => c.Queue == QueueKind.Vehicle)?.TypeId;
    }

    [Theory]
    [InlineData(0, null, "harv")]
    [InlineData(2, null, "harv")]
    [InlineData(3, null, "tank")]
    [InlineData(1, 2.0, "harv")]
    [InlineData(2, 2.0, "tank")]
    [InlineData(4, 5.0, "harv")]
    public void Harvesters_are_queued_until_the_target_and_not_beyond(int harvesters, double? target, string expected)
    {
        Assert.Equal(expected, Queued(harvesters, target));
    }
}
