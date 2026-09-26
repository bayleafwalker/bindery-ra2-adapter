// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Sim;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary>
/// The Claude strategist's failure details go into the hashed decision log, so they must be deterministic under a
/// fixed simulated latency: a wall-clock latency would give the same match a different log hash on every run.
/// </summary>
public sealed class ClaudeFailureRecordTests
{
    [Fact]
    public void A_failed_request_is_logged_with_the_simulated_latency_not_the_wall_clock()
    {
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();
        // No credential: every request fails as unauthorized, which is the failure path under test.
        ArenaRunContext context = new(llmFake: false, llmLatencySeconds: 3);
        context.MarkLlmSkipped("test: no credential");
        BotAgentFactory factory = new(rules, PlaybookLibrary.LoadDefault(), context);
        List<DecisionRecord> log = [];

        MatchRunner.Run(new ArmSpec("llm", false, false), "rush", SimMaps.TwinValley, "training", 1, 30, rules, factory, armLog: records => log.AddRange(records));

        List<JsonElement> failures = [.. log
            .Where(static r => r.Kind == DecisionRecordKinds.ProposalFailed && !r.Data.TryGetProperty("role", out _))
            .Select(static r => r.Data)];
        Assert.NotEmpty(failures);
        Assert.All(failures, static f => Assert.Equal(3.0, f.GetProperty("cost").GetProperty("latencySeconds").GetDouble()));
    }
}
