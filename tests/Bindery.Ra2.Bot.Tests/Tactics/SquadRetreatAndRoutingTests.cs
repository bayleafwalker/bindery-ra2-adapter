// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Tactics;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Tactics;

/// <summary>
/// Regression tests for the squad controller's retreat state (it must survive leaving contact, and must not leak
/// into a later squad), its routing (per region group, plain moves when not engaging) and its targeting (no ghost
/// or unhittable targets).
/// </summary>
public sealed class SquadRetreatAndRoutingTests
{
    private static readonly RegionId Mid = new(3);
    private static readonly RegionId Target = new(4);

    private static OwnEntity Yard() =>
        new(new EntityId(100), "yard", UnitRole.Production, EntityKind.Building, new Cell(10, 10), Fixture.Home, 1.0, 3000, false);

    private static EnemyContact Armed(uint id, Cell cell, RegionId region, GameTime seen, int value) =>
        new(new EntityId(id), new PlayerId(1), "enemy", UnitRole.AntiArmor, EntityKind.Vehicle, cell, region, seen, 1.0, value, 1.0, false);

    private static FakeLeaseManager Leased(string squad, params OwnEntity[] units)
    {
        FakeLeaseManager leases = new();
        foreach (OwnEntity unit in units) leases.Grant(LeaseKey.Unit(unit.Id), $"squad:{squad}", new GameTime(0), 100_000);
        return leases;
    }

    [Fact]
    public void Retreating_squad_keeps_retreating_after_it_leaves_engagement_range()
    {
        SquadController controller = new(new SquadControllerOptions());
        FakeRulesDatabase rules = new(armedTypes: ["enemy"]);
        OwnEntity a = Fixture.Unit(1, Fixture.Front, new Cell(50, 44));
        OwnEntity b = Fixture.Unit(2, Fixture.Front, new Cell(50, 44));
        FakeLeaseManager leases = Leased("attack", a, b);
        SquadOrder order = new("attack", ObjectiveKind.AttackRegion, Fixture.Front, [a.Id, b.Id], Engage: true, RetreatBelowForceRatio: 0.6);

        // Ratio 1600/5000 = 0.32: the squad retreats. It then bounces between 7 and 14 cells from the enemy.
        for (int t = 0; t <= 8; t += 2)
        {
            int y = t % 4 == 0 ? 44 : 37;
            OwnEntity[] own = [Yard(), a with { Position = new Cell(50, y) }, b with { Position = new Cell(50, y) }];
            EnemyContact enemy = Armed(20, new Cell(50, 51), Fixture.Front, GameTime.FromSeconds(t), 5000);
            BeliefSnapshot belief = Fixture.Belief(own: own, enemies: [enemy], time: GameTime.FromSeconds(t));
            GameCommand command = Assert.Single(controller.Tick(belief, [order], leases, rules));
            MoveCommand move = Assert.IsType<MoveCommand>(command);
            Assert.Equal(new Cell(10, 10), move.Destination);
        }
    }

    [Fact]
    public void Retreating_squad_that_reaches_home_out_of_contact_is_ready_to_attack_again()
    {
        SquadController controller = new(new SquadControllerOptions());
        FakeRulesDatabase rules = new(armedTypes: ["enemy"]);
        OwnEntity a = Fixture.Unit(1, Fixture.Front, new Cell(50, 44));
        FakeLeaseManager leases = Leased("attack", a);
        SquadOrder order = new("attack", ObjectiveKind.AttackRegion, Fixture.Front, [a.Id], Engage: true, RetreatBelowForceRatio: 0.6);

        EnemyContact enemy = Armed(20, new Cell(50, 51), Fixture.Front, new GameTime(0), 5000);
        Assert.IsType<MoveCommand>(Assert.Single(controller.Tick(Fixture.Belief(own: [Yard(), a], enemies: [enemy]), [order], leases, rules)));

        OwnEntity home = a with { Position = new Cell(11, 11), Region = Fixture.Home };
        BeliefSnapshot later = Fixture.Belief(own: [Yard(), home], time: GameTime.FromSeconds(30));
        Assert.IsType<AttackMoveCommand>(Assert.Single(controller.Tick(later, [order], leases, rules)));
    }

