// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Operations;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Tuning;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Operations;

/// <summary>
/// Every playbook parameter and intent field the catalogue offers must change what the planner does. Each test
/// here runs the same situation with two values and fails if the planner ignores the field.
/// </summary>
public sealed class PlaybookParameterTests
{
    private static readonly RegionId Mid = new(3);

    /// <summary>Home — Mid — Front, with Front a start location (where scouts go first by default).</summary>
    private static MapInfo ThreeRegionMap() => new(
        "three-region",
        100,
        100,
        [
            new Region(Fixture.Home, "Home", new Cell(10, 10), 8, true, true, false),
            new Region(Fixture.Front, "Front", new Cell(50, 50), 8, true, false, false),
            new Region(Mid, "Mid", new Cell(30, 30), 8, false, false, false),
        ],
        [new RegionLink(Fixture.Home, Mid, 28, true, false), new RegionLink(Mid, Fixture.Front, 28, true, false)],
        [new OreField(Fixture.Home, new Cell(12, 8), 10000, false)]);

    private static OwnEntity Tank(uint id, RegionId region, Cell cell) =>
        new(new EntityId(id), "tank", UnitRole.AntiArmor, EntityKind.Vehicle, cell, region, 1.0, 800, false);

    private static OwnEntity Yard() =>
        new(new EntityId(100), "cy", UnitRole.Production, EntityKind.Building, new Cell(10, 10), Fixture.Home, 1.0, 3000, false);

    private static OperationalPlanner Planner() =>
        new(new FakeRulesDatabase([Fixture.Combat("tank", UnitRole.AntiArmor, QueueKind.Vehicle, 800, 6)]), new FakePlaybookLibrary([]), new OperationalOptions());

    private static StrategicFeatures Ready(BeliefSnapshot belief, IReadOnlyList<ThreatAssessment>? threats = null) =>
        Fixture.Features(belief, threats: threats) with
        {
            Army = new ArmyFeatures(Trend.Flat(3000), new Dictionary<UnitRole, double>(), [], Trend.Flat(0), Trend.Flat(0)),
        };

    private static StrategicIntent WithParameters(StrategicIntent intent, params (string Name, double Value)[] parameters) =>
        intent with { PlaybookParameters = parameters.ToDictionary(static p => p.Name, static p => p.Value, StringComparer.Ordinal) };

    [Theory]
    [InlineData(2.0, ObjectiveKind.AttackRegion)]
    [InlineData(1.2, ObjectiveKind.DefendRegion)]
    public void DefendThreatRatio_decides_when_a_base_threat_pulls_the_army_home(double defendThreatRatio, ObjectiveKind expected)
    {
        BeliefSnapshot belief = Fixture.Belief(own: [Tank(1, Fixture.Home, new Cell(10, 10)), Yard()]);
        // Base threat ratio 1.5 (enemy 1500 against own 1000 at the base).
        ThreatAssessment threat = new(Fixture.Home, 1500, 1000, 0.67, 30, 5, true, 1.0);
        StrategicIntent intent = WithParameters(
            Fixture.Intent(objectives: [new Objective(ObjectiveKind.AttackRegion, Fixture.Front, null, 1)]),
            ("defendThreatRatio", defendThreatRatio));

        OperationalPlan plan = Planner().Plan(belief, Ready(belief, [threat]), intent, new FakeLeaseManager());

        Assert.Equal(expected, Assert.Single(plan.Squads).Objective);
    }

    [Theory]
    [InlineData(30.0, true)]
    [InlineData(120.0, false)]
    public void HarassIntervalSeconds_times_the_next_sortie(double interval, bool secondSortieOutAt40s)
    {
        OperationalPlanner planner = Planner();
        FakeLeaseManager leases = new();
        StrategicIntent intent = WithParameters(
            Fixture.Intent(objectives: [new Objective(ObjectiveKind.Harass, Fixture.Front, null, 2)]),
            ("harassIntervalSeconds", interval));
        OwnEntity[] atHome = [Tank(1, Fixture.Home, new Cell(10, 10)), Tank(2, Fixture.Home, new Cell(11, 10)), Yard()];
        OwnEntity[] atTarget = [Tank(1, Fixture.Front, new Cell(50, 50)), Tank(2, Fixture.Front, new Cell(51, 50)), Yard()];

        SquadOrder Harass(IReadOnlyList<OwnEntity> own, double seconds)
        {
            BeliefSnapshot belief = Fixture.Belief(own: own, time: GameTime.FromSeconds(seconds));
            OperationalPlan plan = planner.Plan(belief, Fixture.Features(belief), intent, leases);
            return plan.Squads.Single(static s => s.Objective == ObjectiveKind.Harass);
        }

        Assert.Equal(Fixture.Front, Harass(atHome, 0).TargetRegion);   // sortie 1 sets out
        Assert.Equal(Fixture.Front, Harass(atTarget, 10).TargetRegion); // arrives and harasses
        SquadOrder back = Harass(atTarget, 25);                         // done: heads home to regroup
        Assert.Equal(Fixture.Home, back.TargetRegion);
        Assert.False(back.Engage);
        SquadOrder next = Harass(atHome, 40);
        Assert.Equal(secondSortieOutAt40s ? Fixture.Front : Fixture.Home, next.TargetRegion);
    }

