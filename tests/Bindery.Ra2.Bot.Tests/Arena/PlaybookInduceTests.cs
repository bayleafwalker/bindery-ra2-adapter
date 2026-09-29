// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary>Synthetic match manifests and decision logs for the inducer and the pinned arm.</summary>
internal static class InduceFixtures
{
    public const string Arm = "llm-t1";

    public static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"bindery-induce-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// One match: <paramref name="proposals"/> Primary LLM proposals of <paramref name="playbook"/> (parameter
    /// <paramref name="parameter"/> = <paramref name="value"/>), and, when <paramref name="launchSeconds"/> is given,
    /// one launch note at that game time. Even seeds are Soviet, odd Allied (no match record in the manifest).
    /// </summary>
    public static void Match(string dir, string stem, int seed, string split, int? winner, string playbook, int proposals, string parameter, double value,
        double? launchSeconds, int launchArmy = 0, string arm = Arm, string? extraLine = null)
    {
        Directory.CreateDirectory(dir);
        StringBuilder log = new();
        for (int i = 0; i < proposals; i++)
        {
            log.AppendLine(Proposal(i * 900, "Primary", "Llm", playbook, new Dictionary<string, double> { [parameter] = value }));
        }
        // Noise the inducer must ignore: a fallback proposal and a selector-sourced primary one.
        log.AppendLine(Proposal(1, "Fallback", "Selector", "generic-defend", []));
        log.AppendLine(Proposal(2, "Primary", "Selector", "generic-defend", []));
        // Distinct per match: the inducer drops byte-identical logs as one match.
        log.AppendLine(JsonSerializer.Serialize(new { kind = "match.marker", frame = 0, data = new { stem } }));
        log.AppendLine(Plan(5, "squads: staging at r2 (army 0/1500, conditions not met, force ratio 99.00/1.20)"));
        if (launchSeconds is { } seconds)
        {
            long frame = (long)Math.Round(seconds * 15);
            log.AppendLine(Plan(frame, string.Create(CultureInfo.InvariantCulture, $"squads: attacking r8 with army value {launchArmy} (force ratio 2.00/1.20, w 0.00, upper 2400, baseAge 84s)")));
            log.AppendLine(Plan(frame + 100, "squads: attacking r8 with army value 99999"));
        }
        if (extraLine is not null) log.AppendLine(extraLine);
        File.WriteAllText(Path.Combine(dir, stem + ".ndjson"), log.ToString());

        MatchManifest manifest = new(MatchManifest.CurrentSchema, new ArmSpec(arm, false, false), "ai-rush", "twin-valley", split, seed, 1200, BenchmarkSettings.Standard,
            null, null, null, winner, "elimination", 600);
        File.WriteAllText(Path.Combine(dir, stem + ".match.json"), manifest.ToJson());
    }

    /// <summary>
    /// Six won Soviet matches of soviet-rhino-rush (launches at 300..400 s with army 1000..2000, proposals of
    /// attackArmyValue 900..1400, six each: 36 supporting), a lost one that must not count, an Allied cluster below the
    /// threshold, and a training-only layout unless <paramref name="heldout"/> is set.
    /// </summary>
    public static string Standard(string root, string split = "training", string sub = "decisions")
    {
        string dir = Path.Combine(root, sub);
        for (int i = 0; i < 6; i++)
        {
            Match(dir, $"m-soviet-{i}", 2 + 2 * i, split, 0, "soviet-rhino-rush", 6, "attackArmyValue", 900 + 100 * i, 300 + 20 * i, 1000 + 200 * i);
        }
        Match(dir, "m-soviet-lost", 20, split, 1, "soviet-rhino-rush", 6, "attackArmyValue", 100, 10, 50);
        Match(dir, "m-allied-0", 1, split, 0, "allied-grizzly-timing", 5, "attackArmyValue", 1500, 200, 1500);
        Match(dir, "m-other-arm", 22, split, 0, "soviet-rhino-rush", 40, "attackArmyValue", 100, 10, 50, arm: "selector");
        return dir;
    }

