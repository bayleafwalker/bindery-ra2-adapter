// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Adapter.Bot;
using Bindery.Ra2.Bot;
using Xunit;
using static Bindery.Ra2.Adapter.Tests.AssemblerTestKit;

namespace Bindery.Ra2.Adapter.Tests;

/// <summary>The match end is the last telemetry, so it must produce a frame at once rather than wait for a boundary.</summary>
public sealed class AssemblerMatchEndTests
{
    [Fact]
    public void TheMatchEnd_IsDeliveredAtOnce_WithTheEventsBeforeIt_AndNothingFollows()
    {
        Ra2ObservationAssembler assembler = NewAssembler();
        Assert.Single(assembler.Ingest(Event("game.economy.credits", Credits(15))));
        Assert.Empty(assembler.Ingest(Event("game.participant.defeated", new { frame = 20, owner = 1 })));

        ObservationFrame last = Assert.Single(assembler.Ingest(Event("game.lifecycle.ended", new { frame = 21 })));

        Assert.Equal(21, last.Time.Frame);
        Assert.Contains(last.Events, static e => e.Kind == GameEventKind.PlayerDefeated && e.Owner == new PlayerId(1));
        Assert.Contains(last.Events, static e => e.Kind == GameEventKind.MatchEnded);
        Assert.Empty(assembler.Ingest(Event("game.economy.credits", Credits(300))));
    }

    [Fact]
    public void AMatchEndOnACadenceBoundary_IsDeliveredExactlyOnce()
    {
        Ra2ObservationAssembler assembler = NewAssembler();

        IReadOnlyList<ObservationFrame> frames = assembler.Ingest(Event("game.lifecycle.ended", new { frame = 30 }));

        Assert.Equal([15L, 30L], frames.Select(static f => f.Time.Frame));
        Assert.Single(frames.SelectMany(static f => f.Events), static e => e.Kind == GameEventKind.MatchEnded);
    }
}