    [Theory]
    [InlineData(3.0)]
    [InlineData(1.0)]
    public void SiegeRangeBufferCells_reaches_the_attack_squad_order(double buffer)
    {
        BeliefSnapshot belief = Fixture.Belief(own: [Tank(1, Fixture.Home, new Cell(10, 10)), Yard()]);
        StrategicIntent intent = WithParameters(
            Fixture.Intent(objectives: [new Objective(ObjectiveKind.AttackRegion, Fixture.Front, null, 1)]),
            ("siegeRangeBufferCells", buffer));

        OperationalPlan plan = Planner().Plan(belief, Ready(belief), intent, new FakeLeaseManager());

        SquadOrder attack = Assert.Single(plan.Squads);
        Assert.Equal(ObjectiveKind.AttackRegion, attack.Objective);
        Assert.Equal(buffer, attack.StandoffBufferCells);
    }

    [Fact]
    public void Without_a_siege_buffer_the_attack_order_has_no_standoff()
    {
        BeliefSnapshot belief = Fixture.Belief(own: [Tank(1, Fixture.Home, new Cell(10, 10)), Yard()]);
        StrategicIntent intent = Fixture.Intent(objectives: [new Objective(ObjectiveKind.AttackRegion, Fixture.Front, null, 1)]);

        OperationalPlan plan = Planner().Plan(belief, Ready(belief), intent, new FakeLeaseManager());

        Assert.Equal(0, Assert.Single(plan.Squads).StandoffBufferCells);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RegionsOfInterest_choose_where_the_army_stages(bool withRegionOfInterest)
    {
        BeliefSnapshot belief = Fixture.Belief(own: [Tank(1, Fixture.Home, new Cell(10, 10)), Yard()]) with { Map = ThreeRegionMap() };
        StrategicFeatures features = Fixture.Features(belief); // army 0: the attack waits, so the army stages
        StrategicIntent intent = Fixture.Intent(objectives: [new Objective(ObjectiveKind.AttackRegion, Fixture.Front, null, 1)])
            with { RegionsOfInterest = withRegionOfInterest ? [Mid] : [] };

        OperationalPlan plan = Planner().Plan(belief, features, intent, new FakeLeaseManager());

        SquadOrder staging = Assert.Single(plan.Squads);
        Assert.Equal(withRegionOfInterest ? Mid : Fixture.Home, staging.TargetRegion);
    }

    [Fact]
    public void A_region_of_interest_that_is_the_attack_target_or_enemy_held_is_not_a_staging_area()
    {
        BeliefSnapshot belief = Fixture.Belief(own: [Tank(1, Fixture.Home, new Cell(10, 10)), Yard()]) with { Map = ThreeRegionMap() };
        StrategicFeatures features = Fixture.Features(belief) with
        {
            MapControl = new MapControlFeatures(
                new Dictionary<RegionId, RegionControl> { [Fixture.Home] = RegionControl.Own, [Mid] = RegionControl.Enemy, [Fixture.Front] = RegionControl.Unknown },
                [], 0.5),
        };
        StrategicIntent intent = Fixture.Intent(objectives: [new Objective(ObjectiveKind.AttackRegion, Fixture.Front, null, 1)])
            with { RegionsOfInterest = [Fixture.Front, Mid] };

        OperationalPlan plan = Planner().Plan(belief, features, intent, new FakeLeaseManager());

        Assert.Equal(Fixture.Home, Assert.Single(plan.Squads).TargetRegion);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RegionsOfInterest_are_scouted_first(bool withRegionOfInterest)
    {
        BeliefSnapshot belief = Fixture.Belief(own: [Tank(1, Fixture.Home, new Cell(10, 10)), Tank(2, Fixture.Home, new Cell(11, 10)), Yard()])
            with { Map = ThreeRegionMap() };
        StrategicIntent intent = Fixture.Intent(objectives: [new Objective(ObjectiveKind.Scout, null, null, 3)])
            with { RegionsOfInterest = withRegionOfInterest ? [Mid] : [] };

        OperationalPlan plan = Planner().Plan(belief, Fixture.Features(belief), intent, new FakeLeaseManager());

        SquadOrder scout = plan.Squads.Single(static s => s.Objective == ObjectiveKind.Scout);
        Assert.Equal(withRegionOfInterest ? Mid : Fixture.Front, scout.TargetRegion);
    }

    [Fact]
    public void Every_declared_playbook_parameter_has_a_consumer()
    {
        IEnumerable<string> declared = PlaybookLibrary.LoadAuthored().All.SelectMany(static p => p.Parameters).Select(static p => p.Name).Distinct();
        Assert.All(declared, name => Assert.Contains(name, TuningKnobs.ConsumedPlaybookParameters));
    }
}
