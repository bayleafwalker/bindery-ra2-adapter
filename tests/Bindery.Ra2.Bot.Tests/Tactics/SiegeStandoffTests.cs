// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Tactics;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Tactics;

/// <summary>
/// <c>siegeRangeBufferCells</c> in action: artillery holds outside a known defense's range plus the buffer,
/// in a region other than the defense's, and bombards it from there. Uses the committed approximate fixture
/// (V3 range 10, Prism tower range 8).
/// </summary>
public sealed class SiegeStandoffTests
{
    private static readonly RegionId Staging = new(1);
    private static readonly RegionId Target = new(2);

    private static readonly MapInfo Map = new(
        "siege",
        60,
        20,
        [
            new Region(Staging, "staging", new Cell(0, 10), 8, false, false, false),
            new Region(Target, "target", new Cell(24, 10), 8, true, false, false),
        ],
        [new RegionLink(Staging, Target, 24, true, false)],
        []);

    // A Prism tower on the staging side of the target region (closer to the target's centre, so it is the target's).
    private static readonly EnemyContact Tower = new(
        new EntityId(50), new PlayerId(1), "GAPRIS", UnitRole.Defense, EntityKind.Building, new Cell(18, 10), Target,
        new GameTime(0), 1.0, 1500, 1.0, false);

    private static OwnEntity Own(uint id, string type, UnitRole role, Cell cell) =>
        new(new EntityId(id), type, role, EntityKind.Vehicle, cell, Map.RegionOf(cell)!.Id, 1.0, 900, false);

    private static BeliefSnapshot Belief(IReadOnlyList<OwnEntity> own, double seconds) => new(
        1, GameTime.FromSeconds(seconds), ObservationMode.Belief, new PlayerId(0), Faction.Soviet, 5000, new PowerState(100, 50),
        own, [Tower], [], [], new Dictionary<RegionId, GameTime>(), [], Map);

    private static (IReadOnlyList<GameCommand> Commands, IRulesDatabase Rules) Tick(SquadController controller, FakeLeaseManager leases, IReadOnlyList<OwnEntity> own, double buffer, double seconds)
    {
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();
        foreach (OwnEntity e in own) leases.Grant(LeaseKey.Unit(e.Id), "squad:attack", GameTime.FromSeconds(seconds), 10);
        SquadOrder order = new("attack", ObjectiveKind.AttackRegion, Target, [.. own.Select(static e => e.Id)], Engage: true, RetreatBelowForceRatio: 0.6, StandoffBufferCells: buffer);
        return (controller.Tick(Belief(own, seconds), [order], leases, rules), rules);
    }

    [Fact]
    public void With_a_buffer_the_squad_holds_outside_defense_range_and_the_artillery_bombards()
    {
        SquadController controller = new(new SquadControllerOptions());
        FakeLeaseManager leases = new();
        OwnEntity v3 = Own(1, "V3", UnitRole.Artillery, new Cell(0, 10));
        OwnEntity rhino = Own(2, "HTNK", UnitRole.AntiArmor, new Cell(1, 10));

        (IReadOnlyList<GameCommand> approach, IRulesDatabase rules) = Tick(controller, leases, [v3, rhino], buffer: 1, seconds: 0);

        // Approach: everyone moves (not attack-moves into the target) to a stand-off cell.
        MoveCommand move = Assert.IsType<MoveCommand>(Assert.Single(approach));
        double towerRange = rules.Get("GAPRIS").Range;
        double distance = move.Destination.DistanceTo(Tower.LastSeenPosition);
        Assert.InRange(distance, towerRange + 1, rules.Get("V3").Range);
        Assert.NotEqual(Target, Map.RegionOf(move.Destination)!.Id);

        // In position: the V3 fires on the tower from there, and nothing walks into the tower's range.
        OwnEntity v3AtStandoff = v3 with { Position = move.Destination, Region = Map.RegionOf(move.Destination)!.Id };
        OwnEntity rhinoAtStandoff = rhino with { Position = move.Destination, Region = Map.RegionOf(move.Destination)!.Id };
        (IReadOnlyList<GameCommand> siege, _) = Tick(controller, leases, [v3AtStandoff, rhinoAtStandoff], buffer: 1, seconds: 2);

        AttackCommand bombard = Assert.Single(siege.OfType<AttackCommand>());
        Assert.Equal(Tower.Id, bombard.Target);
        Assert.Equal([v3.Id], bombard.Units);
        Assert.DoesNotContain(siege, static c => c is AttackMoveCommand);
        foreach (MoveCommand m in siege.OfType<MoveCommand>())
        {
            Assert.True(m.Destination.DistanceTo(Tower.LastSeenPosition) > towerRange, "no unit is sent into the tower's range");
        }
    }

    [Fact]
    public void Without_a_buffer_the_squad_assaults_the_target_region()
    {
        SquadController controller = new(new SquadControllerOptions());
        OwnEntity v3 = Own(1, "V3", UnitRole.Artillery, new Cell(0, 10));
        OwnEntity rhino = Own(2, "HTNK", UnitRole.AntiArmor, new Cell(1, 10));

        (IReadOnlyList<GameCommand> commands, _) = Tick(controller, new FakeLeaseManager(), [v3, rhino], buffer: 0, seconds: 0);

        AttackMoveCommand assault = Assert.IsType<AttackMoveCommand>(Assert.Single(commands));
        Assert.Equal(Target, Map.RegionOf(assault.Destination)!.Id);
    }

    [Fact]
    public void Siege_falls_back_to_an_assault_when_the_artillery_cannot_outrange_the_defense()
    {
        SquadController controller = new(new SquadControllerOptions());
        OwnEntity rhino = Own(2, "HTNK", UnitRole.AntiArmor, new Cell(1, 10));

        // No artillery in the squad: nothing can outrange the tower, so the buffer changes nothing.
        (IReadOnlyList<GameCommand> commands, _) = Tick(controller, new FakeLeaseManager(), [rhino], buffer: 5, seconds: 0);

        Assert.IsType<AttackMoveCommand>(Assert.Single(commands));
    }
}