    private static string Proposal(long frame, string role, string source, string playbook, Dictionary<string, double> parameters) =>
        JsonSerializer.Serialize(new { kind = "strategy.proposal", frame, data = new { role, intent = new { source, playbookId = playbook, playbookParameters = parameters } } });

    private static string Plan(long frame, string note) =>
        JsonSerializer.Serialize(new { kind = "operations.plan", frame, data = new { notes = new[] { note } } });

    public static PlaybookLibrary Library() => PlaybookLibrary.LoadDefault();
}

/// <summary><c>arena induce</c>: clustering, thresholds, medians, phases, determinism, split handling and loading.</summary>
[Collection("ConsoleRedirect")]
public sealed class PlaybookInduceTests : IDisposable
{
    private readonly string root = InduceFixtures.NewRoot();

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private InductionResult Induce(string dir, string split = "training", int minSupport = 30) =>
        PlaybookInducer.Induce([dir], InduceFixtures.Arm, split, minSupport, InduceFixtures.Library());

    [Fact]
    public void One_playbook_per_cluster_at_or_above_the_support_threshold_from_won_matches_of_the_arm()
    {
        string dir = InduceFixtures.Standard(root);
        InductionResult result = Induce(dir);

        // 36 Soviet proposals in won matches; the Allied cluster has 5, the lost match and the other arm do not count.
        Playbook induced = Assert.Single(result.Playbooks);
        Assert.StartsWith("induced-soviet-rhino-rush-soviet-", induced.Id);
        Assert.Equal([Faction.Soviet], induced.Factions);
        Assert.Contains("36 proposals in 6 won matches", induced.Description);
        Assert.Contains("allied-grizzly-timing", result.Report);
        Assert.Contains("5 supporting proposals in 1 won matches, below 30", result.Report);

        // A lower threshold admits the Allied cluster as its own playbook, named for its faction.
        InductionResult both = Induce(dir, minSupport: 5);
        Assert.Equal(2, both.Playbooks.Count);
        Assert.Contains(both.Playbooks, static p => p.Id.StartsWith("induced-allied-grizzly-timing-allied-", StringComparison.Ordinal) && p.Factions.SequenceEqual([Faction.Allied]));

        // Above the threshold nothing is induced, and the report says so.
        InductionResult none = Induce(dir, minSupport: 37);
        Assert.Empty(none.Playbooks);
        Assert.Contains("No cluster reached the threshold", none.Report);
    }

    [Fact]
    public void The_base_fields_are_kept_and_parameters_get_the_proposed_medians()
    {
        Playbook basis = InduceFixtures.Library().All.Single(static p => p.Id == "soviet-rhino-rush");
        Playbook induced = Assert.Single(Induce(InduceFixtures.Standard(root)).Playbooks);

        Assert.Equal(basis.Posture, induced.Posture);
        Assert.Equal(basis.Budget, induced.Budget);
        Assert.Equal(JsonSerializer.Serialize(basis.Composition, BotJson.Options), JsonSerializer.Serialize(induced.Composition, BotJson.Options));
        Assert.Equal(basis.TechGoals, induced.TechGoals);
        Assert.Equal(basis.MinCommitSeconds, induced.MinCommitSeconds);

        // Six matches x six proposals of 900, 1000, ..., 1400: median 1150. The range stays the base's.
        PlaybookParameter parameter = Assert.Single(induced.Parameters);
        PlaybookParameter original = basis.Parameters.Single();
        Assert.Equal(original.Name, parameter.Name);
        Assert.Equal(1150, parameter.Default);
        Assert.Equal(original.Min, parameter.Min);
        Assert.Equal(original.Max, parameter.Max);
    }

