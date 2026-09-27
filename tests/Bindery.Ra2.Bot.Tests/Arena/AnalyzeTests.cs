// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arena;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary><c>arena analyze</c> on a real match log, and shadow agreement in the run report.</summary>
public sealed class AnalyzeTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), $"bindery-analyze-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Analyze_builds_the_report_and_an_optional_fake_narrative_from_a_match_log()
    {
        Assert.Equal(0, Program.Main(["run", "--arms", "llm-shadow", "--maps", "training", "--opponents", "live-rush", "--seeds", "1",
            "--benchmark", "contested", "--max-seconds", "300", "--llm-fake", "--out", dir]));
        string log = Directory.GetFiles(Path.Combine(dir, "decisions"), "*.ndjson").OrderBy(static f => f, StringComparer.Ordinal).First();

        string report = Program.Analyze(log, narrate: true, llmFake: true);

        Assert.Contains("# Post-game report: llm-shadow vs live-rush", report, StringComparison.Ordinal);
        Assert.Contains("## Intent timeline", report, StringComparison.Ordinal);
        Assert.Contains("evidence:", report, StringComparison.Ordinal); // the selector's rationale
        Assert.Contains("Same playbook:", report, StringComparison.Ordinal);
        Assert.Contains("## Narrative", report, StringComparison.Ordinal);
        Assert.Contains("Fake client narrative, scripted from the report, not a model.", report, StringComparison.Ordinal);
        Assert.Equal(report, Program.Analyze(log, narrate: true, llmFake: true));

        string output = Path.Combine(dir, "analysis.md");
        Assert.Equal(0, Program.Main(["analyze", log, "--out", output]));
        Assert.StartsWith("# Post-game report", File.ReadAllText(output), StringComparison.Ordinal);
    }

    [Fact]
    public void The_run_report_shows_shadow_agreement()
    {
        static PlayerMatchMetrics P(int shadows, int compared, int agreed) => new(
            Faction.Allied, 10, 0, 0, [], 1, 0, 0, new Dictionary<string, int>(), 0, 0, shadows, 0, 0.1, 1000, 500, 500, 0, 0, null, 0, 5000, 10, 3, 2000, null, [])
        {
            ShadowCompared = compared,
            ShadowAgreed = agreed,
        };
        static MatchRecord M(int seed, PlayerMatchMetrics p) =>
            new("llm-shadow", "live-rush", "twin-valley", "training", seed, 0, "elimination", 300, new Dictionary<string, PlayerMatchMetrics> { ["arm"] = p, ["opponent"] = p });

        string report = ReportBuilder.Build([M(1, P(10, 8, 6)), M(2, P(10, 8, 2))], [], [], CliOptions.Parse(["run", "--arms", "llm-shadow"]), "test");

        Assert.Contains("| 20 | 0/20 (0.000) | n/a | 8/16 (0.500) |", report, StringComparison.Ordinal);
    }
}
