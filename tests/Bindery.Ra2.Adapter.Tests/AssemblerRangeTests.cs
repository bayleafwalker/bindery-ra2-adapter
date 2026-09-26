// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Adapter.Bot;
using Bindery.Ra2.Bot;
using Xunit;
using static Bindery.Ra2.Adapter.Tests.AssemblerTestKit;

namespace Bindery.Ra2.Adapter.Tests;

/// <summary>A value outside the contract type's range is a contract violation, never a wrapped value that aliases another entity.</summary>
public sealed class AssemblerRangeTests
{
    [Fact]
    public void AnEntityIdBeyondUInt32_IsExcluded_AndDoesNotAliasATrackedEntity()
    {
        Ra2ObservationAssembler assembler = NewAssembler();
        assembler.Ingest(Event("game.unit.created", Own(1, 7, 10, 10)));
        assembler.Ingest(Event("game.unit.created", Own(2, 4294967303L, 12, 12)));

        ObservationFrame frame = Assert.Single(assembler.Ingest(Event("game.unit.destroyed", new { frame = 15, id = 4294967303L })));

        ObservedEntity entity = Assert.Single(frame.Entities);
        Assert.Equal(new Cell(10, 10), entity.Position);
        Assert.DoesNotContain(frame.Events, static e => e.Kind == GameEventKind.EntityDestroyed);
        Assert.Equal(2, assembler.MissingFields.Entries.Count(static e => e.Field == "id" && e.Effect.Contains("out_of_range", StringComparison.Ordinal)));
    }

    [Fact]
    public void AnIntFieldBeyondInt32_IsExcluded_NotWrapped()
    {
        Ra2ObservationAssembler assembler = NewAssembler();

        ObservationFrame frame = Assert.Single(assembler.Ingest(Event("game.unit.created", Own(15, 7, 10, 10, health: 3_000_000_000L))));

        Assert.Empty(frame.Entities);
        Assert.Contains(assembler.MissingFields.Entries, static e => e.Field == "health" && e.Effect.Contains("out_of_range", StringComparison.Ordinal));
    }
}
