// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arena;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary><c>arena run --seed-list</c>: play exactly the listed seeds (e.g. the even, Soviet ones) instead of 1..N.</summary>
[Collection("ConsoleRedirect")]
public sealed class SeedListTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bindery-seedlist-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void The_flag_parses_rejects_bad_lists_and_appears_in_the_usage()
    {
        Assert.Null(CliOptions.Parse(["run"]).SeedList);
        Assert.Equal([2, 4], CliOptions.Parse(["run", "--seed-list", "2, 4"]).SeedList);
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["run", "--seed-list", "2,2"]));
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["run", "--seed-list", "0"]));
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["run", "--seed-list", ","]));
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["run", "--seed-list", "2,a"]));
        Assert.Contains("--seed-list", CliOptions.Usage);
    }

    [Fact]
    public void Only_the_listed_seeds_are_played()
    {
        string dir = Path.Combine(root, "run");
        Assert.Equal(0, Program.Main(["run", "--arms", "selector", "--maps", "training", "--opponents", "ai-rush", "--seeds", "4",
            "--seed-list", "2,4", "--max-seconds", "60", "--out", dir]));

        string[] seeds = [.. Directory.GetFiles(Path.Combine(dir, "decisions"), "*.match.json")
            .Select(static f => Path.GetFileName(f).Split('_')[^1].Split('.')[0]).Distinct().Order()];
        Assert.Equal(["2", "4"], seeds);
    }

    [Fact]
    public void Resume_refuses_a_different_seed_list_and_accepts_the_same_one()
    {
        string dir = Path.Combine(root, "resume");
        string[] Args(params string[] extra) => ["run", "--arms", "selector", "--maps", "training", "--opponents", "ai-rush", "--seeds", "4",
            "--max-seconds", "60", "--out", dir, .. extra];
        Assert.Equal(0, Program.Main(Args("--seed-list", "2,4")));

        Assert.Equal(0, Program.Main(Args("--seed-list", "2,4", "--resume")));
        Assert.NotEqual(0, Program.Main(Args("--seed-list", "4,2", "--resume")));
    }
}
