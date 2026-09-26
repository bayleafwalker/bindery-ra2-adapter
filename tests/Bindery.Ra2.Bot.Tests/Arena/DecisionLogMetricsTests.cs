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

    [Fact]
    public void Cost_counts_cache_writes_prices_each_request_as_billed_and_keeps_the_serving_models_apart()
    {
        ProposalCost cacheWrite = new(4, 1000, 100, 0, "claude-sonnet-5", CacheCreationTokens: 50_000);
        // A fallback-served reply whose declined attempt the strategist priced at its own model's rate.
        ProposalCost fallbackServed = new(4, 1000, 100, 0, "claude-opus-4-8", Usd: 0.5);
        ProposalCost unpriced = new(4, 1000, 100, 0, "claude-unknown-9");
        DecisionRecord[] records =
        [
            R(DecisionRecordKinds.Proposal, new { role = "Primary", strategistId = "claude", latencyFrames = 60, cost = fallbackServed }),
            R(DecisionRecordKinds.Proposal, new { role = "Primary", strategistId = "claude", latencyFrames = 60, cost = cacheWrite }),
            R(DecisionRecordKinds.ProposalFailed, new { strategist = "claude", code = "parse_failed", cost = unpriced }),
        ];
        ArenaAgentStats stats = new();

        DecisionLogMetrics.Apply(stats, records);

        Assert.Equal(3 * 1000 + 50_000, stats.TokensIn);
        double expected = 0.5 + Bindery.Ra2.Bot.Claude.PriceTable.CostUsd("claude-sonnet-5", 1000, 100, 0, 50_000)!.Value;
        Assert.Equal(expected, stats.Usd, 12);
        Assert.Equal(1, stats.UnpricedRequests);
        // The serving models are tallied; none of them relabels the arm.
        Assert.Null(stats.Model);
        Assert.Equal(new Dictionary<string, int> { ["claude-opus-4-8"] = 1, ["claude-sonnet-5"] = 1, ["claude-unknown-9"] = 1 }, stats.ServedBy);
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
