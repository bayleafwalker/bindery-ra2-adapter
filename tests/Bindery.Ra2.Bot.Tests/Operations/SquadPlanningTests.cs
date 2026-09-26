// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Operations;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Operations;

/// <summary>
/// Squad planning: every objective kind the strategist can send changes what the army does, the defend switch has
/// hysteresis, defenders at a base never retreat from it, and a scout is never sent where it cannot walk.
/// </summary>
public sealed class SquadPlanningTests
{
    private static readonly RegionId Mid = new(3);
    private static readonly RegionId Island = new(4);

    /// <summary>Home — Mid — Front (a start location), plus an unlinked island start location.</summary>
    private static MapInfo Map() => new(
        "squad-planning",
        100,
        100,
        [
            new Region(Fixture.Home, "Home", new Cell(10, 10), 8, true, true, false),
            new Region(Fixture.Front, "Front", new Cell(50, 50), 8, true, false, false),
            new Region(Mid, "Mid", new Cell(30, 30), 8, false, false, false),
            new Region(Island, "Island", new Cell(90, 10), 6, true, false, false),
        ],
        [new RegionLink(Fixture.Home, Mid, 28, true, false), new RegionLink(Mid, Fixture.Front, 28, true, false)],
        [new OreField(Fixture.Home, new Cell(12, 8), 10000, false)]);

    private static OwnEntity Tank(uint id) =>
        new(new EntityId(id), "tank", UnitRole.AntiArmor, EntityKind.Vehicle, new Cell(10 + (int)id, 10), Fixture.Home, 1.0, 800, false);

    private static OwnEntity Yard() =>
        new(new EntityId(100), "cy", UnitRole.Production, EntityKind.Building, new Cell(10, 10), Fixture.Home, 1.0, 3000, false);

    private static OperationalPlanner Planner() =>
        new(new FakeRulesDatabase([Fixture.Combat("tank", UnitRole.AntiArmor, QueueKind.Vehicle, 800, 6)]), new FakePlaybookLibrary([]), new OperationalOptions());

    private static BeliefSnapshot Belief(double seconds = 0) =>
        Fixture.Belief(own: [Tank(1), Tank(2), Yard()], time: GameTime.FromSeconds(seconds)) with { Map = Map() };

    /// <summary>Army value 3000: above the default 1500 attack threshold.</summary>
    private static StrategicFeatures Ready(BeliefSnapshot belief, params ThreatAssessment[] threats) =>
        Fixture.Features(belief, threats: threats) with
        {
            Army = new ArmyFeatures(Trend.Flat(3000), new Dictionary<UnitRole, double>(), [], Trend.Flat(0), Trend.Flat(0)),
        };

    private static StrategicIntent Intent(params Objective[] objectives) =>
        Fixture.Intent(objectives: objectives) with { RegionsOfInterest = [] };

    private static ThreatAssessment BaseThreat(double enemyValue, double ownValue = 1000) =>
        new(Fixture.Home, enemyValue, ownValue, ownValue / enemyValue, 30, 5, true, 1.0);

    [Fact]
    public void Defenders_of_a_base_region_never_retreat_from_it()
    {
        BeliefSnapshot belief = Belief();
        OperationalPlan plan = Planner().Plan(belief, Ready(belief, BaseThreat(2000)), Intent(new Objective(ObjectiveKind.AttackRegion, Fixture.Front, null, 1)), new FakeLeaseManager());

        SquadOrder defend = Assert.Single(plan.Squads);
        Assert.Equal(ObjectiveKind.DefendRegion, defend.Objective);
        Assert.Equal(Fixture.Home, defend.TargetRegion);
        Assert.Equal(0, defend.RetreatBelowForceRatio);
    }

    [Fact]
    public void An_attack_keeps_its_retreat_ratio()
    {
        BeliefSnapshot belief = Belief();
        OperationalPlan plan = Planner().Plan(belief, Ready(belief), Intent(new Objective(ObjectiveKind.AttackRegion, Fixture.Front, null, 1)), new FakeLeaseManager());

        SquadOrder attack = Assert.Single(plan.Squads);
        Assert.Equal(ObjectiveKind.AttackRegion, attack.Objective);
        Assert.Equal(new OperationalOptions().DefaultRetreatBelowForceRatio, attack.RetreatBelowForceRatio);
    }

