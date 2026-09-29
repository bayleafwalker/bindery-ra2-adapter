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

    [Fact]
    public void Diagnostic_knob_flag_parses_but_is_a_diagnostic_not_an_operational_or_feature_knob()
    {
        CliOptions options = CliOptions.Parse(["run", "--knob", "ScoutingCoverageCap=0.39"]);

        Assert.Equal(0.39, options.ArmKnobs["ScoutingCoverageCap"]);
        Assert.Single(TuningKnobs.Diagnostic, k => k.Name == "ScoutingCoverageCap");
        Assert.DoesNotContain(TuningKnobs.Features, k => k.Name == "ScoutingCoverageCap");
        Assert.DoesNotContain(TuningKnobs.Operational, k => k.Name == "ScoutingCoverageCap");
    }

    [Fact]
    public void ScoutingCoverageCap_round_trips_through_Set_and_Get()
    {
        OptionKnob knob = Assert.Single(TuningKnobs.Diagnostic, k => k.Name == "ScoutingCoverageCap");
        double value = (knob.Min + knob.Max) / 2;
        Assert.Equal(value, TuningKnobs.Get(TuningKnobs.Set(new Bindery.Ra2.Bot.Features.FeatureOptions(), "ScoutingCoverageCap", value), "ScoutingCoverageCap"), 6);
    }

    public static TheoryData<string> AllKnobNames()
    {
        TheoryData<string> data = [];
        foreach (OptionKnob k in TuningKnobs.Operational.Concat(TuningKnobs.Features).Concat(TuningKnobs.Diagnostic)) data.Add(k.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllKnobNames))]
    public void Every_knob_applies_through_the_arena_path_and_lands_on_the_right_options(string name)
    {
        OptionKnob knob = TuningKnobs.Find(name)!;
        double value = (knob.Min + knob.Max) / 2;
        double expected = knob.Integer ? Math.Round(value, MidpointRounding.AwayFromZero) : value;
        BotAgentFactory factory = new(RulesDatabase.LoadEmbeddedFixture(), PlaybookLibrary.LoadDefault(), new ArenaRunContext(llmFake: true, llmLatencySeconds: null)
        {
            ArmKnobs = new Dictionary<string, double> { [name] = value },
        });

        using IArenaAgent arm = factory.Create(new ArmSpec("selector", false, true), new PlayerId(0), Faction.Allied, Bindery.Ra2.Bot.Sim.SimMaps.TwinValley.Map, 1);

        Assert.Contains(arm.Stats.Labels, l => l.StartsWith("knob:" + name + "=", StringComparison.Ordinal));
        if (TuningKnobs.IsOperational(name))
            Assert.Equal(expected, TuningKnobs.Get(TuningKnobs.Set(new Bindery.Ra2.Bot.Operations.OperationalOptions(), name, value), name), 6);
        else
            Assert.Equal(expected, TuningKnobs.Get(TuningKnobs.Set(new Bindery.Ra2.Bot.Features.FeatureOptions(), name, value), name), 6);
    }

    [Theory]
    [InlineData("BaseSightingWeightCap=NaN")]
    [InlineData("BaseSightingWeightCap=Infinity")]
    [InlineData("BaseSightingWeightCap=-Infinity")]
    [InlineData("BaseSightingWeightCap=1.5")]
    [InlineData("BaseSightingWeightCap=-0.1")]
    [InlineData("MinAttackForceRatio=99")]
    public void Knob_values_that_are_not_finite_or_outside_the_declared_range_are_rejected_by_name(string spec)
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => CliOptions.Parse(["run", "--knob", spec]));
        Assert.Contains(spec[..spec.IndexOf('=', StringComparison.Ordinal)], ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Knob_values_at_the_range_edges_are_accepted()
    {
        CliOptions options = CliOptions.Parse(["run", "--knob", "BaseSightingWeightCap=0", "--knob", "MinAttackForceRatio=2.5"]);
        Assert.Equal(0, options.ArmKnobs["BaseSightingWeightCap"]);
        Assert.Equal(2.5, options.ArmKnobs["MinAttackForceRatio"]);
    }
}
