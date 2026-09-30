// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Arbitration;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Tests.Arbitration;
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
        double? launchSeconds, int launchArmy = 0, string arm = Arm, string? extraLine = null, string opponent = "ai-rush")
    {
        Directory.CreateDirectory(dir);
        StringBuilder log = new();
        // Noise (logged first, so the Primary LLM activations below are the ones in force) the inducer must ignore: a fallback proposal and a selector-sourced primary one.
        log.AppendLine(Proposal(1, "Fallback", "Selector", "generic-defend", []));
        log.AppendLine(Proposal(2, "Primary", "Selector", "generic-defend", []));
        for (int i = 0; i < proposals; i++)
        {
            log.AppendLine(Proposal(i * 900, "Primary", "Llm", playbook, new Dictionary<string, double> { [parameter] = value }, $"{stem}-{i}"));
        }
        // Distinct per match: the inducer drops byte-identical logs as one match.
        log.AppendLine(JsonSerializer.Serialize(new { kind = "match.marker", frame = 0, data = new { stem } }));
        string? last = proposals > 0 ? $"{stem}-{proposals - 1}" : null;
        log.AppendLine(Plan(5, "squads: staging at r2 (army 0/1500, conditions not met, force ratio 99.00/1.20)", last));
        if (launchSeconds is { } seconds)
        {
            long frame = (long)Math.Round(seconds * 15);
            log.AppendLine(Plan(frame, string.Create(CultureInfo.InvariantCulture, $"squads: attacking r8 with army value {launchArmy} (force ratio 2.00/1.20, w 0.00, upper 2400, baseAge 84s)"), last));
            log.AppendLine(Plan(frame + 100, "squads: attacking r8 with army value 99999", last));
        }
        if (extraLine is not null) log.AppendLine(extraLine);
        File.WriteAllText(Path.Combine(dir, stem + ".ndjson"), log.ToString());

        WriteManifest(dir, stem, seed, split, winner, arm, opponent);
    }

    /// <summary>A won training match with exactly the given log lines (plus a distinguishing marker).</summary>
    public static void Custom(string dir, string stem, int seed, params string[] lines)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, stem + ".ndjson"),
            string.Join('\n', lines.Append(JsonSerializer.Serialize(new { kind = "match.marker", frame = 0, data = new { stem } }))) + "\n");
        WriteManifest(dir, stem, seed, "training", 0, Arm, "ai-rush");
    }

    private static void WriteManifest(string dir, string stem, int seed, string split, int? winner, string arm, string opponent)
    {
        MatchManifest manifest = new(MatchManifest.CurrentSchema, new ArmSpec(arm, false, false), opponent, "twin-valley", split, seed, 1200, BenchmarkSettings.Standard,
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

    /// <summary>The arbiter's record of adopting a proposal (what the inducer counts).</summary>
    public static string Proposal(long frame, string role, string source, string playbook, Dictionary<string, double> parameters, string? intentId = null) =>
        JsonSerializer.Serialize(new { kind = "strategy.intent_activated", frame, data = new { role, intentId, renewal = false, intent = new { intentId, source, playbookId = playbook, playbookParameters = parameters } } });

    /// <summary>A validated proposal the arbiter refused (no activation follows).</summary>
    public static string Refused(string intentId) =>
        JsonSerializer.Serialize(new { kind = "strategy.validation", frame = 1, data = new { role = "Primary", intentId, accepted = true, arbitration = "Refused", arbitrationReason = "hysteresis" } });

    /// <summary>The scheduler's record of a proposal its validator rejected.</summary>
    public static string Rejected(string intentId) =>
        JsonSerializer.Serialize(new { kind = "strategy.validation", frame = 1, data = new { role = "Primary", intentId, accepted = false } });

    /// <summary>The scheduler's record of a proposal discarded as late.</summary>
    public static string Discarded(string intentId) =>
        JsonSerializer.Serialize(new { kind = "strategy.late_discarded", frame = 1, data = new { role = "Primary", intentId, reason = "age" } });

    public static string Plan(long frame, string note, string? intentId = null) =>
        JsonSerializer.Serialize(new { kind = "operations.plan", frame, data = new { intentId, notes = new[] { note } } });

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

    private InductionResult Induce(string dir, string split = "training", int minSupport = 4) =>
        PlaybookInducer.Induce([dir], InduceFixtures.Arm, split, minSupport, InduceFixtures.Library());

    [Fact]
    public void One_playbook_per_cluster_at_or_above_the_support_threshold_from_won_matches_of_the_arm()
    {
        string dir = InduceFixtures.Standard(root);
        InductionResult result = Induce(dir);

        // 6 Soviet won matches (36 proposals); the Allied cluster has 1, the lost match and the other arm do not count.
        Playbook induced = Assert.Single(result.Playbooks);
        Assert.StartsWith("induced-soviet-rhino-rush-soviet-", induced.Id);
        Assert.Equal([Faction.Soviet], induced.Factions);
        Assert.Contains("36 proposals in 6 won matches", induced.Description);
        Assert.Contains("allied-grizzly-timing", result.Report);
        Assert.Contains("1 supporting won matches (5 proposals), below 4", result.Report);

        // A lower threshold admits the Allied cluster as its own playbook, named for its faction.
        InductionResult both = Induce(dir, minSupport: 1);
        Assert.Equal(2, both.Playbooks.Count);
        Assert.Contains(both.Playbooks, static p => p.Id.StartsWith("induced-allied-grizzly-timing-allied-", StringComparison.Ordinal) && p.Factions.SequenceEqual([Faction.Allied]));

        // Above the threshold nothing is induced, and the report says so.
        InductionResult none = Induce(dir, minSupport: 7);
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

        // The base's attack conditions with the OwnArmyValue bound at the median (the base's was 1000), plus the launch-time floor.
        Condition[] gate = [new Condition(ConditionMetric.OwnArmyValue, Comparison.Ge, 1500), new Condition(ConditionMetric.GameSeconds, Comparison.Ge, 325)];
        Assert.Equal(gate, induced.Phases[1].AttackConditions);
        Assert.Equal(gate, induced.AttackConditions);
    }

    [Fact]
    public void A_base_without_an_army_bound_gets_one_in_the_attack_phase()
    {
        string dir = Path.Combine(root, "decisions");
        for (int i = 0; i < 4; i++) InduceFixtures.Match(dir, $"ex-{i}", 2 + 2 * i, "training", 0, "generic-expand", 9, "expandAtSeconds", 60, 400 + 10 * i, 3000);
        Playbook basis = InduceFixtures.Library().All.Single(static p => p.Id == "generic-expand");

        Playbook induced = Assert.Single(Induce(dir).Playbooks);
        Assert.DoesNotContain(basis.AttackConditions, static c => c.Metric == ConditionMetric.OwnArmyValue);
        Assert.Equal([.. basis.AttackConditions, new Condition(ConditionMetric.OwnArmyValue, Comparison.Ge, 3000), new Condition(ConditionMetric.GameSeconds, Comparison.Ge, 407.5)], induced.Phases![1].AttackConditions);
    }

    [Fact]
    public void A_cluster_whose_won_matches_never_launched_is_reported_not_induced()
    {
        string dir = Path.Combine(root, "decisions");
        for (int i = 0; i < 4; i++) InduceFixtures.Match(dir, $"nl-{i}", 2 + 2 * i, "training", 0, "soviet-rhino-rush", 9, "attackArmyValue", 1000, null);

        InductionResult result = Induce(dir);
        Assert.Empty(result.Playbooks);
        Assert.Contains("4 supporting won matches, but none of them launched an attack", result.Report);
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
        Assert.Equal(first.Json, PlaybookInducer.Induce([b, a], InduceFixtures.Arm, "training", 4, InduceFixtures.Library()).Json);
        Assert.Equal(first.Json, PlaybookInducer.Induce([a, b], InduceFixtures.Arm, "training", 4, InduceFixtures.Library()).Json);
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
        for (int i = 0; i < 3; i++) InduceFixtures.Match(dir, $"h-{i}", 30 + 2 * i, "heldout", 0, "soviet-rhino-rush", 4, "attackArmyValue", 1000, 300, 1500);

        InductionResult training = Induce(dir);
        Assert.Contains("36 proposals in 6 won matches", Assert.Single(training.Playbooks).Description);
        Assert.Empty(training.Warnings);

        // Three held-out matches are under the threshold.
        InductionResult heldout = Induce(dir, "heldout");
        Assert.Empty(heldout.Playbooks);
        Assert.Contains("held-out", Assert.Single(heldout.Warnings));
        Assert.Contains("must not be tested on the data", heldout.Warnings[0]);

        InductionResult all = Induce(dir, "all");
        Assert.Contains("48 proposals in 9 won matches", Assert.Single(all.Playbooks).Description);
        Assert.Single(all.Warnings);
    }

    [Fact]
    public void Held_out_opponents_are_excluded_from_training_induction_and_belong_to_the_heldout_split()
    {
        string dir = Path.Combine(root, "decisions");
        for (int i = 0; i < 4; i++) InduceFixtures.Match(dir, $"t-{i}", 2 + 2 * i, "training", 0, "soviet-rhino-rush", 6, "attackArmyValue", 1000, 300, 1500);
        // Training map, held-out opponent (any difficulty), won by the LLM: must not leak into training.
        for (int i = 0; i < 4; i++) InduceFixtures.Match(dir, $"horde-{i}", 30 + 2 * i, "training", 0, "soviet-rhino-rush", 6, "attackArmyValue", 5000, 300, 1500, opponent: "ai-horde:hard");
        for (int i = 0; i < 4; i++) InduceFixtures.Match(dir, $"armor-{i}", 40 + 2 * i, "training", 0, "soviet-rhino-rush", 6, "attackArmyValue", 5000, 300, 1500, opponent: "ai-armor");

        InductionResult training = Induce(dir);
        Assert.Contains("24 proposals in 4 won matches", Assert.Single(training.Playbooks).Description);
        Assert.Equal(1000, Assert.Single(Assert.Single(training.Playbooks).Parameters).Default);

        // Held-out opponents on training maps count as held-out data.
        InductionResult heldout = Induce(dir, "heldout");
        Assert.Contains("48 proposals in 8 won matches", Assert.Single(heldout.Playbooks).Description);
        Assert.Single(heldout.Warnings);
    }

    [Fact]
    public void Support_counts_matches_once_and_ignores_rejected_and_late_discarded_proposals()
    {
        string dir = Path.Combine(root, "decisions");
        // One match with 40 proposals is one supporter, not 40.
        InduceFixtures.Match(dir, "one", 2, "training", 0, "soviet-rhino-rush", 40, "attackArmyValue", 1000, 300, 1500);
        Assert.Empty(Induce(dir, minSupport: 2).Playbooks);
        Assert.Contains("1 supporting won matches (40 proposals), below 2", Induce(dir, minSupport: 2).Report);

        // Three more matches: 10 proposals each, of which 0-4 are rejected and 5-6 late-discarded (3 count per match).
        string extra = string.Join('\n', Enumerable.Range(0, 5).Select(i => InduceFixtures.Rejected($"{{0}}-{i}")).Concat(Enumerable.Range(5, 2).Select(i => InduceFixtures.Discarded($"{{0}}-{i}"))));
        for (int i = 0; i < 3; i++) InduceFixtures.Match(dir, $"r-{i}", 4 + 2 * i, "training", 0, "soviet-rhino-rush", 10, "attackArmyValue", 1000, 300, 1500, extraLine: extra.Replace("{0}", $"r-{i}"));
        InductionResult result = Induce(dir, minSupport: 4);
        Assert.Contains("49 proposals in 4 won matches", Assert.Single(result.Playbooks).Description);
    }

    private static string Attack(long frame, int army, string intentId) => InduceFixtures.Plan(frame, $"squads: attacking r8 with army value {army} (force ratio 2.00/1.20)", intentId);

    private static string Stage(long frame, string intentId) => InduceFixtures.Plan(frame, "squads: staging at r2 (army 0/1500, conditions not met)", intentId);

    private static Dictionary<string, double> P(double v) => new() { ["attackArmyValue"] = v };

    [Fact]
    public void Each_supporting_match_weighs_the_same_in_the_parameter_medians()
    {
        string dir = Path.Combine(root, "decisions");
        // Two matches renewing 10 times at 2000 and two proposing once at 1000: per proposal the median is 2000, per match 1500.
        for (int m = 0; m < 4; m++)
        {
            bool heavy = m < 2;
            string[] lines = [.. Enumerable.Range(0, heavy ? 10 : 1).Select(i => InduceFixtures.Proposal(i * 10, "Primary", "Llm", "soviet-rhino-rush", P(heavy ? 2000 : 1000), $"w{m}-{i}")), Attack(5000, 1500, $"w{m}-{(heavy ? 9 : 0)}")];
            InduceFixtures.Custom(dir, $"w{m}", 2 + 2 * m, lines);
        }
        Playbook induced = Assert.Single(Induce(dir).Playbooks);
        Assert.Equal(1500, Assert.Single(induced.Parameters).Default);
        Assert.Contains("4 matches (22 proposals), per-match medians: median 1500", Induce(dir).Report);
    }

    [Fact]
    public void Proposals_the_arbiter_refused_do_not_count()
    {
        string dir = Path.Combine(root, "decisions");
        for (int m = 0; m < 4; m++)
        {
            // Validated and accepted, but arbitration refused: no activation follows, so no support and no launch credit.
            InduceFixtures.Custom(dir, $"a{m}", 2 + 2 * m,
                InduceFixtures.Proposal(0, "Primary", "Llm", "soviet-rhino-rush", P(1000), $"a{m}-0"),
                InduceFixtures.Proposal(10, "Primary", "Llm", "soviet-rhino-rush", P(5000), $"a{m}-1").Replace("strategy.intent_activated", "strategy.proposal"),
                InduceFixtures.Refused($"a{m}-1"),
                Attack(5000, 1500, $"a{m}-0"));
        }
        InductionResult result = Induce(dir);
        Playbook induced = Assert.Single(result.Playbooks);
        Assert.Equal(1000, Assert.Single(induced.Parameters).Default);
        Assert.Contains("4 proposals in 4 won matches", induced.Description);
    }

    [Fact]
    public void A_launch_is_credited_only_to_the_cluster_whose_playbook_was_active()
    {
        string dir = Path.Combine(root, "decisions");
        for (int m = 0; m < 4; m++)
        {
            // rhino-rush is adopted first and launches at 100 s with army 1000; soviet-turtle replaces it and launches at 300 s with army 3000.
            InduceFixtures.Custom(dir, $"b{m}", 2 + 2 * m,
                InduceFixtures.Proposal(0, "Primary", "Llm", "soviet-rhino-rush", P(1000), $"b{m}-0"),
                Attack(1500, 1000, $"b{m}-0"),
                Stage(1700, $"b{m}-0"),
                InduceFixtures.Proposal(2000, "Primary", "Llm", "soviet-turtle", [], $"b{m}-1"),
                Stage(2100, $"b{m}-1"),
                Attack(4500, 3000, $"b{m}-1"));
        }
        InductionResult result = Induce(dir);
        Playbook rush = Assert.Single(result.Playbooks, static p => p.Id.StartsWith("induced-soviet-rhino-rush-", StringComparison.Ordinal));
        Assert.Equal(1000, rush.AttackConditions.Single(static c => c.Metric == ConditionMetric.OwnArmyValue).Threshold);
        Assert.Equal(100, rush.AttackConditions.Single(static c => c.Metric == ConditionMetric.GameSeconds).Threshold);
        Playbook push = Assert.Single(result.Playbooks, static p => p.Id.StartsWith("induced-soviet-turtle-", StringComparison.Ordinal));
        Assert.Equal(3000, push.AttackConditions.Single(static c => c.Metric == ConditionMetric.OwnArmyValue).Threshold);
        Assert.Equal(300, push.AttackConditions.Single(static c => c.Metric == ConditionMetric.GameSeconds).Threshold);
    }

    [Fact]
    public void An_attack_that_continues_across_an_intent_switch_is_not_a_launch_of_the_new_playbook()
    {
        string dir = Path.Combine(root, "decisions");
        for (int m = 0; m < 4; m++)
        {
            // The attack starts under rhino-rush at 100 s and goes on (the note repeats each tick) while soviet-turtle is
            // adopted: turtle launches nothing. Later the attack stops and turtle starts another one (a rising edge) at 400 s.
            // The two-launch variant is m >= 2; matches 0-1 never launch again under turtle.
            List<string> lines =
            [
                InduceFixtures.Proposal(0, "Primary", "Llm", "soviet-rhino-rush", P(1000), $"d{m}-0"),
                Attack(1500, 1000, $"d{m}-0"),
                InduceFixtures.Proposal(2000, "Primary", "Llm", "soviet-turtle", [], $"d{m}-1"),
                Attack(2100, 1200, $"d{m}-1"),
                Attack(2200, 1300, $"d{m}-1"),
            ];
            if (m >= 2)
            {
                lines.Add(Stage(3000, $"d{m}-1"));
                lines.Add(Attack(6000, 3000, $"d{m}-1"));
            }
            InduceFixtures.Custom(dir, $"d{m}", 2 + 2 * m, [.. lines]);
        }
        InductionResult result = Induce(dir, minSupport: 2);
        Playbook rush = Assert.Single(result.Playbooks, static p => p.Id.StartsWith("induced-soviet-rhino-rush-", StringComparison.Ordinal));
        Assert.Equal(1000, rush.AttackConditions.Single(static c => c.Metric == ConditionMetric.OwnArmyValue).Threshold);
        // Only the two matches with a separate later attack launch under turtle, at 400 s with army 3000; 1200 (mid-attack) is never credited.
        Playbook turtle = Assert.Single(result.Playbooks, static p => p.Id.StartsWith("induced-soviet-turtle-", StringComparison.Ordinal));
        Assert.Equal(3000, turtle.AttackConditions.Single(static c => c.Metric == ConditionMetric.OwnArmyValue).Threshold);
        Assert.Equal(400, turtle.AttackConditions.Single(static c => c.Metric == ConditionMetric.GameSeconds).Threshold);
        Assert.Contains("2/4 matches launched", result.Report);
    }

    [Fact]
    public void A_launch_under_no_adopted_llm_playbook_is_credited_to_nobody_and_a_small_collection_prints_the_largest_support()
    {
        string dir = Path.Combine(root, "decisions");
        for (int m = 0; m < 3; m++)
        {
            InduceFixtures.Custom(dir, $"c{m}", 2 + 2 * m,
                InduceFixtures.Proposal(0, "Primary", "Llm", "soviet-rhino-rush", P(1000), $"c{m}-0"),
                InduceFixtures.Proposal(1, "Primary", "Selector", "generic-defend", [], $"c{m}-s"),
                Attack(5000, 1500, $"c{m}-s"));
        }
        InductionResult result = Induce(dir, minSupport: 5);
        Assert.Empty(result.Playbooks);
        Assert.Contains("3 supporting won matches", result.Note);
        Assert.Contains("--min-support 3", result.Note);
        Assert.Null(Induce(InduceFixtures.Standard(Path.Combine(root, "std"))).Note);
    }

    [Fact]
    public void The_induced_attack_gate_is_in_force_in_the_build_phase()
    {
        Playbook induced = Assert.Single(Induce(InduceFixtures.Standard(root)).Playbooks);
        Condition army = new(ConditionMetric.OwnArmyValue, Comparison.Ge, 1500), time = new(ConditionMetric.GameSeconds, Comparison.Ge, 325);
        Assert.Contains(army, induced.AttackConditions);
        Assert.Contains(time, induced.AttackConditions);

        // The build phase overrides nothing, so the intent's own (playbook top-level) attack conditions are what apply.
        StrategicIntent intent = PlaybookIntents.FromPlaybook(induced, Fx.Features(10, faction: Faction.Soviet), "i", IntentSource.Scripted);
        PhaseTracker tracker = new();
        tracker.Reset(intent, new PlaybookLibrary([induced]));
        Assert.Equal("build", tracker.Name);
        Assert.Contains(army, tracker.Effective(intent).AttackConditions);
        Assert.Contains(time, tracker.Effective(intent).AttackConditions);
        Assert.False(ConditionEvaluator.AllOf(tracker.Effective(intent).AttackConditions, Fx.Features(200, faction: Faction.Soviet, ownArmy: 5000)));
        Assert.False(ConditionEvaluator.AllOf(tracker.Effective(intent).AttackConditions, Fx.Features(400, faction: Faction.Soviet, ownArmy: 1000)));
        Assert.True(ConditionEvaluator.AllOf(tracker.Effective(intent).AttackConditions, Fx.Features(400, faction: Faction.Soviet, ownArmy: 1600)));
    }

    [Fact]
    public void The_command_writes_the_file_and_report_and_the_split_defaults_to_training()
    {
        string dir = InduceFixtures.Standard(root);
        string outFile = Path.Combine(root, "out.json"), report = Path.Combine(root, "report.md");
        Assert.Equal(0, Program.Main(["induce", "--from", dir, "--arm", InduceFixtures.Arm, "--min-support", "4", "--out", outFile, "--report", report]));
        Assert.Equal(Induce(dir).Json, File.ReadAllText(outFile));
        string text = File.ReadAllText(report);
        Assert.Contains("## `induced-soviet-rhino-rush-soviet-", text);
        Assert.Contains("median 1150", text);
        Assert.Contains("SHA-256", text);
        Assert.Contains("Selection on outcome", text);
        Assert.Contains("--split", CliOptions.Usage);
        Assert.Contains("arena induce", CliOptions.Usage);

        Assert.Equal("training", InduceOptions.Parse(["induce", "--from", dir, "--arm", "x", "--out", outFile]).Split);
        Assert.Equal(5, InduceOptions.Parse(["induce", "--from", dir, "--arm", "x", "--out", outFile]).MinSupport);
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
