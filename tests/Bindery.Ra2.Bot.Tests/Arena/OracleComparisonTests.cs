// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arena;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary>
/// The perception-bottleneck question needs the same arm with belief and with oracle frames in one run, on the same
/// jobs, and a belief − oracle delta per metric.
/// </summary>
public sealed class OracleComparisonTests
{
    [Fact]
    public void An_oracle_suffix_makes_an_oracle_arm_next_to_the_belief_one()
    {
        CliOptions options = CliOptions.Parse(["run", "--arms", "selector,selector-oracle"]);

        Assert.Equal(["selector", "selector-oracle"], options.ArmSpecs().Select(static a => a.ToString()));
        Assert.Equal([false, true], options.ArmSpecs().Select(static a => a.Oracle));
        Assert.All(options.ArmSpecs(), static a => Assert.Equal("selector", a.Name));
    }

    [Fact]
    public void Oracle_both_runs_every_arm_twice()
    {
        CliOptions options = CliOptions.Parse(["run", "--arms", "selector,bandit", "--oracle", "both"]);

        Assert.Equal(["selector", "selector-oracle", "bandit", "bandit-oracle"], options.ArmSpecs().Select(static a => a.ToString()));
    }

    [Fact]
    public void The_legacy_oracle_flag_still_makes_every_arm_oracle()
    {
        CliOptions options = CliOptions.Parse(["run", "--arms", "selector", "--oracle"]);

        Assert.Equal(["selector-oracle"], options.ArmSpecs().Select(static a => a.ToString()));
    }

    [Fact]
    public void Report_shows_belief_minus_oracle_deltas_on_the_same_jobs()
    {
        static PlayerMatchMetrics P(int lost) => new(
            Faction.Allied, 0, 0, 0, [], 0, 0, 0, new Dictionary<string, int>(), 0, 0, 0, 0, 0.1, 1000, 500, lost, 0, 0, null, 0, 5000, 10, 3, 2000, null, []);
        static MatchRecord M(string arm, int seed, int? winner, int lost) =>
            new(arm, "live-rush", "twin-valley", "training", seed, winner, "elimination", 300,
                new Dictionary<string, PlayerMatchMetrics> { ["arm"] = P(lost), ["opponent"] = P(0) });
        List<MatchRecord> matches = [.. Enumerable.Range(1, 6).SelectMany(s => new[] { M("selector", s, s % 2, 3000), M("selector-oracle", s, 0, 1000) })];

        string report = ReportBuilder.Build(matches, [], [], CliOptions.Parse(["run", "--arms", "selector", "--oracle", "both"]), "test");

        Assert.Contains("## Perception bottleneck (belief − oracle)", report, StringComparison.Ordinal);
        Assert.Contains("### selector vs selector-oracle (6 pairs)", report, StringComparison.Ordinal);
        // Value lost: belief 3000 against oracle 1000 on every pair.
        Assert.Contains("| value lost | 6 | 1000 | 3000 | 2000 [2000, 2000] | 0 / 6 / 0 |", report, StringComparison.Ordinal);
    }
}
