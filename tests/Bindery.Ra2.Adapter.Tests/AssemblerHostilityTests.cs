// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Adapter.Bot;
using Bindery.Ra2.Bot;
using Xunit;
using static Bindery.Ra2.Adapter.Tests.AssemblerTestKit;

namespace Bindery.Ra2.Adapter.Tests;

/// <summary>Owners the caller declares non-hostile (allies, neutral and civilian houses) never become enemy contacts.</summary>
public sealed class AssemblerHostilityTests
{
    private static Ra2ObservationAssembler NewAssembler(IEnumerable<PlayerId> nonHostile) =>
        new(Self, Faction.Allied, Map, frameCadence: 15, nonHostileOwners: nonHostile);

    [Fact]
    public void AnAlliedOrNeutralOwner_NeverBecomesAnEnemyContact()
    {
        Ra2ObservationAssembler assembler = NewAssembler([new PlayerId(2)]);
        assembler.Ingest(Event("game.unit.created", Enemy(1, 20, 10, 11, owner: 2)));
        assembler.Ingest(Event("game.unit.created", Enemy(1, 21, 10, 12, owner: 1)));

        ObservationFrame frame = Assert.Single(assembler.Ingest(Event("game.unit.destroyed", new { frame = 15, id = 20, visible = true })));

        Assert.DoesNotContain(frame.Entities, static e => e.Owner == new PlayerId(2));
        Assert.Contains(frame.Entities, static e => e.Id == new EntityId(21));
        Assert.DoesNotContain(frame.Events, static e => e.Entity == new EntityId(20));
    }

    [Fact]
    public void TheControlledPlayer_CannotBeDeclaredNonHostile()
    {
        Assert.Throws<ArgumentException>(() => NewAssembler([Self]));
    }
}
