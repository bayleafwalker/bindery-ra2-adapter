// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Adapter.Bot;
using Bindery.Ra2.Bot;
using Xunit;
using static Bindery.Ra2.Adapter.Tests.AssemblerTestKit;

namespace Bindery.Ra2.Adapter.Tests;

/// <summary>What a frame claims is observed now was observed in that frame's window, and no death in fog reaches the bot.</summary>
public sealed class AssemblerFogTests
{
    [Fact]
    public void VisibleRegions_ComeFromOwnEntities_NeverFromWhereAnEnemyStands()
    {
        Ra2ObservationAssembler assembler = NewAssembler();
        assembler.Ingest(Event("game.unit.created", Own(1, 7, 10, 10)));
        assembler.Ingest(Event("game.unit.created", Enemy(2, 9, 100, 100)));

        ObservationFrame frame = Assert.Single(assembler.Ingest(Event("game.economy.credits", Credits(15))));

        Assert.Contains(frame.Entities, static e => e.Id == new EntityId(9));
        Assert.Equal([new RegionId(1)], frame.VisibleRegions.ToArray());
    }

    [Fact]
    public void AnEnemySightedOnce_IsNotReportedAsSeenInLaterFrames()
    {
        Ra2ObservationAssembler assembler = NewAssembler();
        assembler.Ingest(Event("game.unit.created", Enemy(1, 9, 100, 100)));

        ObservationFrame first = Assert.Single(assembler.Ingest(Event("game.economy.credits", Credits(15))));
        IReadOnlyList<ObservationFrame> later = assembler.Ingest(Event("game.economy.credits", Credits(9000)));

        Assert.Contains(first.Entities, static e => e.Id == new EntityId(9));
        Assert.NotEmpty(later);
        Assert.All(later, static f => Assert.DoesNotContain(f.Entities, static e => e.Id == new EntityId(9)));
    }

    [Fact]
    public void AnEnemyResightedEveryWindow_StaysInEveryFrame_AtItsLatestPosition()
    {
        Ra2ObservationAssembler assembler = NewAssembler();
        List<ObservationFrame> frames = [];
        frames.AddRange(assembler.Ingest(Event("game.unit.created", Enemy(1, 9, 100, 100))));
        frames.AddRange(assembler.Ingest(Event("game.unit.created", Enemy(14, 9, 101, 100))));
        frames.AddRange(assembler.Ingest(Event("game.unit.created", Enemy(29, 9, 102, 100))));
        frames.AddRange(assembler.Ingest(Event("game.economy.credits", Credits(30))));

        Assert.Equal(2, frames.Count);
        Assert.Equal(new Cell(101, 100), Assert.Single(frames[0].Entities).Position);
        Assert.Equal(new Cell(102, 100), Assert.Single(frames[1].Entities).Position);
    }

    [Fact]
    public void AnEnemyRemovalWithoutVisibilityOrOurKill_IsNotReported_AndTheMissingFlagIsAudited()
    {
        Ra2ObservationAssembler assembler = NewAssembler();
        assembler.Ingest(Event("game.unit.created", Enemy(1, 9, 100, 100)));

        IReadOnlyList<ObservationFrame> frames = assembler.Ingest(Event("game.unit.destroyed", new { frame = 15, id = 9 }));

        Assert.DoesNotContain(frames.SelectMany(static f => f.Events), static e => e.Entity == new EntityId(9) && e.Kind != GameEventKind.EntityCreated);
        Assert.Contains(assembler.MissingFields.Entries, static e => e.Field == "visible" && e.EventType == "game.unit.destroyed");
    }

    [Fact]
    public void AnEnemyRemovalMarkedVisible_OrKilledByUs_IsReported()
    {
        Ra2ObservationAssembler assembler = NewAssembler();
        assembler.Ingest(Event("game.unit.created", Enemy(1, 8, 100, 100)));
        assembler.Ingest(Event("game.unit.created", Enemy(1, 9, 100, 101)));
        assembler.Ingest(Event("game.unit.destroyed", new { frame = 2, id = 8, visible = true }));

        IReadOnlyList<ObservationFrame> frames = assembler.Ingest(Event("game.unit.killed", new { frame = 15, id = 9, killer = 0 }));

        List<GameEvent> events = [.. frames.SelectMany(static f => f.Events)];
        Assert.Contains(events, static e => e.Kind == GameEventKind.EntityDestroyed && e.Entity == new EntityId(8) && e.Owner == new PlayerId(1));
        Assert.Contains(events, static e => e.Kind == GameEventKind.EntityKilledByUs && e.Entity == new EntityId(9));
    }

    [Fact]
    public void ARemovalOfANeverSeenId_IsNeverReported_EvenWhenMarkedVisible()
    {
        Ra2ObservationAssembler assembler = NewAssembler();

        IReadOnlyList<ObservationFrame> frames = assembler.Ingest(Event("game.unit.destroyed", new { frame = 15, id = 12345, visible = true }));

        Assert.DoesNotContain(frames.SelectMany(static f => f.Events), static e => e.Entity == new EntityId(12345));
    }
}
