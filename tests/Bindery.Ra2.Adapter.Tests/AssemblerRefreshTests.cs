// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Adapter.Bot;
using Bindery.Ra2.Bot;
using Xunit;
using static Bindery.Ra2.Adapter.Tests.AssemblerTestKit;

namespace Bindery.Ra2.Adapter.Tests;

/// <summary>A repeated upsert is a state refresh of a tracked entity, not a second creation.</summary>
public sealed class AssemblerRefreshTests
{
    [Fact]
    public void ARepeatedUpsertOfAnOwnEntity_RefreshesItsState_WithoutASecondCreatedEvent()
    {
        Ra2ObservationAssembler assembler = NewAssembler();
        List<ObservationFrame> frames = [];
        frames.AddRange(assembler.Ingest(Event("game.unit.created", Own(1, 7, 10, 10))));
        frames.AddRange(assembler.Ingest(Event("game.unit.created", Own(20, 7, 12, 10, health: 100))));
        frames.AddRange(assembler.Ingest(Event("game.economy.credits", Credits(30))));

        ObservedEntity latest = Assert.Single(frames[^1].Entities);
        Assert.Equal(new Cell(12, 10), latest.Position);
        Assert.Equal(100, latest.Health);
        Assert.Single(frames.SelectMany(static f => f.Events), static e => e.Kind == GameEventKind.EntityCreated);
    }

    [Fact]
    public void AnEnemyResightedInEveryWindow_IsCreatedOnce()
    {
        Ra2ObservationAssembler assembler = NewAssembler();
        List<ObservationFrame> frames = [];
        frames.AddRange(assembler.Ingest(Event("game.unit.created", Enemy(1, 9, 100, 100))));
        frames.AddRange(assembler.Ingest(Event("game.unit.created", Enemy(14, 9, 101, 100))));
        frames.AddRange(assembler.Ingest(Event("game.unit.created", Enemy(29, 9, 102, 100))));
        frames.AddRange(assembler.Ingest(Event("game.economy.credits", Credits(30))));

        Assert.Single(frames.SelectMany(static f => f.Events), static e => e.Kind == GameEventKind.EntityCreated);
    }
}
