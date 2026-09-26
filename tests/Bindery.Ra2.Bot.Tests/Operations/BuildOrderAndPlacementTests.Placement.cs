// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Operations;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Operations;

/// <summary>Placement tests; the fixtures are in the other half of this class.</summary>
public sealed partial class BuildOrderAndPlacementTests
{
    [Fact]
    public void Two_buildings_placed_in_one_pass_never_share_a_cell()
    {
        // The enemy start (Front) lies down-right of the base; with it up-left the plain building's first ring
        // cell and the defense's biased cell coincide. Put the base at (50,50) and the enemy at Home (10,10).
        BeliefSnapshot belief = Fixture.Belief(
            own: [new OwnEntity(new EntityId(100), "cy", UnitRole.Production, EntityKind.Building, new Cell(50, 50), Fixture.Front, 1.0, 3000, false)],
            queues:
            [
                new ProductionQueueState(QueueKind.Building, [new QueueItem("power", 1.0, true, false)], 1),
                new ProductionQueueState(QueueKind.Defense, [new QueueItem("pbox", 1.0, true, false)], 1),
            ]);

        OperationalPlan plan = Planner().Plan(belief, Fixture.Features(belief), Fixture.Intent(), new FakeLeaseManager());

        List<PlaceBuildingCommand> places = plan.ProductionCommands.OfType<PlaceBuildingCommand>().ToList();
        Assert.Equal(2, places.Count);
        Assert.True(places[0].Cell.DistanceTo(places[1].Cell) >= new OperationalOptions().BuildGridStep, $"{places[0].Cell} vs {places[1].Cell}");
    }

    [Fact]
    public void A_placement_the_game_does_not_accept_is_retried_elsewhere()
    {
        OperationalPlanner planner = Planner();
        FakeLeaseManager leases = new();
        ProductionQueueState waiting = new(QueueKind.Building, [new QueueItem("power", 1.0, true, false)], 1);

        Cell PlaceAt(double seconds)
        {
            BeliefSnapshot belief = Base(seconds, 0, [waiting]);
            return Assert.Single(planner.Plan(belief, Economy(belief, 1, 3), Fixture.Intent(), leases).ProductionCommands.OfType<PlaceBuildingCommand>()).Cell;
        }

        Cell first = PlaceAt(0);
        Assert.Equal(first, PlaceAt(1)); // not yet refused: the order may still be in flight
        Cell second = PlaceAt(5);        // still unplaced well after the order: that cell is refused
        Assert.NotEqual(first, second);
        Cell third = PlaceAt(10);
        Assert.NotEqual(first, third);
        Assert.NotEqual(second, third);
    }

    [Fact]
    public void A_refinery_goes_toward_the_ore_field_the_expand_objective_names()
    {
        ProductionQueueState ready = new(QueueKind.Building, [new QueueItem("ref", 1.0, true, false)], 1);
        BeliefSnapshot belief = Base(300, 0, [ready]);
        StrategicIntent intent = Fixture.Intent(objectives: [new Objective(ObjectiveKind.Expand, FarOre, null, 3)]);

        OperationalPlan plan = Planner().Plan(belief, Economy(belief, 1, 3), intent, new FakeLeaseManager());

        Cell cell = Assert.Single(plan.ProductionCommands.OfType<PlaceBuildingCommand>()).Cell;
        Cell far = new(10, 40);
        double nearestOwn = belief.Own.Where(static e => e.Kind == EntityKind.Building).Min(e => e.Position.DistanceTo(far));
        Assert.True(cell.DistanceTo(far) < nearestOwn, $"refinery at {cell} is no nearer the far field than the base ({nearestOwn:0.0})");

        // Without the objective the fallback picks the nearest unserved field (Mid), whose side of the base is also
        // nearer FarOre than the base; the objective must do better than that fallback, or it has no effect.
        Cell fallback = Assert.Single(Planner().Plan(belief, Economy(belief, 1, 3), Fixture.Intent(), new FakeLeaseManager())
            .ProductionCommands.OfType<PlaceBuildingCommand>()).Cell;
        Assert.True(cell.DistanceTo(far) < fallback.DistanceTo(far), $"expand placed {cell}, the no-objective fallback {fallback}");
    }

    /// <summary>
    /// An Expand objective naming a field we already serve (the composer's nearest candidate is often the home
    /// field) must not pull the new refinery back onto it: the refinery goes to the nearest unserved field instead.
    /// </summary>
    [Fact]
    public void An_expand_objective_naming_an_already_served_field_does_not_crowd_it()
    {
        ProductionQueueState ready = new(QueueKind.Building, [new QueueItem("ref", 1.0, true, false)], 1);
        BeliefSnapshot belief = Base(300, 0, [ready]);
        StrategicIntent intent = Fixture.Intent(objectives: [new Objective(ObjectiveKind.Expand, Fixture.Home, null, 3)]);

        OperationalPlan plan = Planner().Plan(belief, Economy(belief, 1, 3), intent, new FakeLeaseManager());

        Cell cell = Assert.Single(plan.ProductionCommands.OfType<PlaceBuildingCommand>()).Cell;
        Cell mid = new(30, 30);
        double nearestOwn = belief.Own.Where(static e => e.Kind == EntityKind.Building).Min(e => e.Position.DistanceTo(mid));
        Assert.True(cell.DistanceTo(mid) < nearestOwn, $"refinery at {cell} is no nearer the unserved Mid field than the base ({nearestOwn:0.0})");
    }

    [Fact]
    public void Without_an_expand_objective_a_new_refinery_goes_toward_ore_that_has_no_refinery_yet()
    {
        ProductionQueueState ready = new(QueueKind.Building, [new QueueItem("ref", 1.0, true, false)], 1);
        BeliefSnapshot belief = Base(300, 0, [ready]);

        OperationalPlan plan = Planner().Plan(belief, Economy(belief, 1, 3), Fixture.Intent(), new FakeLeaseManager());

        // The home field (12,6) already has the refinery at (13,7); the next nearest field is Mid (30,30), so the
        // new refinery goes on the base's Mid side.
        Cell cell = Assert.Single(plan.ProductionCommands.OfType<PlaceBuildingCommand>()).Cell;
        Cell mid = new(30, 30);
        double nearestOwn = belief.Own.Where(static e => e.Kind == EntityKind.Building).Min(e => e.Position.DistanceTo(mid));
        Assert.True(cell.DistanceTo(mid) < nearestOwn, $"refinery at {cell} is no nearer the Mid field than the base ({nearestOwn:0.0})");
    }
}