    [Fact]
    public void A_new_wave_under_a_reused_squad_id_does_not_inherit_the_old_retreat()
    {
        SquadController controller = new(new SquadControllerOptions());
        FakeRulesDatabase rules = new(armedTypes: ["enemy"]);
        OwnEntity a = Fixture.Unit(1, Fixture.Front, new Cell(50, 50));
        OwnEntity b = Fixture.Unit(2, Fixture.Front, new Cell(51, 50));
        OwnEntity c = Fixture.Unit(3, Fixture.Front, new Cell(50, 50));
        OwnEntity d = Fixture.Unit(4, Fixture.Front, new Cell(51, 50));

        // Wave 1 meets ratio 0.32 and retreats.
        FakeLeaseManager first = Leased("attack", a, b);
        EnemyContact strong = Armed(20, new Cell(50, 51), Fixture.Front, new GameTime(0), 5000);
        SquadOrder wave1 = new("attack", ObjectiveKind.AttackRegion, Fixture.Front, [a.Id, b.Id], true, 0.6);
        Assert.IsType<MoveCommand>(Assert.Single(controller.Tick(Fixture.Belief(own: [a, b], enemies: [strong]), [wave1], first, rules)));

        // Wave 2, entirely new units, meets ratio 0.8 (inside the band, above the 0.6 retreat line) and fights.
        FakeLeaseManager second = Leased("attack", c, d);
        EnemyContact medium = Armed(21, new Cell(50, 51), Fixture.Front, GameTime.FromSeconds(300), 2000);
        SquadOrder wave2 = new("attack", ObjectiveKind.AttackRegion, Fixture.Front, [c.Id, d.Id], true, 0.6);
        BeliefSnapshot later = Fixture.Belief(own: [c, d], enemies: [medium], time: GameTime.FromSeconds(300));
        Assert.IsType<AttackCommand>(Assert.Single(controller.Tick(later, [wave2], second, rules)));
    }

    [Fact]
    public void A_disbanded_squad_id_starts_afresh_when_it_is_ordered_again()
    {
        SquadController controller = new(new SquadControllerOptions());
        FakeRulesDatabase rules = new(armedTypes: ["enemy"]);
        OwnEntity a = Fixture.Unit(1, Fixture.Front, new Cell(50, 50));
        OwnEntity b = Fixture.Unit(2, Fixture.Front, new Cell(51, 50));
        FakeLeaseManager leases = Leased("attack", a, b);
        SquadOrder order = new("attack", ObjectiveKind.AttackRegion, Fixture.Front, [a.Id, b.Id], true, 0.6);

        EnemyContact strong = Armed(20, new Cell(50, 51), Fixture.Front, new GameTime(0), 5000);
        Assert.IsType<MoveCommand>(Assert.Single(controller.Tick(Fixture.Belief(own: [a, b], enemies: [strong]), [order], leases, rules)));
        // The planner drops the attack squad for a while (staging under another id).
        Assert.Empty(controller.Tick(Fixture.Belief(own: [a, b], time: GameTime.FromSeconds(100)), [], leases, rules));

        EnemyContact medium = Armed(21, new Cell(50, 51), Fixture.Front, GameTime.FromSeconds(300), 2000);
        BeliefSnapshot later = Fixture.Belief(own: [a, b], enemies: [medium], time: GameTime.FromSeconds(300));
        Assert.IsType<AttackCommand>(Assert.Single(controller.Tick(later, [order], leases, rules)));
    }

    [Fact]
    public void Outnumbered_squad_already_at_home_fights_instead_of_moving_to_its_own_base()
    {
        SquadController controller = new(new SquadControllerOptions());
        FakeRulesDatabase rules = new(armedTypes: ["enemy"]);
        OwnEntity a = Fixture.Unit(1, Fixture.Home, new Cell(12, 12));
        FakeLeaseManager leases = Leased("defend", a);
        SquadOrder order = new("defend", ObjectiveKind.DefendRegion, Fixture.Home, [a.Id], true, 0.6);

        for (int t = 0; t <= 10; t++)
        {
            EnemyContact enemy = Armed(20, new Cell(14, 14), Fixture.Home, GameTime.FromSeconds(t), 2000);
            BeliefSnapshot belief = Fixture.Belief(own: [Yard(), a], enemies: [enemy], time: GameTime.FromSeconds(t));
            Assert.IsType<AttackCommand>(Assert.Single(controller.Tick(belief, [order], leases, rules)));
        }
    }

    [Fact]
    public void Squad_that_does_not_engage_routes_with_a_plain_move()
    {
        SquadController controller = new(new SquadControllerOptions());
        FakeRulesDatabase rules = new(armedTypes: ["enemy"]);
        OwnEntity scout = Fixture.Unit(1, Fixture.Home, new Cell(12, 12));
        FakeLeaseManager leases = Leased("scout", scout);
        SquadOrder order = new("scout", ObjectiveKind.Scout, Fixture.Front, [scout.Id], Engage: false, RetreatBelowForceRatio: 0.6);
        EnemyContact enemy = Armed(20, new Cell(14, 14), Fixture.Home, new GameTime(0), 800);

        GameCommand command = Assert.Single(controller.Tick(Fixture.Belief(own: [scout], enemies: [enemy]), [order], leases, rules));
        Assert.IsType<MoveCommand>(command);
    }

