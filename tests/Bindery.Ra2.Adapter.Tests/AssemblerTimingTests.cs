// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Adapter.Bot;
using Bindery.Ra2.Bot;
using Xunit;
using static Bindery.Ra2.Adapter.Tests.AssemblerTestKit;

namespace Bindery.Ra2.Adapter.Tests;

/// <summary>A frame never carries state or events from after its own time, and the match end always reaches the bot.</summary>
public sealed class AssemblerTimingTests
{
    [Fact]
    public void NoFrame_CarriesStateOrEventsFromAfterItsOwnTime()
    {
        Ra2ObservationAssembler assembler = NewAssembler();
        List<ObservationFrame> frames = [];
        frames.AddRange(assembler.Ingest(Event("game.unit.created", Own(44, 7, 10, 10))));

        Assert.Equal([15L, 30L], frames.Select(static f => f.Time.Frame));
        Assert.All(frames, static f => Assert.Empty(f.Entities));
        Assert.All(frames, static f => Assert.Empty(f.Events));

        ObservationFrame next = Assert.Single(assembler.Ingest(Event("game.economy.credits", Credits(45))));
        Assert.Single(next.Entities);
        GameEvent created = Assert.Single(next.Events);
        Assert.Equal(44, created.Time.Frame);
    }
}
