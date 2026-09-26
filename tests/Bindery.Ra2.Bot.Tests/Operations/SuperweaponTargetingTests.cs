// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Operations;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Operations;

/// <summary>
/// A superweapon strike hits every object within its radius, own ones included, so the planner scores a target by
/// the enemy value it would destroy minus the own value it would destroy, and holds the launch when no target is
/// worth it.
/// </summary>
public sealed class SuperweaponTargetingTests
{
    private static readonly EntityId Silo = new(1);

    private static OwnEntity Own(uint id, EntityKind kind, Cell cell, int value) =>
        new(new EntityId(id), $"own-{id}", kind == EntityKind.Building ? UnitRole.Superweapon : UnitRole.AntiArmor, kind, cell, Fixture.Home, 1.0, value, false);

    private static EnemyContact Building(uint id, Cell cell, int value) =>
        new(new EntityId(id), new PlayerId(1), $"enemy-{id}", UnitRole.Production, EntityKind.Building, cell, Fixture.Front, new GameTime(0), 1.0, value, 1.0, false);

    private static List<LaunchSuperweaponCommand> Launches(IReadOnlyList<OwnEntity> own, IReadOnlyList<EnemyContact> enemies)
    {
        OperationalPlanner planner = new(new FakeRulesDatabase([]), new FakePlaybookLibrary([]), new OperationalOptions());
        BeliefSnapshot belief = Fixture.Belief(own: own, enemies: enemies) with
        {
            Superweapons = [new SuperweaponStatus(new PlayerId(0), "silo", Silo, 600, 0, true)],
        };
        OperationalPlan plan = planner.Plan(belief, Fixture.Features(belief), Fixture.Intent(), new FakeLeaseManager());
        return [.. plan.ProductionCommands.OfType<LaunchSuperweaponCommand>()];
    }

    [Fact]
    public void The_strike_goes_where_it_destroys_the_most_net_value_not_onto_the_own_assault()
    {
        Cell baseCell = new(50, 50), outpost = new(60, 40);
        List<OwnEntity> own = [Own(1, EntityKind.Building, new Cell(10, 10), 5000), .. Enumerable.Range(0, 4).Select(i => Own((uint)(10 + i), EntityKind.Vehicle, new Cell(51, 49 + i), 900))];
        List<EnemyContact> enemies = [Building(100, baseCell, 3000), Building(101, new Cell(52, 51), 2000), Building(102, outpost, 2000)];

        LaunchSuperweaponCommand launch = Assert.Single(Launches(own, enemies));

        Assert.Equal(outpost, launch.Target);
    }

    [Fact]
    public void With_the_own_army_on_every_target_the_launch_is_held()
    {
        List<OwnEntity> own = [Own(1, EntityKind.Building, new Cell(10, 10), 5000), .. Enumerable.Range(0, 6).Select(i => Own((uint)(10 + i), EntityKind.Vehicle, new Cell(51, 50 + i), 900))];
        List<EnemyContact> enemies = [Building(100, new Cell(50, 50), 1500)];

        Assert.Empty(Launches(own, enemies));
    }
}
