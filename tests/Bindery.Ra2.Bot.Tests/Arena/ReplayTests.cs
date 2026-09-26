// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Runtime;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary>
/// The operator's replay tool: a run writes each match's decision log and settings, and <c>arena replay</c>
/// re-runs the match with <see cref="ReplayStrategist"/> in place of the recorded strategists and says whether the
/// decision log hash matches.
/// </summary>
public sealed class ReplayTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), $"bindery-replay-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private string RunOne(string arm)
    {
        int code = Program.Main(["run", "--arms", arm, "--maps", "training", "--opponents", "live-rush", "--seeds", "1",
            "--benchmark", "contested", "--max-seconds", "240", "--llm-fake", "--out", dir]);
        Assert.Equal(0, code);
        string decisions = Path.Combine(dir, "decisions");
        return Directory.GetFiles(decisions, $"{arm.Replace('+', '-')}_*.ndjson").OrderBy(static f => f, StringComparer.Ordinal).First();
    }

    [Theory]
    [InlineData("llm")]
    [InlineData("llm-shadow")]
    [InlineData("llm+fast")]
    [InlineData("bandit")]
    [InlineData("distilled")]
    public void A_recorded_match_replays_to_the_same_decision_log_hash(string arm)
    {
        string recorded = RunOne(arm);
        Assert.True(File.Exists(Path.ChangeExtension(recorded, ".match.json")), "the match settings are written next to the log");

        ReplayResult result = Program.ReplayMatch(recorded);

        Assert.True(result.Equal, $"replayed {result.ReplayedHash} vs recorded {result.RecordedHash}; first difference at record {result.FirstDifference}");
        Assert.Equal(0, result.Misses);
        Assert.Equal(result.RecordedHash, DecisionLogCodec.Hash(DecisionLogCodec.ReadAll(new StringReader(File.ReadAllText(recorded)))));
    }

    [Fact]
    public void A_log_that_does_not_follow_from_its_answers_replays_differently_and_says_so()
    {
        // Recorded answers are replayed as they are; everything else the log holds is recomputed. Editing a derived
        // record (here an operational plan's notes) is caught by the replay.
        string recorded = RunOne("llm");
        List<string> lines = [.. File.ReadAllLines(recorded)];
        int index = lines.FindIndex(static l => l.Contains("\"kind\":\"operations.plan\"", StringComparison.Ordinal));
        Assert.True(index >= 0);
        lines[index] = lines[index].Replace("\"notes\":[", "\"notes\":[\"edited\",", StringComparison.Ordinal);
        File.WriteAllLines(recorded, lines);

        ReplayResult result = Program.ReplayMatch(recorded);

        Assert.False(result.Equal);
        Assert.Equal(index, result.FirstDifference);
        Assert.NotEqual(result.ManifestHash, result.RecordedHash);
    }

    [Fact]
    public void The_replay_subcommand_exits_zero_when_equal()
    {
        string recorded = RunOne("llm");
        Assert.Equal(0, Program.Main(["replay", recorded]));
    }
}
