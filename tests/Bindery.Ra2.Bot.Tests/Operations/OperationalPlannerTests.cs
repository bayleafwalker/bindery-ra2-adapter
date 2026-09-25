// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Operations;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Operations;

public sealed class OperationalPlannerTests
{
    [Fact]
    public void Plan_NeverQueuesAnUnbuildableType()
    {
        UnitRule tankWeak = Fixture.Combat("tank-basic", UnitRole.AntiArmor, QueueKind.Vehicle, 800, 6);
        UnitRule tankElite = new(
            "tank-elite", "tank-elite", [Faction.Allied], EntityKind.Vehicle, UnitRole.AntiArmor, QueueKind.Vehicle,
            900, 6, -5, [["battlelab"]], 3, 400, ArmorClass.Heavy, 20, WeaponClass.AntiArmor, 6, 5, 6, false, false);
        FakeRulesDatabase rules = new([tankWeak, tankElite], new()
        {
            [("tank-basic", "enemy-heavy")] = 1.0,
            [("tank-elite", "enemy-heavy")] = 5.0, // strictly more effective, but unbuildable (missing battlelab)
        });
        FakePlaybookLibrary playbooks = new([]);
        OperationalPlanner planner = new(rules, playbooks, new OperationalOptions());

        BeliefSnapshot belief = Fixture.Belief(
            queues: [new ProductionQueueState(QueueKind.Vehicle, [], Factories: 1), new ProductionQueueState(QueueKind.Building, [], Factories: 1)]);
        StrategicFeatures features = Fixture.Features(belief, enemyComposition: new Dictionary<UnitRole, double> { [UnitRole.AntiArmor] = 1.0 });
        StrategicIntent intent = Fixture.Intent(composition: [new CompositionTarget(UnitRole.AntiArmor, 0.6, 1.0)]);

        OperationalPlan plan = planner.Plan(belief, features, intent, new FakeLeaseManager());

        IReadOnlyList<string> produced = plan.ProductionCommands.OfType<ProduceCommand>().Select(c => c.TypeId).ToList();
        Assert.DoesNotContain("tank-elite", produced);
        foreach (string typeId in produced)
        {
            Assert.True(rules.CanBuild(belief.Faction, belief.OwnBuildingTypes, typeId), $"{typeId} was queued but is not buildable");
        }
    }

    [Fact]
    public void Plan_BuildsPowerFirstWhenSurplusCannotCoverTheNextBuilding()
    {
        UnitRule powerplant = Fixture.Building("powerplant", UnitRole.Power, power: 100);
        UnitRule techcenter = Fixture.Building("techcenter", UnitRole.Tech, power: -30);
        FakeRulesDatabase rules = new([powerplant, techcenter]);
        Playbook playbook = new(
            "test-playbook", "test", [Faction.Allied], StrategicPosture.Tech, new BudgetShares(0.2, 0.3, 0.4, 0.1),
            [], ["techcenter"], [], [], [], 30);
        FakePlaybookLibrary playbooks = new([playbook]);
        OperationalPlanner planner = new(rules, playbooks, new OperationalOptions());

        BeliefSnapshot belief = Fixture.Belief(
            power: new PowerState(60, 50), // surplus 10, techcenter needs 30 => must build power first
            queues: [new ProductionQueueState(QueueKind.Building, [], Factories: 1)]);
        StrategicFeatures features = Fixture.Features(belief);
        StrategicIntent intent = Fixture.Intent(budget: new BudgetShares(0.1, 0.1, 0.7, 0.1));

        OperationalPlan plan = planner.Plan(belief, features, intent, new FakeLeaseManager());

        ProduceCommand building = Assert.Single(plan.ProductionCommands.OfType<ProduceCommand>());
        Assert.Equal("powerplant", building.TypeId);
    }

