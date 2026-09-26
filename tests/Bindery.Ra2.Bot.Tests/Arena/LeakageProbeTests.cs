// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Sim;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary>
/// The arena's leakage probe must report a leak whenever one reaches the arm's frames, not only the leaks its
/// perturbation happens to exercise: it counts per-frame fog violations over the whole run as well as differing
/// strategist contexts.
/// </summary>
public sealed class LeakageProbeTests
{
    [Fact]
    public void The_arena_probe_finds_no_leak_and_checks_every_frame_for_hidden_objects()
    {
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();
        BotAgentFactory factory = new(rules, PlaybookLibrary.LoadDefault(), new ArenaRunContext(llmFake: true, null));

        LeakageProbeResult result = LeakageProbe.Run(new ArmSpec("selector", false, false), SimMaps.TwinValley, 1, rules, factory, perturbAtSeconds: 240, compareSeconds: 30, opponent: "rush");

        Assert.True(result.StatesMatchedBeforePerturbation);
        Assert.True(result.FramesCompared > 0);
        Assert.Equal(0, result.Differences);
        Assert.Equal(0, result.FogViolations);
    }
}
