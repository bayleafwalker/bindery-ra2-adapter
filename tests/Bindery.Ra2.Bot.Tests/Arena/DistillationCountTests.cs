// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Sim;
using Bindery.Ra2.Bot.Strategy;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary>
/// The distilled arm's escalation rate counts every request its own model did not answer, including an escalation
/// still waiting on the LLM when the match ended (the scheduler cancels it without a log record).
/// </summary>
public sealed class DistillationCountTests
{
    [Fact]
    public void With_an_empty_dataset_every_decision_is_an_escalation_even_one_in_flight_at_match_end()
    {
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();
        BotAgentFactory factory = new(rules, PlaybookLibrary.LoadDefault(), new ArenaRunContext(llmFake: true, null) { DistillDataset = DecisionDataset.Empty });

        // The fake LLM answers after 4 s; at 133 s a request made at about 131 s is still waiting on it (found by
        // scanning match lengths), and 125 s ends with every request answered.
        foreach (int seconds in new[] { 125, 133 })
        {
            MatchRecord match = MatchRunner.Run(new ArmSpec("distilled", false, true), "rush", SimMaps.Training[0], "training", 1, seconds, rules, factory);

            PlayerMatchMetrics arm = match.Players["arm"];
            Assert.True(arm.DistilledDecisions > 0);
            Assert.True(arm.DistilledDecisions == arm.DistilledEscalations, $"{seconds} s: {arm.DistilledEscalations} of {arm.DistilledDecisions} counted as escalations");
        }
    }
}