    [Fact]
    public void Plan_PicksTheMoreEffectiveCounterForTheLargestCompositionGap()
    {
        UnitRule tankWeak = Fixture.Combat("tank-weak", UnitRole.AntiArmor, QueueKind.Vehicle, 800, 6);
        UnitRule tankStrong = Fixture.Combat("tank-strong", UnitRole.AntiArmor, QueueKind.Vehicle, 800, 6);
        UnitRule enemyHeavy = Fixture.Combat("enemy-heavy", UnitRole.AntiArmor, QueueKind.Vehicle, 800, 6);
        FakeRulesDatabase rules = new([tankWeak, tankStrong, enemyHeavy], new()
        {
            [("tank-weak", "enemy-heavy")] = 0.5,
            [("tank-strong", "enemy-heavy")] = 2.0,
        });
        FakePlaybookLibrary playbooks = new([]);
        OperationalPlanner planner = new(rules, playbooks, new OperationalOptions());

        BeliefSnapshot belief = Fixture.Belief(queues: [new ProductionQueueState(QueueKind.Vehicle, [], Factories: 1)]);
        StrategicFeatures features = Fixture.Features(
            belief,
            valueByRole: new Dictionary<UnitRole, double>(),
            enemyComposition: new Dictionary<UnitRole, double> { [UnitRole.AntiArmor] = 1.0 },
            knownTech: new HashSet<string> { "enemy-heavy" });
        StrategicIntent intent = Fixture.Intent(composition: [new CompositionTarget(UnitRole.AntiArmor, 0.6, 1.0)]);

        OperationalPlan plan = planner.Plan(belief, features, intent, new FakeLeaseManager());

        ProduceCommand produced = Assert.Single(plan.ProductionCommands.OfType<ProduceCommand>());
        Assert.Equal("tank-strong", produced.TypeId);
    }

    [Fact]
    public void Plan_StagesAnAttackUntilConditionsHoldThenCommits()
    {
        UnitRule tank = Fixture.Combat("tank", UnitRole.AntiArmor, QueueKind.Vehicle, 800, 6);
        FakeRulesDatabase rules = new([tank]);
        FakePlaybookLibrary playbooks = new([]);
        OperationalPlanner planner = new(rules, playbooks, new OperationalOptions());
        FakeLeaseManager leases = new();

        List<OwnEntity> own =
        [
            new OwnEntity(new EntityId(1), "tank", UnitRole.AntiArmor, EntityKind.Vehicle, new Cell(10, 10), Fixture.Home, 1.0, 800, false),
            new OwnEntity(new EntityId(2), "cy", UnitRole.Production, EntityKind.Building, new Cell(10, 10), Fixture.Home, 1.0, 3000, false),
        ];
        Objective attack = new(ObjectiveKind.AttackRegion, Fixture.Front, null, 1);
        Condition condition = new(ConditionMetric.OwnArmyValue, Comparison.Gt, 1000);

        BeliefSnapshot notReadyBelief = Fixture.Belief(own: own);
        StrategicFeatures weakFeatures = Fixture.Features(notReadyBelief);
        StrategicIntent intent = Fixture.Intent(objectives: [attack], attackConditions: [condition]);

        OperationalPlan staged = planner.Plan(notReadyBelief, weakFeatures, intent, leases);
        // Staging holds at home (engaged, so a staging army still defends itself) and is not an attack.
        SquadOrder stagingOrder = Assert.Single(staged.Squads);
        Assert.NotEqual(ObjectiveKind.AttackRegion, stagingOrder.Objective);
        Assert.Equal(Fixture.Home, stagingOrder.TargetRegion);

        BeliefSnapshot readyBelief = Fixture.Belief(own: own, time: new GameTime(200));
        StrategicFeatures readyFeatures = Fixture.Features(readyBelief) with
        {
            Army = new ArmyFeatures(Trend.Flat(2000), new Dictionary<UnitRole, double>(), [], Trend.Flat(0), Trend.Flat(0)),
        };

        OperationalPlan committed = planner.Plan(readyBelief, readyFeatures, intent, leases);
        SquadOrder attackOrder = Assert.Single(committed.Squads);
        Assert.True(attackOrder.Engage);
        Assert.Equal(ObjectiveKind.AttackRegion, attackOrder.Objective);
        Assert.Equal(Fixture.Front, attackOrder.TargetRegion);
    }
}
