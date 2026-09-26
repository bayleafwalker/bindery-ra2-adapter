// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Tactics;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Tactics;

/// <summary>
/// The repair controller only takes a vehicle to a building that repairs, keeps it until it is actually repaired,
/// and gives it back when no repair happens.
/// </summary>
public sealed class RepairControllerTests
{
    private static OwnEntity Building(uint id, UnitRole role, Cell cell) =>
        new(new EntityId(id), $"b{id}", role, EntityKind.Building, cell, Fixture.Home, 1.0, 1000, false);

    private static OwnEntity Tank(double health) =>
        new(new EntityId(1), "tank", UnitRole.AntiArmor, EntityKind.Vehicle, new Cell(15, 15), Fixture.Home, health, 800, false);

    [Fact]
    public void A_vehicle_is_never_sent_to_a_factory_or_yard_for_repair()
    {
        RepairController controller = new(new RepairControllerOptions());
        OwnEntity yard = Building(100, UnitRole.Production, new Cell(10, 10));
        OwnEntity factory = Building(101, UnitRole.Production, new Cell(12, 10));

        IReadOnlyList<GameCommand> commands = controller.Tick(Fixture.Belief(own: [yard, factory, Tank(0.3)]), [], new FakeLeaseManager(), new FakeRulesDatabase());

        Assert.Empty(commands);
    }

    [Fact]
    public void A_damaged_vehicle_goes_to_the_service_depot()
    {
        RepairController controller = new(new RepairControllerOptions());
        OwnEntity yard = Building(100, UnitRole.Production, new Cell(14, 14));
        OwnEntity depot = Building(102, UnitRole.Support, new Cell(20, 20));

        IReadOnlyList<GameCommand> commands = controller.Tick(Fixture.Belief(own: [yard, depot, Tank(0.3)]), [], new FakeLeaseManager(), new FakeRulesDatabase());

        RepairCommand repair = Assert.IsType<RepairCommand>(Assert.Single(commands));
        Assert.Equal(depot.Id, repair.Depot);
    }

    [Fact]
    public void A_vehicle_under_repair_is_kept_until_it_is_repaired_not_until_it_crosses_the_entry_threshold()
    {
        RepairController controller = new(new RepairControllerOptions());
        OwnEntity depot = Building(102, UnitRole.Support, new Cell(20, 20));
        FakeLeaseManager leases = new();
        FakeRulesDatabase rules = new();

        double[] health = [0.39, 0.45, 0.6, 0.75, 0.89];
        for (int t = 0; t < health.Length; t++)
        {
            BeliefSnapshot belief = Fixture.Belief(own: [depot, Tank(health[t])], time: GameTime.FromSeconds(t));
            Assert.IsType<RepairCommand>(Assert.Single(controller.Tick(belief, [], leases, rules)));
            Assert.Equal("repair", leases.OwnerOf(LeaseKey.Unit(new EntityId(1)), GameTime.FromSeconds(t)));
        }

        BeliefSnapshot done = Fixture.Belief(own: [depot, Tank(0.95)], time: GameTime.FromSeconds(health.Length));
        Assert.Empty(controller.Tick(done, [], leases, rules));
    }

    [Fact]
    public void A_vehicle_that_is_not_being_repaired_is_given_back()
    {
        RepairController controller = new(new RepairControllerOptions());
        OwnEntity depot = Building(102, UnitRole.Support, new Cell(20, 20));
        FakeLeaseManager leases = new();
        FakeRulesDatabase rules = new();

        int lastRepairOrder = -1;
        for (int t = 0; t <= 80; t++)
        {
            BeliefSnapshot belief = Fixture.Belief(own: [depot, Tank(0.3)], time: GameTime.FromSeconds(t));
            if (controller.Tick(belief, [], leases, rules).Count > 0) lastRepairOrder = t;
        }

        Assert.InRange(lastRepairOrder, 0, 40);
        Assert.NotEqual("repair", leases.OwnerOf(LeaseKey.Unit(new EntityId(1)), GameTime.FromSeconds(80)));
    }
}
