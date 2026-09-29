// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Runtime;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary>
/// <c>arena run --interleave</c> (cell-major job order across arms) and <c>--resume</c> (finished matches under
/// <c>&lt;out&gt;/decisions/</c> are loaded, not replayed). Deterministic arms (selector, bandit), one opponent, three
/// training maps, two seeds, short matches.
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class InterleaveResumeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bindery-resume-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static string[] Args(string arms, string out_, params string[] extra) =>
        ["run", "--arms", arms, "--maps", "training", "--opponents", "ai-rush", "--seeds", "2", "--max-seconds", "60", "--out", out_, .. extra];

    private string Run(string name, string arms, params string[] extra)
    {
        string dir = Path.Combine(root, name);
        Assert.Equal(0, Program.Main(Args(arms, dir, extra)));
        return dir;
    }

    private static string Decisions(string dir) => Path.Combine(dir, "decisions");

    private static string Manifest(string dir, string stem) => Path.Combine(Decisions(dir), stem + ".match.json");

    [Fact]
    public void Cell_major_order_schedules_every_arms_match_for_a_cell_together()
    {
        List<(int Arm, int Job)> order = ArenaScheduling.CellMajor<string>([["a1", "a2", "a3"], ["b1", "b2", "b3"], ["c1", "c2", "c3"]]);

        Assert.Equal([(0, 0), (1, 0), (2, 0), (0, 1), (1, 1), (2, 1), (0, 2), (1, 2), (2, 2)], order);
        Assert.Throws<ArgumentException>(() => ArenaScheduling.CellMajor<string>([["a1"], ["b1", "b2"]]));
    }

    [Fact]
    public void The_flags_parse_and_appear_in_the_usage()
    {
        CliOptions plain = CliOptions.Parse(["run"]);
        CliOptions flagged = CliOptions.Parse(["run", "--interleave", "--resume"]);

        Assert.False(plain.Interleave);
        Assert.False(plain.Resume);
        Assert.True(flagged.Interleave);
        Assert.True(flagged.Resume);
        Assert.Contains("--interleave", CliOptions.Usage);
        Assert.Contains("--resume", CliOptions.Usage);
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["run", "--resume", "--no-decisions"]));
    }

    [Fact]
    public void An_interleaved_run_reports_the_same_as_the_default_per_arm_run()
    {
        string perArm = Run("per-arm", "selector,bandit");
        string interleaved = Run("interleaved", "selector,bandit", "--interleave");

        Assert.Equal(File.ReadAllText(Path.Combine(perArm, "results.json")), File.ReadAllText(Path.Combine(interleaved, "results.json")));
        Assert.Equal(File.ReadAllText(Path.Combine(perArm, "report.md")), File.ReadAllText(Path.Combine(interleaved, "report.md")));
    }

    [Fact]
    public void A_match_record_is_on_disk_for_every_finished_match()
    {
        string dir = Run("flushed", "selector");

        string[] manifests = Directory.GetFiles(Decisions(dir), "*.match.json");
        Assert.Equal(6, manifests.Length);
        Assert.Equal(6, Directory.GetFiles(Decisions(dir), "*.ndjson").Length);
        List<MatchRecord> results = JsonSerializer.Deserialize<List<MatchRecord>>(File.ReadAllText(Path.Combine(dir, "results.json")), BotJson.Options)!;
        foreach (string path in manifests)
        {
            MatchManifest manifest = MatchManifest.Load(path);
            Assert.Contains(results, r => r.Arm == manifest.Record!.Arm && r.Map == manifest.Record.Map && r.Seed == manifest.Record.Seed && r.Reason == manifest.Record.Reason && r.DurationSeconds == manifest.Record.DurationSeconds);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resume_loads_recorded_matches_runs_only_the_missing_one_and_reproduces_the_report(bool interleave)
    {
        string[] extra = interleave ? ["--interleave"] : [];
        string fresh = Run("fresh", "selector,bandit", extra);

        string resumed = Path.Combine(root, "resumed");
        CopyDirectory(fresh, resumed);
        // A sentinel in a loaded record shows up in the output only if that record was loaded rather than replayed.
        string sentinelStem = "selector_ai-rush_twin-valley_1";
        MatchManifest tampered = MatchManifest.Load(Manifest(resumed, sentinelStem));
        File.WriteAllText(Manifest(resumed, sentinelStem), (tampered with { Record = tampered.Record! with { Reason = "loaded-not-replayed" } }).ToJson());
        // Missing records (the interrupted run's tail) are the only matches that run.
        string missingStem = "selector_ai-rush_river-crossing_2";
        File.Delete(Manifest(resumed, missingStem));
        Thread.Sleep(100);
        DateTime before = DateTime.UtcNow;

        Assert.Equal(0, Program.Main(Args("selector,bandit", resumed, ["--resume", .. extra])));

        string results = File.ReadAllText(Path.Combine(resumed, "results.json"));
        Assert.Contains("loaded-not-replayed", results);
        Assert.Equal(6 + 6, JsonSerializer.Deserialize<List<MatchRecord>>(results, BotJson.Options)!.Count);
        // Only the deleted match was played again (the sentinel one and the bandit's were loaded, so left untouched).
        string[] rewritten = [.. Directory.GetFiles(Decisions(resumed), "*.match.json").Where(f => File.GetLastWriteTimeUtc(f) >= before)];
        Assert.Equal([Manifest(resumed, missingStem)], rewritten);

        // Same structure and the same report: undo the sentinel and compare with the fresh run.
        File.WriteAllText(Manifest(resumed, sentinelStem), File.ReadAllText(Manifest(fresh, sentinelStem)));
        Assert.Equal(0, Program.Main(Args("selector,bandit", resumed, ["--resume", .. extra])));
        Assert.Equal(File.ReadAllText(Path.Combine(fresh, "results.json")), File.ReadAllText(Path.Combine(resumed, "results.json")));
        Assert.Equal(File.ReadAllText(Path.Combine(fresh, "report.md")), File.ReadAllText(Path.Combine(resumed, "report.md")));
        Assert.Equal(File.ReadAllText(Path.Combine(fresh, "probes.json")), File.ReadAllText(Path.Combine(resumed, "probes.json")));
    }

    [Fact]
    public void Resume_of_a_complete_run_plays_nothing()
    {
        string dir = Run("complete", "selector");
        DateTime[] stamps = [.. Directory.GetFiles(Decisions(dir)).Select(static f => File.GetLastWriteTimeUtc(f))];
        string results = File.ReadAllText(Path.Combine(dir, "results.json"));

        Assert.Equal(0, Program.Main(Args("selector", dir, "--resume")));

        Assert.Equal(stamps, Directory.GetFiles(Decisions(dir)).Select(static f => File.GetLastWriteTimeUtc(f)));
        Assert.Equal(results, File.ReadAllText(Path.Combine(dir, "results.json")));
    }

    [Theory]
    [InlineData("seed")]
    [InlineData("map")]
    [InlineData("split")]
    [InlineData("opponent")]
    [InlineData("arm")]
    [InlineData("benchmark")]
    public void Resume_refuses_a_record_that_does_not_match_its_job(string field)
    {
        string dir = Run("mismatch-" + field, "selector");
        string stem = "selector_ai-rush_twin-valley_1";
        MatchManifest manifest = MatchManifest.Load(Manifest(dir, stem));
        MatchManifest bad = field switch
        {
            "seed" => manifest with { Seed = 9 },
            "map" => manifest with { Map = "other-map" },
            "split" => manifest with { Split = "heldout" },
            "opponent" => manifest with { Opponent = "ai-armor" },
            "arm" => manifest with { Arm = new ArmSpec("bandit", false, false) },
            _ => manifest with { Benchmark = BenchmarkSettings.Preset("contested") },
        };
        File.WriteAllText(Manifest(dir, stem), bad.ToJson());

        (int code, string error) = RunCapturingError(Args("selector", dir, "--resume"));

        Assert.Equal(1, code);
        Assert.Contains("--resume", error);
        Assert.Contains(field == "arm" ? "arm" : field, error);
        Assert.Contains("twin-valley", error);
    }

    [Fact]
    public void Resume_refuses_a_different_max_seconds_and_a_manifest_without_a_record()
    {
        string dir = Run("maxsec", "selector");

        (int code, string error) = RunCapturingError(["run", "--arms", "selector", "--maps", "training", "--opponents", "ai-rush", "--seeds", "2", "--max-seconds", "30", "--out", dir, "--resume"]);
        Assert.Equal(1, code);
        Assert.Contains("maxSeconds recorded 60", error);

        string stem = "selector_ai-rush_twin-valley_1";
        MatchManifest manifest = MatchManifest.Load(Manifest(dir, stem));
        File.WriteAllText(Manifest(dir, stem), (manifest with { Record = null }).ToJson());
        (code, error) = RunCapturingError(Args("selector", dir, "--resume"));
        Assert.Equal(1, code);
        Assert.Contains("no match record", error);
    }

    [Fact]
    public void Resume_refuses_a_log_that_no_longer_hashes_to_its_record()
    {
        string dir = Run("hash", "selector");
        string log = Path.Combine(Decisions(dir), "selector_ai-rush_twin-valley_1.ndjson");
        string[] lines = File.ReadAllLines(log);
        File.WriteAllLines(log, lines.Take(lines.Length - 1));

        (int code, string error) = RunCapturingError(Args("selector", dir, "--resume"));

        Assert.Equal(1, code);
        Assert.Contains("hash", error);
    }

    [Fact]
    public void A_partly_recorded_bandit_arm_cannot_be_resumed()
    {
        string dir = Run("bandit-partial", "bandit");
        File.Delete(Manifest(dir, "bandit_ai-rush_twin-valley_2"));

        (int code, string error) = RunCapturingError(Args("bandit", dir, "--resume"));

        Assert.Equal(1, code);
        Assert.Contains("bandit", error);
    }

    private static (int Code, string Error) RunCapturingError(string[] args)
    {
        TextWriter original = Console.Error;
        StringWriter captured = new();
        Console.SetError(captured);
        try
        {
            return (Program.Main(args), captured.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(dir.Replace(from, to, StringComparison.Ordinal));
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(file, file.Replace(from, to, StringComparison.Ordinal));
    }
}