    [Fact]
    public void Focus_fire_never_picks_a_target_the_squad_cannot_hurt()
    {
        SquadController controller = new(new SquadControllerOptions());
        FakeRulesDatabase rules = new(new Dictionary<(string, string), double> { [("tank", "enemy")] = 0.0 }, armedTypes: ["enemy"]);
        OwnEntity tank = Fixture.Unit(1, Fixture.Front, new Cell(50, 50), value: 8000);
        FakeLeaseManager leases = Leased("attack", tank);
        SquadOrder order = new("attack", ObjectiveKind.AttackRegion, Fixture.Front, [tank.Id], true, 0.6);
        EnemyContact aircraft = Armed(20, new Cell(51, 50), Fixture.Front, new GameTime(0), 800);

        GameCommand command = Assert.Single(controller.Tick(Fixture.Belief(own: [tank], enemies: [aircraft]), [order], leases, rules));
        Assert.IsNotType<AttackCommand>(command);
    }

    [Fact]
    public void Contact_out_of_sight_for_seconds_is_neither_a_focus_target_nor_a_reason_to_retreat()
    {
        SquadController controller = new(new SquadControllerOptions());
        FakeRulesDatabase rules = new(armedTypes: ["enemy"]);
        OwnEntity tank = Fixture.Unit(1, Fixture.Front, new Cell(50, 50));
        FakeLeaseManager leases = Leased("attack", tank);
        SquadOrder order = new("attack", ObjectiveKind.AttackRegion, Fixture.Front, [tank.Id], true, 0.6);
        // Passed by 15 s ago and left vision; still "fresh" by the 20 s staleness limit.
        EnemyContact ghost = Armed(20, new Cell(52, 50), Fixture.Front, GameTime.FromSeconds(85), 5000);

        for (int t = 100; t <= 110; t++)
        {
            BeliefSnapshot belief = Fixture.Belief(own: [tank], enemies: [ghost], time: GameTime.FromSeconds(t));
            Assert.IsType<AttackMoveCommand>(Assert.Single(controller.Tick(belief, [order], leases, rules)));
        }
    }

    /// <summary>Home(10,10) - Mid(30,10) - Front(50,10) - Target(70,10).</summary>
    private static MapInfo Chain() => new(
        "chain",
        100,
        30,
        [
            new Region(Fixture.Home, "Home", new Cell(10, 10), 8, true, true, false),
            new Region(Fixture.Front, "Front", new Cell(50, 10), 8, false, false, false),
            new Region(Mid, "Mid", new Cell(30, 10), 8, false, false, false),
            new Region(Target, "Target", new Cell(70, 10), 8, true, false, false),
        ],
        [
            new RegionLink(Fixture.Home, Mid, 20, true, false),
            new RegionLink(Mid, Fixture.Front, 20, true, false),
            new RegionLink(Fixture.Front, Target, 20, true, false),
        ],
        []);

    [Fact]
    public void Reinforcements_at_base_do_not_pull_the_front_line_back()
    {
        SquadController controller = new(new SquadControllerOptions());
        FakeRulesDatabase rules = new(armedTypes: ["enemy"]);
        List<OwnEntity> tanks = Enumerable.Range(1, 5).Select(i => Fixture.Unit((uint)i, Fixture.Front, new Cell(52, 10))).ToList();
        List<OwnEntity> fresh = Enumerable.Range(11, 6).Select(i => Fixture.Unit((uint)i, Fixture.Home, new Cell(11, 10))).ToList();
        OwnEntity[] all = [.. tanks, .. fresh];
        FakeLeaseManager leases = Leased("attack", all);
        SquadOrder order = new("attack", ObjectiveKind.AttackRegion, Target, [.. all.Select(static u => u.Id)], true, 0.6);
        BeliefSnapshot belief = Fixture.Belief(own: all) with { Map = Chain() };

        List<AttackMoveCommand> moves = controller.Tick(belief, [order], leases, rules).Cast<AttackMoveCommand>().ToList();

        AttackMoveCommand front = Assert.Single(moves, m => m.Units.Contains(tanks[0].Id));
        Assert.Equal(new Cell(70, 10), front.Destination);
        Assert.All(tanks, t => Assert.Contains(t.Id, front.Units));
        AttackMoveCommand rear = Assert.Single(moves, m => m.Units.Contains(fresh[0].Id));
        Assert.Equal(new Cell(30, 10), rear.Destination);
    }
}
