// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Sim;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Sim;

public sealed class SimAdaptersTests
{
    [Fact]
    public async Task SimPlayerSource_returns_null_once_the_match_has_ended()
    {
        TestRules rules = new();
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 29, maxSeconds: 0.5));
        SimPlayerSource source = new(sim, new PlayerId(0));

        sim.Advance(1);
        Assert.True(sim.MatchEnded);

        Assert.Null(await source.NextAsync());
    }

    [Fact]
    public async Task SimPlayerSource_reflects_the_current_frame_without_advancing_it()
    {
        TestRules rules = new();
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 29, maxSeconds: 30));
        SimPlayerSource source = new(sim, new PlayerId(0));

        ObservationFrame? first = await source.NextAsync();
        ObservationFrame? second = await source.NextAsync();

        Assert.NotNull(first);
        Assert.Equal(first!.Time, second!.Time);
    }

    [Fact]
    public void SimCommandSink_submits_under_the_sink_owner_not_the_caller()
    {
        TestRules rules = new();
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 29, maxSeconds: 30));
        PlayerId owner = new(0);
        PlayerId other = new(1);
        SimCommandSink sink = new(sim, owner);

        EntityId ownerMcv = sim.Observe(owner, ObservationMode.Oracle).Entities.Single(e => e.Owner == owner).Id;
        sink.Submit(new DeployCommand("test", ownerMcv));
        sim.Step();

        // The command must have taken effect for `owner`, never silently attributed to `other`.
        Assert.True(sim.Observe(owner, ObservationMode.Oracle).Entities.Single(e => e.Id == ownerMcv).Deployed);
        Assert.Equal(0, sim.RejectedCommandCount(owner));
        Assert.Equal(0, sim.RejectedCommandCount(other));
    }
}
