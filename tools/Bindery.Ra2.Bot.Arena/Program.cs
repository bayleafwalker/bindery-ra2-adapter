// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot;
using Bindery.Ra2.Bot.Sim;

namespace Bindery.Ra2.Bot.Arena;

/// <summary>
/// `arena run` entry point: arms × maps (for the requested split) ×
/// opponents × seeds, run against the bindery region sim, not retail RA2.
/// Writes `results.json` and `report.md` into <c>--out</c>.
/// </summary>
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
        ArenaRuleset rules = new();
        ScriptedAgentFactory factory = new(rules);

        List<(SimMap Map, string Split)> maps = MapsForSplit(options.MapSplit);
        List<ArmSpec> arms = [.. options.Arms.Select(a => new ArmSpec(a, options.Oracle, options.LlmFake))];

        List<(ArmSpec Arm, string Opponent, SimMap Map, string Split, int Seed)> jobs = [];
        foreach (ArmSpec arm in arms)
        {
            foreach (string opponent in options.Opponents)
            {
                foreach ((SimMap map, string split) in maps)
                {
                    for (int seed = 1; seed <= options.Seeds; seed++)
                    {
                        jobs.Add((arm, opponent, map, split, seed));
                    }
                }
            }
        }

        MatchRecord[] results = new MatchRecord[jobs.Count];
        Parallel.For(0, jobs.Count, i =>
        {
            (ArmSpec arm, string opponent, SimMap map, string split, int seed) = jobs[i];
            results[i] = MatchRunner.Run(arm, opponent, map, split, seed, options.MaxSeconds, rules, factory);
        });

        List<MatchRecord> ordered = [.. results.OrderBy(r => r.Arm, StringComparer.Ordinal)
                                                 .ThenBy(r => r.Opponent, StringComparer.Ordinal)
                                                 .ThenBy(r => r.Map, StringComparer.Ordinal)
                                                 .ThenBy(r => r.Seed)];

        bool leakagePassed = RunLeakageProbe(rules);

        Directory.CreateDirectory(options.OutDir);
        string resultsPath = Path.Combine(options.OutDir, "results.json");
        string reportPath = Path.Combine(options.OutDir, "report.md");
        File.WriteAllText(resultsPath, JsonSerializer.Serialize(ordered, new JsonSerializerOptions(BotJson.Options) { WriteIndented = true }));
        File.WriteAllText(reportPath, ReportBuilder.Build(ordered, leakagePassed));

        Console.WriteLine($"Wrote {ordered.Count} match results to {resultsPath}");
        Console.WriteLine($"Wrote report to {reportPath}");
    }

    private static List<(SimMap Map, string Split)> MapsForSplit(string split) => split.ToLowerInvariant() switch
    {
        "training" => [.. SimMaps.Training.Select(m => (m, "training"))],
        "heldout" => [.. SimMaps.HeldOut.Select(m => (m, "heldout"))],
        "all" => [.. SimMaps.Training.Select(m => (m, "training")), .. SimMaps.HeldOut.Select(m => (m, "heldout"))],
        _ => throw new ArgumentException($"Unknown map split '{split}'; expected training, heldout or all."),
    };

    /// <summary>
    /// Perturbs hidden enemy state in a fresh belief-mode match and asserts
    /// the observer's frame is byte-identical before and after, per the
    /// spec's hidden-information invariant.
    /// </summary>
    private static bool RunLeakageProbe(ArenaRuleset rules)
    {
        SimSettings settings = new(1, 60, [new SimPlayer(new PlayerId(0), Faction.Allied), new SimPlayer(new PlayerId(1), Faction.Soviet)]);
        SkirmishSimulation sim = new(SimMaps.Training[0], rules, settings);
        sim.Step();
        PlayerId observer = new(0);
        ObservationFrame before = sim.Observe(observer);
        string beforeJson = JsonSerializer.Serialize(before, BotJson.Options);
        SimLeakageProbe.PerturbHidden(sim, observer);
        ObservationFrame after = sim.Observe(observer);
        string afterJson = JsonSerializer.Serialize(after, BotJson.Options);
        return beforeJson == afterJson;
    }
}