    [Fact]
    public void A_median_outside_the_base_range_is_clamped()
    {
        string dir = Path.Combine(root, "decisions");
        for (int i = 0; i < 5; i++) InduceFixtures.Match(dir, $"hi-{i}", 2 + 2 * i, "training", 0, "soviet-rhino-rush", 7, "attackArmyValue", 50000, 300, 1500);
        Playbook basis = InduceFixtures.Library().All.Single(static p => p.Id == "soviet-rhino-rush");

        Playbook induced = Assert.Single(Induce(dir).Playbooks);
        Assert.Equal(basis.Parameters.Single().Max, induced.Parameters.Single().Default);
        Assert.Contains("clamped", Induce(dir).Report);
    }

    [Fact]
    public void The_attack_phase_needs_the_median_launch_army_and_the_first_quartile_launch_time()
    {
        Playbook induced = Assert.Single(Induce(InduceFixtures.Standard(root)).Playbooks);

        // Launch armies 1000..2000 (median 1500); launch times 300..400 s (25th percentile 325 s).
        Assert.NotNull(induced.Phases);
        Assert.Equal(["build", "attack"], induced.Phases.Select(static p => p.Name));
        Assert.Empty(induced.Phases[0].EnterWhen);
        Assert.Equal(
            [new Condition(ConditionMetric.OwnArmyValue, Comparison.Ge, 1500), new Condition(ConditionMetric.GameSeconds, Comparison.Ge, 325)],
            induced.Phases[1].EnterWhen);

        // The base's attack conditions with the OwnArmyValue bound at the median (the base's was 1000).
        Assert.Equal([new Condition(ConditionMetric.OwnArmyValue, Comparison.Ge, 1500)], induced.Phases[1].AttackConditions);
    }

    [Fact]
    public void A_base_without_an_army_bound_gets_one_in_the_attack_phase()
    {
        string dir = Path.Combine(root, "decisions");
        for (int i = 0; i < 4; i++) InduceFixtures.Match(dir, $"ex-{i}", 2 + 2 * i, "training", 0, "generic-expand", 9, "expandAtSeconds", 60, 400 + 10 * i, 3000);
        Playbook basis = InduceFixtures.Library().All.Single(static p => p.Id == "generic-expand");

        Playbook induced = Assert.Single(Induce(dir).Playbooks);
        Assert.DoesNotContain(basis.AttackConditions, static c => c.Metric == ConditionMetric.OwnArmyValue);
        Assert.Equal([.. basis.AttackConditions, new Condition(ConditionMetric.OwnArmyValue, Comparison.Ge, 3000)], induced.Phases![1].AttackConditions);
    }

    [Fact]
    public void A_cluster_whose_won_matches_never_launched_is_reported_not_induced()
    {
        string dir = Path.Combine(root, "decisions");
        for (int i = 0; i < 4; i++) InduceFixtures.Match(dir, $"nl-{i}", 2 + 2 * i, "training", 0, "soviet-rhino-rush", 9, "attackArmyValue", 1000, null);

        InductionResult result = Induce(dir);
        Assert.Empty(result.Playbooks);
        Assert.Contains("none of its 4 won matches launched an attack", result.Report);
    }

    [Fact]
    public void The_same_inputs_give_byte_identical_output_whatever_their_location_or_order()
    {
        string a = InduceFixtures.Standard(root, sub: "a");
        string b = InduceFixtures.Standard(Path.Combine(root, "elsewhere"), sub: "b");

        InductionResult first = Induce(a);
        InductionResult second = Induce(a);
        Assert.Equal(first.Json, second.Json);
        Assert.Equal(first.Report, second.Report);

        // Another directory with the same logs, and both directories in the other order: the same bytes (duplicates dropped).
        Assert.Equal(first.Json, Induce(b).Json);
        Assert.Equal(first.Json, PlaybookInducer.Induce([b, a], InduceFixtures.Arm, "training", 30, InduceFixtures.Library()).Json);
        Assert.Equal(first.Json, PlaybookInducer.Induce([a, b], InduceFixtures.Arm, "training", 30, InduceFixtures.Library()).Json);
    }

