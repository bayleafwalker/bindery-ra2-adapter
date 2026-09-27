// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Tuning;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary>
/// <c>--knob Name=value</c> overrides a tuning knob for the arms only: live-* opponents keep the defaults, so a sweep
/// does not move the opponent it is measured against.
/// </summary>
public sealed class ArmKnobTests
{
    [Fact]
    public void Knob_flags_parse_and_unknown_knobs_are_refused()
    {
        CliOptions options = CliOptions.Parse(["run", "--knob", "MinAttackForceRatio=1.5", "--knob", "ThreatSearchCells=40"]);

        Assert.Equal(1.5, options.ArmKnobs["MinAttackForceRatio"]);
        Assert.Equal(40, options.ArmKnobs["ThreatSearchCells"]);
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["run", "--knob", "NoSuchKnob=1"]));
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["run", "--knob", "MinAttackForceRatio"]));
    }

    [Fact]
    public void Knobs_reach_the_arm_but_not_a_live_opponent()
    {
        ArenaRunContext context = new(llmFake: true, llmLatencySeconds: null)
        {
            ArmKnobs = new Dictionary<string, double> { ["MinAttackForceRatio"] = 1.5 },
        };
        BotAgentFactory factory = new(RulesDatabase.LoadEmbeddedFixture(), PlaybookLibrary.LoadDefault(), context);
        using IArenaAgent arm = factory.Create(new ArmSpec("selector", false, true), new PlayerId(0), Faction.Allied, Bindery.Ra2.Bot.Sim.SimMaps.TwinValley.Map, 1);
        using IArenaAgent live = factory.Create(new ArmSpec("live-rush", false, false), new PlayerId(1), Faction.Soviet, Bindery.Ra2.Bot.Sim.SimMaps.TwinValley.Map, 1);

        Assert.Contains("knob:MinAttackForceRatio=1.5", arm.Stats.Labels);
        Assert.DoesNotContain(live.Stats.Labels, static l => l.StartsWith("knob:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("MinAttackForceRatio")]
    [InlineData("SeenAttackForceRatio")]
    [InlineData("EnemyPriorValuePerSecond")]
    [InlineData("EnemyPriorMaxValue")]
    [InlineData("EnemyUncertaintyMargin")]
    public void Attack_gate_settings_are_tuning_knobs(string name)
    {
        OptionKnob knob = Assert.Single(TuningKnobs.Operational, k => k.Name == name);
        double value = (knob.Min + knob.Max) / 2;
        Assert.Equal(value, TuningKnobs.Get(TuningKnobs.Set(new Bindery.Ra2.Bot.Operations.OperationalOptions(), name, value), name), 6);
    }
}
