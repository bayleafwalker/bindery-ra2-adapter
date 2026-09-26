// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bindery.Ra2.Bot.Arena;

namespace Bindery.Ra2.Bot.Tune;

/// <summary>
/// Parsed <c>tune search</c> / <c>tune validate</c> arguments. Hand-rolled like the arena's, so the tool has no
/// dependency beyond the bot, the simulator and the arena.
/// </summary>
/// <param name="Command"><c>search</c> or <c>validate</c>.</param>
/// <param name="Mode"><c>benchmark</c> (default opponents: every <c>ai-*</c> style and pinned style) or <c>selfplay</c> (default opponents: <c>bot:default</c>, <c>bot:champion</c>).</param>
/// <param name="Date">Provenance date, from the command line so output never depends on the clock.</param>
public sealed record TuneOptions(
    string Command,
    string Mode,
    IReadOnlyList<string> Opponents,
    int Generations,
    int Population,
    int? Parents,
    double Sigma,
    ulong Seed,
    int SeedsPerGeneration,
    int FinalSeeds,
    int Seeds,
    double TradeWeight,
    double MaxSeconds,
    int Threads,
    string OutDir,
    string Date,
    string? Commit,
    string? Tuned,
    string? Write)
{
    public const string Usage =
        "Usage:\n" +
        "  tune search --date YYYY-MM-DD --out <dir> [--mode benchmark|selfplay] [--opponents a,b,...|all|self]\n" +
        "              [--generations 16] [--population 12] [--parents 6] [--sigma 0.2] [--seed 1]\n" +
        "              [--seeds-per-generation 2] [--final-seeds 6] [--trade-weight 0.1] [--max-seconds 1200]\n" +
        "              [--threads N] [--commit <sha>]\n" +
        "  tune validate --tuned <tuned-candidate.json> --date YYYY-MM-DD --out <dir> [--seeds 20]\n" +
        "              [--opponents ...] [--trade-weight 0.1] [--max-seconds 1200] [--threads N] [--write <path>]\n" +
        "Opponents: ai-rush, ai-balanced, ai-turtle, ai-air (optional :easy|:medium|:hard), rush, turtle, tech, harass,\n" +
        "balanced (frozen baseline stack), live-<style>, bot:default (untuned live bot), bot:champion (search's\n" +
        "previous-generation best; the untuned bot in validation). 'all' = every ai-* and pinned style; 'self' = both bot:*.";

    public static TuneOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0 || args[0] is not ("search" or "validate")) throw new ArgumentException(Usage);
        string command = args[0];
        string mode = "benchmark";
        List<string>? opponents = null;
        int generations = 16, population = 12, seedsPerGeneration = 2, finalSeeds = 6, seeds = 20;
        int? parents = null;
        double sigma = 0.2, tradeWeight = 0.1, maxSeconds = CliOptions.DefaultMaxSeconds;
        ulong seed = 1;
        int threads = Environment.ProcessorCount;
        string outDir = "tune-out";
        string? date = null, commit = null, tuned = null, write = null;

        for (int i = 1; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--mode": mode = Next(args, ref i); break;
                case "--opponents": opponents = [.. Next(args, ref i).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]; break;
                case "--generations": generations = Int(args, ref i); break;
                case "--population": population = Int(args, ref i); break;
                case "--parents": parents = Int(args, ref i); break;
                case "--sigma": sigma = Double(args, ref i); break;
                case "--seed": seed = ulong.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                case "--seeds-per-generation": seedsPerGeneration = Int(args, ref i); break;
                case "--final-seeds": finalSeeds = Int(args, ref i); break;
                case "--seeds": seeds = Int(args, ref i); break;
                case "--trade-weight": tradeWeight = Double(args, ref i); break;
                case "--max-seconds": maxSeconds = Double(args, ref i); break;
                case "--threads": threads = Int(args, ref i); break;
                case "--out": outDir = Next(args, ref i); break;
                case "--date": date = Next(args, ref i); break;
                case "--commit": commit = Next(args, ref i); break;
                case "--tuned": tuned = Next(args, ref i); break;
                case "--write": write = Next(args, ref i); break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'.\n{Usage}");
            }
        }

        if (mode is not ("benchmark" or "selfplay")) throw new ArgumentException($"Unknown mode '{mode}'; expected benchmark or selfplay.");
        if (date is null) throw new ArgumentException("--date is required (provenance is never read from the clock).");
        if (command == "validate" && tuned is null) throw new ArgumentException("validate needs --tuned <file>.");
        if (population < 2 || generations < 1 || seedsPerGeneration < 1 || seeds < 1 || threads < 1) throw new ArgumentException("Counts must be positive (population ≥ 2).");

        opponents ??= mode == "selfplay" ? ["self"] : ["all"];
        List<string> expanded = [];
        foreach (string o in opponents)
        {
            if (o == "all") expanded.AddRange(BotAgentFactory.AllOpponents);
            else if (o == "self") expanded.AddRange(VariantAgentFactory.SelfPlayOpponents);
            else if (VariantAgentFactory.IsOpponent(o)) expanded.Add(o);
            else throw new ArgumentException($"Unknown opponent '{o}'.\n{Usage}");
        }
        return new TuneOptions(command, mode, [.. expanded.Distinct(StringComparer.Ordinal)], generations, population, parents, sigma, seed,
            seedsPerGeneration, finalSeeds, seeds, tradeWeight, maxSeconds, threads, outDir, date, commit, tuned, write);
    }

    private static string Next(IReadOnlyList<string> args, ref int i)
    {
        if (i + 1 >= args.Count) throw new ArgumentException($"Missing value for '{args[i]}'.");
        return args[++i];
    }

    private static int Int(IReadOnlyList<string> args, ref int i) => int.Parse(Next(args, ref i), CultureInfo.InvariantCulture);

    private static double Double(IReadOnlyList<string> args, ref int i) => double.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
}
