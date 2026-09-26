// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Arena;

/// <summary>Parsed `arena run` arguments. Deliberately hand-rolled (no CLI framework dependency) to keep this tool self-contained.</summary>
public sealed record CliOptions(
    IReadOnlyList<string> Arms,
    string MapSplit,
    IReadOnlyList<string> Opponents,
    int Seeds,
    string OutDir,
    bool Oracle,
    bool LlmFake,
    double MaxSeconds,
    string? Dataset = null,
    double? LlmLatencySeconds = null)
{
    public const double DefaultMaxSeconds = 1200;

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] != "run")
        {
            throw new ArgumentException("Usage: arena run --arms a,b --maps training|heldout|all --opponents ai-rush,ai-balanced[:easy|:medium|:hard],rush,turtle,...|all --seeds N --out <dir> [--oracle] [--llm-fake] [--max-seconds N] [--dataset <decisions.ndjson>] [--llm-latency <game seconds>]");
        }

        List<string> arms = ["selector"];
        string mapSplit = "training";
        List<string> opponents = ["balanced"];
        int seeds = 1;
        string outDir = "arena-out";
        bool oracle = false;
        bool llmFake = false;
        double maxSeconds = DefaultMaxSeconds;
        string? dataset = null;
        double? llmLatency = null;

        for (int i = 1; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--arms": arms = Split(args, ref i); break;
                case "--maps": mapSplit = Next(args, ref i); break;
                case "--opponents": opponents = Split(args, ref i); break;
                case "--seeds": seeds = int.Parse(Next(args, ref i), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--out": outDir = Next(args, ref i); break;
                case "--oracle": oracle = true; break;
                case "--llm-fake": llmFake = true; break;
                case "--max-seconds": maxSeconds = double.Parse(Next(args, ref i), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--dataset": dataset = Next(args, ref i); break;
                case "--llm-latency": llmLatency = double.Parse(Next(args, ref i), System.Globalization.CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }

        if (opponents.Count == 1 && opponents[0] == "all") opponents = [.. BotAgentFactory.AllOpponents];
        if (arms.Count == 1 && arms[0] == "all") arms = [.. BotAgentFactory.Arms];
        return new CliOptions(arms, mapSplit, opponents, seeds, outDir, oracle, llmFake, maxSeconds, dataset, llmLatency);
    }

    private static string Next(IReadOnlyList<string> args, ref int i)
    {
        if (i + 1 >= args.Count) throw new ArgumentException($"Missing value for '{args[i]}'.");
        return args[++i];
    }

    private static List<string> Split(IReadOnlyList<string> args, ref int i) =>
        [.. Next(args, ref i).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