    [Fact]
    public void Different_inputs_give_a_different_id()
    {
        string dir = InduceFixtures.Standard(root);
        string id = Assert.Single(Induce(dir).Playbooks).Id;
        InduceFixtures.Match(dir, "extra", 40, "training", 0, "soviet-rhino-rush", 6, "attackArmyValue", 1000, 350, 1500);
        Assert.NotEqual(id, Assert.Single(Induce(dir).Playbooks).Id);
    }

    [Fact]
    public void The_split_filter_selects_matches_and_held_out_data_warns()
    {
        string dir = Path.Combine(root, "decisions");
        for (int i = 0; i < 6; i++) InduceFixtures.Match(dir, $"t-{i}", 2 + 2 * i, "training", 0, "soviet-rhino-rush", 6, "attackArmyValue", 1000, 300, 1500);
        for (int i = 0; i < 6; i++) InduceFixtures.Match(dir, $"h-{i}", 30 + 2 * i, "heldout", 0, "soviet-rhino-rush", 4, "attackArmyValue", 1000, 300, 1500);

        InductionResult training = Induce(dir);
        Assert.Contains("36 proposals in 6 won matches", Assert.Single(training.Playbooks).Description);
        Assert.Empty(training.Warnings);

        // 24 held-out proposals are under the threshold.
        InductionResult heldout = Induce(dir, "heldout");
        Assert.Empty(heldout.Playbooks);
        Assert.Contains("held-out", Assert.Single(heldout.Warnings));
        Assert.Contains("must not be tested on the data", heldout.Warnings[0]);

        InductionResult all = Induce(dir, "all");
        Assert.Contains("60 proposals in 12 won matches", Assert.Single(all.Playbooks).Description);
        Assert.Single(all.Warnings);
    }

    [Fact]
    public void The_command_writes_the_file_and_report_and_the_split_defaults_to_training()
    {
        string dir = InduceFixtures.Standard(root);
        string outFile = Path.Combine(root, "out.json"), report = Path.Combine(root, "report.md");
        Assert.Equal(0, Program.Main(["induce", "--from", dir, "--arm", InduceFixtures.Arm, "--out", outFile, "--report", report]));
        Assert.Equal(Induce(dir).Json, File.ReadAllText(outFile));
        string text = File.ReadAllText(report);
        Assert.Contains("## `induced-soviet-rhino-rush-soviet-", text);
        Assert.Contains("median 1150", text);
        Assert.Contains("SHA-256", text);
        Assert.Contains("--split", CliOptions.Usage);
        Assert.Contains("arena induce", CliOptions.Usage);

        Assert.Equal("training", InduceOptions.Parse(["induce", "--from", dir, "--arm", "x", "--out", outFile]).Split);
        Assert.Equal(30, InduceOptions.Parse(["induce", "--from", dir, "--arm", "x", "--out", outFile]).MinSupport);
        Assert.Equal(1, Program.Main(["induce", "--from", dir, "--arm", "x"]));
        Assert.Equal(1, Program.Main(["induce", "--from", dir, "--arm", "x", "--out", outFile, "--split", "validation"]));
    }

    [Fact]
    public void The_output_loads_as_a_playbook_document_and_with_the_playbooks_flag()
    {
        InductionResult result = Induce(InduceFixtures.Standard(root));
        PlaybookLibrary loaded = PlaybookLibrary.LoadJson(result.Json);
        Playbook induced = Assert.Single(loaded.All);
        Assert.Equal(JsonSerializer.Serialize(Assert.Single(result.Playbooks), BotJson.Options), JsonSerializer.Serialize(induced, BotJson.Options));

        string file = Path.Combine(root, "induced.json");
        File.WriteAllText(file, result.Json);
        (_, IPlaybookLibrary merged, _) = Program.LoadRules(null, [file]);
        Assert.Equal(13, merged.All.Count);
        Assert.True(merged.TryGet(induced.Id, out _));
        Assert.Equal(CliOptions.Parse(["run", "--playbooks", file]).PlaybookFiles, [file]);
    }
}
