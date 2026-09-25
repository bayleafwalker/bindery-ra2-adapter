// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Sim;
using Bindery.Ra2.Bot.Strategy;

namespace Bindery.Ra2.Bot.Arena;

/// <summary>An arm that did not run, and why.</summary>
public sealed record SkippedArm(string Arm, string Reason);

/// <summary>
/// `arena run` entry point: arms × maps (for the requested split) × opponents ×
/// seeds, every side a full <c>BotRuntime</c> on the bindery region sim (not
/// retail RA2). Writes <c>results.json</c>, <c>probes.json</c>, <c>report.md</c>
/// and one <c>dataset-&lt;arm&gt;.ndjson</c> of training-map decision examples per arm into <c>--out</c>.
/// </summary>
/// <remarks>
/// Order: the distilled arm needs a dataset, so when <c>--dataset</c> is absent it trains on the
/// selector arm's primary decisions from this run (running the selector's training-map matches
/// first, unreported, if the selector arm was not requested) and says so in its label. The bandit's
/// matches run sequentially in a fixed order because it learns across them; other arms run in
/// parallel. A live LLM arm runs its first match alone and is skipped with the recorded reason if no
/// credential resolved.
/// </remarks>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            CliOptions options = CliOptions.Parse(args);
            Run(options);
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void Run(CliOptions options)
    {
        Stopwatch wall = Stopwatch.StartNew();
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();
        IPlaybookLibrary playbooks = PlaybookLibrary.LoadDefault();
        ArenaRunContext context = new(options.LlmFake, options.LlmLatencySeconds);
        BotAgentFactory factory = new(rules, playbooks, context);
        foreach (string arm in options.Arms)
        {
            if (!BotAgentFactory.Arms.Contains(arm)) throw new ArgumentException($"Unknown arm '{arm}'. Arms: {string.Join(", ", BotAgentFactory.Arms)}.");
        }
        foreach (string opponent in options.Opponents)
        {
            if (!BotAgentFactory.OpponentStyles.ContainsKey(opponent)) throw new ArgumentException($"Unknown opponent '{opponent}'.");
        }

        List<(SimMap Map, string Split)> maps = MapsForSplit(options.MapSplit);
        List<ArmSpec> arms = [.. options.Arms.Select(a => new ArmSpec(a, options.Oracle, options.LlmFake))];
        if (options.Dataset is not null)
        {
            using StreamReader reader = new(options.Dataset);
            context.DistillDataset = DecisionDataset.ReadNdjson(reader);
            context.DistillSource = Path.GetFileName(options.Dataset);
        }

        // Distilled last, after its dataset source.
        arms = [.. arms.OrderBy(static a => a.Name == "distilled" ? 1 : 0)];
        List<MatchRecord> results = [];
        List<SkippedArm> skipped = [];
        List<LeakageProbeResult> probes = [];
        Directory.CreateDirectory(options.OutDir);

        foreach (ArmSpec arm in arms)
        {
            if (arm.Name == "distilled" && context.DistillDataset is null)
            {
                context.DistillDataset = SelectorDataset(options, maps, rules, factory);
                context.DistillSource = "selector decisions gathered in this run (training maps, unreported)";
            }

            List<(ArmSpec Arm, string Opponent, SimMap Map, string Split, int Seed)> jobs = Jobs(arm, options, maps);
            ConcurrentDictionary<int, IReadOnlyList<DecisionRecord>> logs = new();
            MatchRecord[] armResults = new MatchRecord[jobs.Count];
            int start = 0;
            if (arm.UsesLlm && !options.LlmFake)
            {
                armResults[0] = RunJob(jobs[0], options, rules, factory, log => logs[0] = log);
                start = 1;
                if (context.LlmSkipReason is { } reason)
                {
                    skipped.Add(new SkippedArm(arm.ToString(), reason));
                    Console.WriteLine($"{arm}: {reason}");
                    continue;
                }
            }

            if (arm.Name == "bandit" || (arm.UsesLlm && !options.LlmFake))
            {
                for (int i = start; i < jobs.Count; i++)
                {
                    int index = i;
                    armResults[i] = RunJob(jobs[i], options, rules, factory, log => logs[index] = log);
                }
            }
            else
            {
                Parallel.For(start, jobs.Count, i => armResults[i] = RunJob(jobs[i], options, rules, factory, log => logs[i] = log));
            }
            results.AddRange(armResults);

            // Exported datasets hold training-map decisions only, so a distilled arm trained on them is never
            // evaluated on maps its teacher's data came from.
            DecisionDataset dataset = DecisionDataset.Merge(Enumerable.Range(0, jobs.Count)
                .Where(i => logs.ContainsKey(i) && jobs[i].Split == "training")
                .Select(i => DecisionDataset.FromDecisionLog(logs[i], DatasetFilter.PrimaryOnly, MatchId(jobs[i]))));
            using (StreamWriter writer = new(Path.Combine(options.OutDir, $"dataset-{Slug(arm.ToString())}.ndjson")))
            {
                dataset.WriteNdjson(writer);
            }
            if (arm.Name == "selector" && !arm.Oracle && context.DistillDataset is null && arms.Any(static a => a.Name == "distilled"))
            {
                context.DistillDataset = DecisionDataset.Merge(Enumerable.Range(0, jobs.Count)
                    .Where(i => logs.ContainsKey(i) && jobs[i].Split == "training")
                    .Select(i => DecisionDataset.FromDecisionLog(logs[i], DatasetFilter.PrimaryOnly, MatchId(jobs[i]))));
                context.DistillSource = "selector decisions in this run (training maps)";
            }

            LeakageProbeResult probe = LeakageProbe.Run(arm, maps[0].Map, 1, rules, factory);
            if (arm.Name == "bandit") context.Bandit.AbandonEpisode();
            probes.Add(probe);
            Console.WriteLine($"{arm}: {armResults.Length} matches, {armResults.Count(static r => r.Winner == 0)} wins; leakage probe {probe.Differences}/{probe.FramesCompared} differing frames.");
        }

        List<MatchRecord> ordered = [.. results.OrderBy(r => r.Arm, StringComparer.Ordinal)
                                                 .ThenBy(r => r.Opponent, StringComparer.Ordinal)
                                                 .ThenBy(r => r.Map, StringComparer.Ordinal)
                                                 .ThenBy(r => r.Seed)];

        JsonSerializerOptions indented = new(BotJson.Options) { WriteIndented = true };
        File.WriteAllText(Path.Combine(options.OutDir, "results.json"), JsonSerializer.Serialize(ordered, indented));
        File.WriteAllText(Path.Combine(options.OutDir, "probes.json"), JsonSerializer.Serialize(new { probes, skipped }, indented));
        File.WriteAllText(Path.Combine(options.OutDir, "report.md"), ReportBuilder.Build(ordered, probes, skipped, options, rules.RulesetId));

        Console.WriteLine($"Wrote {ordered.Count} match results, {probes.Count} leakage probes and report.md to {options.OutDir} in {wall.Elapsed.TotalSeconds:0}s.");
    }

    /// <summary>Runs the selector on the training maps (unreported) to gather decisions when the selector arm was not requested.</summary>
    private static DecisionDataset SelectorDataset(CliOptions options, List<(SimMap Map, string Split)> maps, IRulesDatabase rules, BotAgentFactory factory)
    {
        List<(ArmSpec Arm, string Opponent, SimMap Map, string Split, int Seed)> jobs =
            Jobs(new ArmSpec("selector", false, options.LlmFake), options, [.. maps.Where(static m => m.Split == "training")]);
        ConcurrentDictionary<int, IReadOnlyList<DecisionRecord>> logs = new();
        Parallel.For(0, jobs.Count, i => RunJob(jobs[i], options, rules, factory, log => logs[i] = log));
        return DecisionDataset.Merge(Enumerable.Range(0, jobs.Count)
            .Where(logs.ContainsKey)
            .Select(i => DecisionDataset.FromDecisionLog(logs[i], DatasetFilter.PrimaryOnly, MatchId(jobs[i]))));
    }

    private static List<(ArmSpec Arm, string Opponent, SimMap Map, string Split, int Seed)> Jobs(ArmSpec arm, CliOptions options, List<(SimMap Map, string Split)> maps)
    {
        List<(ArmSpec, string, SimMap, string, int)> jobs = [];
        foreach (string opponent in options.Opponents)
        {
            foreach ((SimMap map, string split) in maps)
            {
                for (int seed = 1; seed <= options.Seeds; seed++) jobs.Add((arm, opponent, map, split, seed));
            }
        }
        return jobs;
    }

    private static MatchRecord RunJob((ArmSpec Arm, string Opponent, SimMap Map, string Split, int Seed) job, CliOptions options, IRulesDatabase rules, IArenaAgentFactory factory, Action<IReadOnlyList<DecisionRecord>> armLog) =>
        MatchRunner.Run(job.Arm, job.Opponent, job.Map, job.Split, job.Seed, options.MaxSeconds, rules, factory, armLog);

    private static string MatchId((ArmSpec Arm, string Opponent, SimMap Map, string Split, int Seed) job) =>
        $"{job.Arm}/{job.Opponent}/{job.Map.Map.MapId}/{job.Seed}";

    private static string Slug(string name) => name.Replace('+', '-');

    private static List<(SimMap Map, string Split)> MapsForSplit(string split) => split.ToLowerInvariant() switch
    {
        "training" => [.. SimMaps.Training.Select(m => (m, "training"))],
        "heldout" => [.. SimMaps.HeldOut.Select(m => (m, "heldout"))],
        "all" => [.. SimMaps.Training.Select(m => (m, "training")), .. SimMaps.HeldOut.Select(m => (m, "heldout"))],
        _ => throw new ArgumentException($"Unknown map split '{split}'; expected training, heldout or all."),
    };
}
