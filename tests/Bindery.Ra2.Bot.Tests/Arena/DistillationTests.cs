// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Sim;
using Bindery.Ra2.Bot.Strategy;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary>
/// Step 7 as the spec and proposal put it: distil the <c>llm</c> arm's decisions, reserve the LLM for unusual
/// states, and report the escalation rate next to inference cost.
/// </summary>
public sealed class DistillationTests
{
    [Fact]
    public void The_teacher_is_the_runs_llm_arm_when_there_is_one()
    {
        List<ArmSpec> arms = [new("selector", false, true), new("llm", false, true), new("llm", true, true), new("distilled", false, true)];

        (ArmSpec teacher, bool reported) = Program.DistillTeacher(arms, llmFake: true);

        Assert.Equal(new ArmSpec("llm", false, true), teacher);
        Assert.True(reported);
    }

    [Fact]
    public void Without_an_llm_arm_the_teacher_is_an_unreported_llm_run_never_the_selector()
    {
        List<ArmSpec> arms = [new("selector", false, true), new("distilled", false, true)];

        (ArmSpec teacher, bool reported) = Program.DistillTeacher(arms, llmFake: true);

        Assert.Equal("llm", teacher.Name);
        Assert.False(teacher.Oracle);
        Assert.False(reported);
    }

    [Fact]
    public void Out_of_distribution_states_escalate_to_the_claude_strategist()
    {
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();
        ArenaRunContext context = new(llmFake: true, llmLatencySeconds: null) { DistillDataset = DecisionDataset.Empty, DistillSource = "empty" };
        BotAgentFactory factory = new(rules, PlaybookLibrary.LoadDefault(), context);
        SimMap map = SimMaps.TwinValley;
        SkirmishSimulation sim = new(map, rules, new SimSettings(1, 60, [new SimPlayer(new PlayerId(0), Faction.Allied), new SimPlayer(new PlayerId(1), Faction.Soviet)]));
        using IArenaAgent agent = factory.Create(new ArmSpec("distilled", false, true), new PlayerId(0), Faction.Allied, sim.Map, 1);

        for (int frame = 0; frame < GameTime.FramesPerSecond * 15; frame++)
        {
            foreach (GameCommand c in agent.Tick(sim.Observe(new PlayerId(0)))) sim.Submit(new PlayerId(0), c);
            sim.Step();
        }

        // An empty dataset is out of distribution everywhere, so every primary decision escalated: the proposal the
        // scheduler received from the distilled strategist is Claude's (source Llm), not a selector's.
        DecisionRecord proposal = agent.DecisionLog.First(static r => r.Kind == DecisionRecordKinds.Proposal
            && r.Data.GetProperty("role").GetString() == "Primary");
        Assert.Equal("distilled", proposal.Data.GetProperty("strategistId").GetString());
        Assert.Equal(nameof(IntentSource.Llm), proposal.Data.GetProperty("intent").GetProperty("source").GetString());
        Assert.True(proposal.Data.GetProperty("cost").GetProperty("inputTokens").GetInt64() > 0);
    }

    [Fact]
    public void The_report_shows_the_escalation_rate_next_to_inference_cost()
    {
        static PlayerMatchMetrics P(int decisions, int escalations, double usd) => new(
            Faction.Allied, decisions, 0, 0, [], 1, 0, 0, new Dictionary<string, int>(), 0, 0, 0, 0, 0.1, 1000, 500, 500, 100, 10, "claude-opus-5", usd, 5000, 10, 3, 2000, null, [])
        {
            DistilledDecisions = decisions,
            DistilledEscalations = escalations,
        };
        static MatchRecord M(string arm, int seed, PlayerMatchMetrics p) =>
            new(arm, "live-rush", "twin-valley", "training", seed, 0, "elimination", 300,
                new Dictionary<string, PlayerMatchMetrics> { ["arm"] = p, ["opponent"] = p });
        List<MatchRecord> matches = [M("distilled", 1, P(10, 3, 0.2)), M("distilled", 2, P(10, 1, 0.1)), M("llm", 1, P(10, 0, 1.0)), M("llm", 2, P(10, 0, 1.2))];

        string report = ReportBuilder.Build(matches, [], [], CliOptions.Parse(["run", "--arms", "llm,distilled", "--llm-fake"]), "test");

        Assert.Contains("## Distillation", report, StringComparison.Ordinal);
        Assert.Contains("| distilled | 20 | 4/20 (0.200) | $0.1500 | $1.1000 |", report, StringComparison.Ordinal);
    }
}
