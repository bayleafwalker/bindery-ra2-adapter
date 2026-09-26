// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Tactics;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Tactics;

/// <summary>
/// Stand-off siege never overrides the squad's safety rules: the retreat hysteresis (invariant 3) applies to a
/// sieging squad as to any other, a stand-off cell is always somewhere the squad can walk to, and an order that
/// does not engage moves plainly instead of attack-moving (an attack-move holds in any region with an enemy in it).
/// </summary>
public sealed class SiegeSafetyTests
{
    private static readonly RegionId Staging = new(1);
    private static readonly RegionId Sea = new(3);
    private static readonly RegionId Target = new(2);

    private static MapInfo Map(bool waterBetween) => new(
        "siege-safety",
        60,
        20,
        waterBetween
            ?
            [
                new Region(Staging, "staging", new Cell(0, 10), 5, false, false, false),
                new Region(Sea, "sea", new Cell(11, 10), 5, false, false, true),
                new Region(Target, "target", new Cell(24, 10), 8, true, false, false),
            ]
            :
            [
                new Region(Staging, "staging", new Cell(0, 10), 8, false, false, false),
                new Region(Target, "target", new Cell(24, 10), 8, true, false, false),
            ],
        waterBetween
            ? [new RegionLink(Staging, Target, 40, true, false), new RegionLink(Staging, Sea, 11, false, true), new RegionLink(Sea, Target, 13, false, true)]
            : [new RegionLink(Staging, Target, 24, true, false)],
        []);

    private static readonly EnemyContact Tower = new(
        new EntityId(50), new PlayerId(1), "GAPRIS", UnitRole.Defense, EntityKind.Building, new Cell(18, 10), Target,
        new GameTime(0), 1.0, 1500, 1.0, false);

    private static OwnEntity Own(MapInfo map, uint id, string type, UnitRole role, Cell cell, EntityKind kind = EntityKind.Vehicle) =>
        new(new EntityId(id), type, role, kind, cell, map.RegionOf(cell)!.Id, 1.0, 900, false);

    private static IReadOnlyList<GameCommand> Tick(
        SquadController controller, MapInfo map, IReadOnlyList<OwnEntity> own, IReadOnlyList<EnemyContact> enemies, double seconds, double buffer, bool engage = true)
    {
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();
        FakeLeaseManager leases = new();
        List<OwnEntity> squad = [.. own.Where(static e => e.Kind != EntityKind.Building)];
        foreach (OwnEntity e in squad) leases.Grant(LeaseKey.Unit(e.Id), "squad:attack", GameTime.FromSeconds(seconds), 10);
        BeliefSnapshot belief = new(
            1, GameTime.FromSeconds(seconds), ObservationMode.Belief, new PlayerId(0), Faction.Soviet, 5000, new PowerState(100, 50),
            own, enemies, [], [], new Dictionary<RegionId, GameTime>(), [], map);
        SquadOrder order = new("attack", ObjectiveKind.AttackRegion, Target, [.. squad.Select(static e => e.Id)], Engage: engage, RetreatBelowForceRatio: 0.6, StandoffBufferCells: buffer);
        return controller.Tick(belief, [order], leases, rules);
    }

    [Fact]
    public void An_outnumbered_sieging_squad_retreats_like_any_other()
    {
        MapInfo map = Map(waterBetween: false);
        SquadController controller = new(new SquadControllerOptions());
        OwnEntity yard = Own(map, 9, "NACNST", UnitRole.Production, new Cell(1, 5), EntityKind.Building);
        OwnEntity v3 = Own(map, 1, "V3", UnitRole.Artillery, new Cell(4, 10));
        OwnEntity rhino = Own(map, 2, "HTNK", UnitRole.AntiArmor, new Cell(4, 11));
        List<EnemyContact> tanks = [.. Enumerable.Range(0, 10).Select(i => new EnemyContact(
            new EntityId((uint)(100 + i)), new PlayerId(1), "MTNK", UnitRole.AntiArmor, EntityKind.Vehicle, new Cell(5, 10), Staging,
            new GameTime(0), 1.0, 900, 1.0, false))];

        foreach (double t in new[] { 0.0, 6.0, 12.0 })
        {
            List<EnemyContact> seen = [Tower, .. tanks.Select(e => e with { LastSeenAt = GameTime.FromSeconds(t) })];
            IReadOnlyList<GameCommand> commands = Tick(controller, map, [yard, v3, rhino], seen, t, buffer: 1);

            Assert.DoesNotContain(commands, static c => c is AttackCommand or AttackMoveCommand);
            MoveCommand retreat = Assert.IsType<MoveCommand>(Assert.Single(commands));
            Assert.Equal(Staging, map.RegionOf(retreat.Destination)!.Id);
        }
    }

    [Fact]
    public void A_stand_off_cell_is_never_water_or_out_of_ground_reach_the_squad_assaults_instead()
    {
        MapInfo map = Map(waterBetween: true);
        SquadController controller = new(new SquadControllerOptions());
        OwnEntity v3 = Own(map, 1, "V3", UnitRole.Artillery, new Cell(0, 10));
        OwnEntity rhino = Own(map, 2, "HTNK", UnitRole.AntiArmor, new Cell(1, 10));

        IReadOnlyList<GameCommand> commands = Tick(controller, map, [v3, rhino], [Tower], 0, buffer: 1);

        Assert.NotEmpty(commands);
        foreach (GameCommand c in commands)
        {
            Cell? destination = c switch { MoveCommand m => m.Destination, AttackMoveCommand a => a.Destination, _ => null };
            if (destination is { } d) Assert.False(map.RegionOf(d)!.Water, $"{c} sends ground units into water");
        }
        Assert.IsType<AttackMoveCommand>(Assert.Single(commands));
    }

    [Fact]
    public void An_order_that_does_not_engage_moves_plainly_so_it_can_leave_a_region_with_enemies()
    {
        MapInfo map = Map(waterBetween: false);
        SquadController controller = new(new SquadControllerOptions());
        OwnEntity rhino = Own(map, 2, "HTNK", UnitRole.AntiArmor, new Cell(1, 10));
        EnemyContact pillbox = Tower with { Id = new EntityId(60), LastSeenPosition = new Cell(2, 10), LastSeenRegion = Staging };

        IReadOnlyList<GameCommand> commands = Tick(controller, map, [rhino], [pillbox], 0, buffer: 0, engage: false);

        MoveCommand move = Assert.IsType<MoveCommand>(Assert.Single(commands));
        Assert.Equal(Target, map.RegionOf(move.Destination)!.Id);
    }
}
