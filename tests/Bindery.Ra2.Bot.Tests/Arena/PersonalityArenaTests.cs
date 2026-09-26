// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary><c>--personality</c> runs each arm under each style, and the report measures how distinct the styles are.</summary>
public sealed class PersonalityArenaTests
{
    [Fact]
    public void Personality_flag_multiplies_arms_by_styles()
    {
        CliOptions options = CliOptions.Parse(["run", "--arms", "selector,llm", "--personality", "aggressive,turtle"]);

        Assert.Equal(["selector@aggressive", "selector@turtle", "llm@aggressive", "llm@turtle"], options.ArmSpecs().Select(static a => a.ToString()));
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["run", "--personality", "reckless"]));
        Assert.Equal(["selector", "selector@tech"], CliOptions.Parse(["run", "--personality", "none,tech"]).ArmSpecs().Select(static a => a.ToString()));
    }

    [Fact]
    public void An_arm_with_a_personality_passes_it_to_its_strategists()
    {
        BotAgentFactory factory = new(RulesDatabase.LoadEmbeddedFixture(), PlaybookLibrary.LoadDefault(), new ArenaRunContext(llmFake: true, llmLatencySeconds: null));
        using IArenaAgent agent = factory.Create(new ArmSpec("selector", false, true) { Personality = "turtle" }, new PlayerId(0), Faction.Allied, Bindery.Ra2.Bot.Sim.SimMaps.TwinValley.Map, 1);

        Assert.Equal("turtle", ((BotArenaAgent)agent).Runtime.Options.Personality);
        Assert.Contains("personality:turtle", agent.Stats.Labels);
    }

    [Fact]
    public void The_report_measures_style_distinctness()
    {
        static PlayerMatchMetrics P(string playbook, string posture, double firstAttack) => new(
            Faction.Allied, 10, 0, 0, [], 1, 0, 0, new Dictionary<string, int>(), 0, 0, 0, 0, 0.1, 1000, 500, 500, 0, 0, null, 0, 5000, 10, 3, 2000, null, [], firstAttack)
        {
            PlaybookSeconds = new Dictionary<string, double> { [playbook] = 300 },
            PostureSeconds = new Dictionary<string, double> { [posture] = 300 },
        };
        static MatchRecord M(string arm, int seed, PlayerMatchMetrics p) =>
            new(arm, "live-rush", "twin-valley", "training", seed, 0, "elimination", 300, new Dictionary<string, PlayerMatchMetrics> { ["arm"] = p, ["opponent"] = p });
        List<MatchRecord> matches =
        [
            M("selector@aggressive", 1, P("allied-grizzly-timing", "Pressure", 120)), M("selector@aggressive", 2, P("allied-grizzly-timing", "Pressure", 140)),
            M("selector@turtle", 1, P("allied-prism-turtle", "Turtle", 400)), M("selector@turtle", 2, P("allied-prism-turtle", "Turtle", 420)),
        ];

        string report = ReportBuilder.Build(matches, [], [], CliOptions.Parse(["run", "--arms", "selector", "--personality", "aggressive,turtle"]), "test");

        Assert.Contains("## Play styles", report, StringComparison.Ordinal);
        Assert.Contains("| selector@aggressive | 2 | allied-grizzly-timing 100% | Pressure 100% | 130 (2/2) |", report, StringComparison.Ordinal);
        // Disjoint playbook and posture distributions: Jensen–Shannon divergence 1 (base 2).
        Assert.Contains("| selector | aggressive | turtle | 1.000 | 1.000 | 280 |", report, StringComparison.Ordinal);
    }
}
