// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Sim;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary>
/// What the bandit may learn from. Each arm (observation mode and personality included) has its own learner, so
/// an oracle arm's omniscient episodes never shape a belief arm's policy and a belief-versus-oracle delta measures
/// information, not extra training. Held-out maps and held-out opponents never train it, so its held-out results
/// are out of sample.
/// </summary>
public sealed class BanditIsolationTests
{
    private static MatchRecord Play(BotAgentFactory factory, ArmSpec arm, int seed) =>
        MatchRunner.Run(arm, "rush", SimMaps.Training[0], "training", seed, 300, RulesDatabase.LoadEmbeddedFixture(), factory);

    private static BotAgentFactory Factory(ArenaRunContext context) =>
        new(RulesDatabase.LoadEmbeddedFixture(), PlaybookLibrary.LoadDefault(), context);

    [Fact]
    public void An_oracle_bandit_arm_run_first_leaves_the_belief_bandit_as_fresh_as_in_a_run_of_its_own()
    {
        ArmSpec belief = new("bandit", false, true);
        BotAgentFactory alone = Factory(new ArenaRunContext(llmFake: true, null));
        string? fresh = Play(alone, belief, 1).Players["arm"].DecisionLogHash;

        BotAgentFactory shared = Factory(new ArenaRunContext(llmFake: true, null));
        Play(shared, new ArmSpec("bandit", true, true), 1);
        Play(shared, new ArmSpec("bandit", true, true), 2);
        Play(shared, belief with { Personality = "turtle" }, 1);
        string? afterOthers = Play(shared, belief, 1).Players["arm"].DecisionLogHash;

        Assert.NotNull(fresh);
        Assert.Equal(fresh, afterOthers);
    }

    [Theory]
    [InlineData("rush", "training", true)]
    [InlineData("rush", "heldout", false)]
    [InlineData("ai-horde", "training", false)]
    [InlineData("ai-horde", "heldout", false)]
    public void The_bandit_learns_only_from_training_maps_against_training_opponents(string opponent, string split, bool learns)
    {
        Assert.Equal(learns, Program.BanditLearnsFrom(opponent, split));
    }

    [Fact]
    public void The_tuner_refuses_a_held_out_opponent_on_its_command_line()
    {
        Assert.Throws<ArgumentException>(() => Bindery.Ra2.Bot.Tune.TuneOptions.Parse(["search", "--date", "2026-09-26", "--out", "x", "--opponents", "ai-horde"]));
        Assert.Contains("ai-rush", Bindery.Ra2.Bot.Tune.TuneOptions.Parse(["search", "--date", "2026-09-26", "--out", "x", "--opponents", "ai-rush"]).Opponents);
    }
}
