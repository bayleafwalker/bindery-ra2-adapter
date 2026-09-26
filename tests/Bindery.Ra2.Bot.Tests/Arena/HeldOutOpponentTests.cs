// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Sim;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary>
/// The proposal's "win rate on held-out maps and opponent styles": an opponent set that is held out, contested
/// (not the bot's own stack, and it wins some games), never learned or tuned from, and reported on its own.
/// </summary>
public sealed class HeldOutOpponentTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), $"bindery-heldout-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void The_held_out_set_is_disjoint_from_training_and_never_the_bots_own_stack()
    {
        Assert.NotEmpty(OpponentSets.HeldOut);
        Assert.Empty(OpponentSets.HeldOut.Intersect(OpponentSets.Training));
        Assert.All(OpponentSets.HeldOut, static o =>
        {
            Assert.True(OpponentSets.IsHeldOut(o));
            Assert.False(BotAgentFactory.OpponentStyles.ContainsKey(o));
            Assert.False(o.StartsWith(BotAgentFactory.LivePrefix, StringComparison.Ordinal));
            Assert.True(BotAgentFactory.IsOpponent(o));
        });
        Assert.All(OpponentSets.Training, static o => Assert.False(OpponentSets.IsHeldOut(o)));
        Assert.True(OpponentSets.IsHeldOut("ai-horde:easy"));
        // The tuner's default opponent list is the training scripted and pinned styles.
        Assert.DoesNotContain(BotAgentFactory.AllOpponents, OpponentSets.IsHeldOut);
    }

    [Fact]
    public void Opponent_groups_expand_on_the_command_line()
    {
        Assert.Equal(OpponentSets.HeldOut, CliOptions.Parse(["run", "--opponents", "heldout"]).Opponents);
        Assert.Equal(OpponentSets.Training, CliOptions.Parse(["run", "--opponents", "training"]).Opponents);
        IReadOnlyList<string> all = CliOptions.Parse(["run", "--opponents", "all"]).Opponents;
        Assert.Superset(new HashSet<string>(OpponentSets.HeldOut), new HashSet<string>(all));
        Assert.Superset(new HashSet<string>(OpponentSets.HeldOut), new HashSet<string>(CliOptions.Parse(["run", "--benchmark", "contested"]).Opponents));
    }

    [Fact]
    public void Learning_and_tuning_refuse_held_out_opponents()
    {
        Assert.Throws<ArgumentException>(() => OpponentSets.EnsureTraining("ai-horde", "tuning"));
        OpponentSets.EnsureTraining("ai-rush", "tuning");
        Assert.DoesNotContain(OpponentSets.TeacherOpponents(["ai-horde", "live-rush"]), OpponentSets.IsHeldOut);
        Assert.NotEmpty(OpponentSets.TeacherOpponents(["ai-horde"]));
        Assert.DoesNotContain(OpponentSets.TeacherOpponents(["ai-horde"]), OpponentSets.IsHeldOut);
    }

    [Fact]
    public void A_held_out_opponent_is_contested_it_beats_the_selector_on_some_seeds()
    {
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();
        BotAgentFactory factory = new(rules, PlaybookLibrary.LoadDefault(), new ArenaRunContext(llmFake: true, null));
        SimMap map = SimMaps.All.Single(static m => m.Map.MapId == "river-crossing");

        // Some seeds, not a particular one: any change to the simulator reshuffles which seeds the opponent wins.
        List<int?> winners = [.. Enumerable.Range(1, 4).Select(seed =>
            MatchRunner.Run(new ArmSpec("selector", false, true), "ai-horde", map, "training", seed, 1200, rules, factory, benchmark: BenchmarkSettings.Contested).Winner)];

        Assert.Contains(1, winners);
    }

    [Fact]
    public void The_bandit_never_learns_from_a_held_out_match()
    {
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();
        ArenaRunContext context = new(llmFake: true, null) { BanditLearning = false };
        BotAgentFactory factory = new(rules, PlaybookLibrary.LoadDefault(), context);
        SimMap map = SimMaps.Training[0];

        MatchRunner.Run(new ArmSpec("bandit", false, true), "ai-horde", map, "training", 1, 300, rules, factory, benchmark: BenchmarkSettings.Contested);
        Assert.Equal(0, context.Bandit.Updates);

        context.BanditLearning = true;
        MatchRunner.Run(new ArmSpec("bandit", false, true), "live-rush", map, "training", 1, 300, rules, factory, benchmark: BenchmarkSettings.Contested);
        Assert.True(context.Bandit.Updates > 0);
    }

    [Fact]
    public void Exported_datasets_leave_out_held_out_opponents_and_the_report_splits_them()
    {
        Assert.Equal(0, Program.Main(["run", "--arms", "selector,bandit", "--maps", "training", "--opponents", "ai-horde,live-rush", "--seeds", "1",
            "--benchmark", "contested", "--max-seconds", "240", "--no-decisions", "--out", dir]));

        string dataset = File.ReadAllText(Path.Combine(dir, "dataset-selector.ndjson"));
        Assert.NotEmpty(dataset);
        Assert.DoesNotContain("ai-horde", dataset, StringComparison.Ordinal);
        Assert.Contains("live-rush", dataset, StringComparison.Ordinal);
        string report = File.ReadAllText(Path.Combine(dir, "report.md"));
        Assert.Contains("## Held-out opponents", report, StringComparison.Ordinal);
        Assert.Contains("| selector | heldout | all |", report, StringComparison.Ordinal);
        Assert.Contains("| selector | training | training |", report, StringComparison.Ordinal);
        Assert.Contains("### Paired differences vs selector, held-out opponents only", report, StringComparison.Ordinal);
    }
}
