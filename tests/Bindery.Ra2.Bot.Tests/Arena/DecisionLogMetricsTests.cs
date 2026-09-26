// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Sim;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary>
/// The arm's strategy figures describe the strategist under test: the fallback's instant proposals stay out of its
/// lateness and invalid-plan rate, a shadow strategist's lateness and fog rejections are reported, and a failed
/// request's billed tokens count toward cost.
/// </summary>
public sealed class DecisionLogMetricsTests
{
    private static DecisionRecord R(string kind, object data) => new(kind, GameTime.FromSeconds(10), 150, BotJson.ToElement(data));

    private static readonly ProposalCost Billed = new(4, 1_000_000, 100_000, 0, "claude-sonnet-5");

    [Fact]
    public void Fallback_proposals_stay_out_of_the_arm_figures_and_shadow_and_failed_requests_are_counted()
    {
        object[] fogReject = [new { code = "fog.unobserved_type", severity = "Reject" }];
        DecisionRecord[] records =
        [
            R(DecisionRecordKinds.Proposal, new { role = "Primary", strategistId = "claude", latencyFrames = 60, cost = Billed }),
            R(DecisionRecordKinds.Validation, new { role = "Primary", accepted = false, issues = fogReject }),
            R(DecisionRecordKinds.Proposal, new { role = "Fallback", strategistId = "selector", latencyFrames = 0 }),
            R(DecisionRecordKinds.Validation, new { role = "Fallback", accepted = false, issues = Array.Empty<object>() }),
            R(DecisionRecordKinds.LateDiscarded, new { role = "Fallback", reason = "age" }),
            R(DecisionRecordKinds.ShadowProposal, new { role = "Shadow", strategistId = "claude", latencyFrames = 75, cost = Billed, accepted = false, issues = fogReject }),
            R(DecisionRecordKinds.ProposalFailed, new { strategist = "claude", code = "parse_failed", cost = Billed }),
            R(DecisionRecordKinds.ProposalFailed, new { role = "Primary", strategistId = "claude", reason = "no_opinion" }),
        ];
        ArenaAgentStats stats = new();

        DecisionLogMetrics.Apply(stats, records);

        Assert.Equal(1, stats.Proposals);
        Assert.Equal(1, stats.Rejected);
        Assert.Equal(0, stats.LateDiscarded);
        Assert.Equal([4.0], stats.LateSeconds);
        Assert.Equal(1, stats.FallbackProposals);
        Assert.Equal([5.0], stats.ShadowLateSeconds);
        Assert.Equal(1, stats.ShadowRejected);
        Assert.Equal(1, stats.ShadowFogRejections);
        Assert.Equal(1, stats.FogRejections);
        Assert.Equal(3 * 1_100_000, stats.TokensIn + stats.TokensOut);
        Assert.True(stats.FailedRequestUsd > 0);
        Assert.Equal(3 * stats.FailedRequestUsd, stats.Usd, 9);
    }

    // End to end: every llm proposal waits the fixed simulated latency, so the arm's lateness can never be below it,
    // however many instant fallback proposals the match also had.
    [Fact]
    public void The_llm_arm_lateness_is_the_llm_latency_not_diluted_by_the_fallback()
    {
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();
        BotAgentFactory factory = new(rules, PlaybookLibrary.LoadDefault(), new ArenaRunContext(llmFake: true, null));

        MatchRecord match = MatchRunner.Run(new ArmSpec("llm", false, true), "rush", SimMaps.TwinValley, "training", 2, 300, rules, factory);

        PlayerMatchMetrics arm = match.Players["arm"];
        Assert.NotEmpty(arm.LateSeconds);
        Assert.All(arm.LateSeconds, s => Assert.True(s >= BotAgentFactory.FakeLatencySeconds - 1.0 / GameTime.FramesPerSecond, $"lateness {s} s"));
    }
}