    [Fact]
    public void Base_threat_hovering_around_the_defend_line_does_not_flip_the_army_every_pass()
    {
        OperationalPlanner planner = Planner();
        FakeLeaseManager leases = new();
        StrategicIntent intent = Intent(new Objective(ObjectiveKind.AttackRegion, Fixture.Front, null, 1));
        List<ObjectiveKind> seen = [];
        for (int t = 0; t < 8; t++)
        {
            BeliefSnapshot belief = Belief(t);
            ThreatAssessment threat = BaseThreat(t % 2 == 0 ? 1980 : 2020, 2000); // 0.99 / 1.01
            seen.Add(Assert.Single(planner.Plan(belief, Ready(belief, threat), intent, leases).Squads).Objective);
        }
        Assert.Equal(ObjectiveKind.AttackRegion, seen[0]);
        Assert.All(seen.Skip(1), static o => Assert.Equal(ObjectiveKind.DefendRegion, o));

        // Once the threat has clearly receded (and the minimum time has passed) the attack resumes.
        BeliefSnapshot calm = Belief(30);
        Assert.Equal(ObjectiveKind.AttackRegion, Assert.Single(planner.Plan(calm, Ready(calm, BaseThreat(500, 2000)), intent, leases).Squads).Objective);
    }

    [Fact]
    public void Scout_never_heads_for_a_start_location_it_cannot_walk_to()
    {
        // Front was just seen, so the only unvisited start location left is the island.
        BeliefSnapshot belief = Belief(10) with { RegionLastSeen = new Dictionary<RegionId, GameTime> { [Fixture.Front] = GameTime.FromSeconds(5) } };
        OperationalPlan plan = Planner().Plan(belief, Fixture.Features(belief), Intent(new Objective(ObjectiveKind.Scout, null, null, 3)), new FakeLeaseManager());

        SquadOrder scout = Assert.Single(plan.Squads, static s => s.Objective == ObjectiveKind.Scout);
        Assert.NotEqual(Island, scout.TargetRegion);
        Assert.Equal(Mid, scout.TargetRegion);
    }

    [Fact]
    public void A_scout_objective_region_is_visited_first()
    {
        BeliefSnapshot belief = Belief();
        OperationalPlan plan = Planner().Plan(belief, Fixture.Features(belief), Intent(new Objective(ObjectiveKind.Scout, Mid, null, 3)), new FakeLeaseManager());

        Assert.Equal(Mid, Assert.Single(plan.Squads, static s => s.Objective == ObjectiveKind.Scout).TargetRegion);
    }

    [Fact]
    public void Deny_expansion_sends_the_army_to_its_region()
    {
        BeliefSnapshot belief = Belief();
        OperationalPlan plan = Planner().Plan(belief, Ready(belief), Intent(new Objective(ObjectiveKind.DenyExpansion, Mid, null, 1)), new FakeLeaseManager());

        SquadOrder squad = Assert.Single(plan.Squads);
        Assert.Equal(ObjectiveKind.AttackRegion, squad.Objective);
        Assert.Equal(Mid, squad.TargetRegion);
    }

    [Fact]
    public void A_defend_objective_that_outranks_the_attack_holds_the_army_at_its_threatened_region()
    {
        BeliefSnapshot belief = Belief();
        ThreatAssessment midThreat = new(Mid, 500, 0, 0, 30, 5, false, 1.0);
        StrategicIntent intent = Intent(new Objective(ObjectiveKind.DefendRegion, Mid, null, 1), new Objective(ObjectiveKind.AttackRegion, Fixture.Front, null, 2));

        SquadOrder squad = Assert.Single(Planner().Plan(belief, Ready(belief, midThreat), intent, new FakeLeaseManager()).Squads);

        Assert.Equal(ObjectiveKind.DefendRegion, squad.Objective);
        Assert.Equal(Mid, squad.TargetRegion);
    }

    [Fact]
    public void Without_an_attack_the_army_waits_at_the_defend_objective_region()
    {
        BeliefSnapshot belief = Belief();
        SquadOrder squad = Assert.Single(Planner().Plan(belief, Ready(belief), Intent(new Objective(ObjectiveKind.DefendRegion, Mid, null, 5)), new FakeLeaseManager()).Squads);

        Assert.Equal(ObjectiveKind.DefendRegion, squad.Objective);
        Assert.Equal(Mid, squad.TargetRegion);
    }
}
