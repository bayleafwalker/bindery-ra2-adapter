// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

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
    double? LlmLatencySeconds = null,
    string? TraceDir = null)
{
    public const double DefaultMaxSeconds = 1200;

    public const string Usage =
        "Usage: arena run --arms a,b --maps training|heldout|all --opponents ai-rush,ai-balanced[:easy|:medium|:hard],rush,turtle,...|all --seeds N --out <dir> " +
        "[--oracle [both|all]] [--llm-fake] [--max-seconds N] [--dataset <decisions.ndjson>] [--llm-latency <game seconds>] [--trace <dir>] " +
        "[--benchmark standard|contested] [--opponent-income X] [--opponent-credits N] [--combat-noise F] [--allied-income X] [--allied-credits N] [--baseline <arm>] [--no-decisions] [--write-adoption <path>] [--personality aggressive,turtle,tech,harasser,none]\n" +
        "       (arms: selector, bandit, llm-shadow, llm, llm+fast, distilled, llm-t0..llm-t3 or tiers, all; any with -oracle)\n" +
        "       arena replay <out>/decisions/<match>.ndjson [--out <replayed.ndjson>]\n" +
        "       arena analyze <out>/decisions/<match>.ndjson [--out <report.md>] [--narrate] [--llm-fake]";

    /// <summary>The benchmark the matches run under (<see cref="BenchmarkSettings.Standard"/> unless set).</summary>
    public BenchmarkSettings Benchmark { get; init; } = BenchmarkSettings.Standard;

    /// <summary>The arm every other arm is compared with, pair by pair, in the report.</summary>
    public string Baseline { get; init; } = "selector";

    /// <summary>Play styles every arm runs under (null for none); <c>--personality aggressive,turtle</c>.</summary>
    public IReadOnlyList<string?> PersonalityList { get; init; } = [null];

    /// <summary>Also write the run's <c>vocabulary-adoption.json</c> here (for example the embedded record in the Claude project).</summary>
    public string? WriteAdoption { get; init; }

    /// <summary>Write each arm match's decision log and manifest into <c>&lt;out&gt;/decisions/</c> (for <c>arena replay</c> and <c>arena analyze</c>).</summary>
    public bool WriteDecisions { get; init; } = true;

    /// <summary><c>none</c> (belief frames unless an arm is named <c>*-oracle</c>), <c>all</c> (the legacy <c>--oracle</c>) or <c>both</c>.</summary>
    public string OracleMode { get; init; } = "none";

    /// <summary>Suffix that makes an arm name an oracle arm (<c>selector-oracle</c>).</summary>
    public const string OracleSuffix = "-oracle";

    /// <summary>
    /// The arms to run, in order: each named arm (a <c>-oracle</c> suffix makes it an oracle arm), both belief and
    /// oracle for every arm under <c>--oracle both</c>, oracle only under the legacy <c>--oracle</c>. Duplicates are dropped.
    /// </summary>
    public IReadOnlyList<ArmSpec> ArmSpecs()
    {
        List<ArmSpec> specs = [];
        foreach (string token in Arms)
        {
            bool suffixed = token.EndsWith(OracleSuffix, StringComparison.Ordinal);
            string name = suffixed ? token[..^OracleSuffix.Length] : token;
            IEnumerable<bool> modes = suffixed ? [true] : OracleMode switch
            {
                "all" => [true],
                "both" => [false, true],
                _ => [false],
            };
            foreach (bool oracle in modes)
            {
                foreach (string? personality in PersonalityList)
                {
                    ArmSpec spec = new(name, oracle, LlmFake) { Personality = personality };
                    if (!specs.Contains(spec)) specs.Add(spec);
                }
            }
        }
        return specs;
    }

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] != "run") throw new ArgumentException(Usage);

        List<string> arms = ["selector"];
        string mapSplit = "training";
        List<string>? opponents = null;
        int seeds = 1;
        string outDir = "arena-out";
        bool oracle = false;
        string oracleMode = "none";
        bool llmFake = false;
        double maxSeconds = DefaultMaxSeconds;
        string? dataset = null;
        double? llmLatency = null;
        string? traceDir = null;
        BenchmarkSettings benchmark = BenchmarkSettings.Standard;
        double? opponentIncome = null;
        int? opponentCredits = null;
        double? combatNoise = null;
        double? alliedIncome = null;
        int? alliedCredits = null;
        string baseline = "selector";
        bool writeDecisions = true;
        string? writeAdoption = null;
        List<string?> personalities = [null];

        for (int i = 1; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--arms": arms = Split(args, ref i); break;
                case "--maps": mapSplit = Next(args, ref i); break;
                case "--opponents": opponents = Split(args, ref i); break;
                case "--seeds": seeds = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                case "--out": outDir = Next(args, ref i); break;
                case "--oracle":
                    if (i + 1 < args.Count && args[i + 1] is "both" or "all" or "none")
                    {
                        oracleMode = args[++i];
                    }
                    else
                    {
                        oracleMode = "all";
                    }
                    oracle = oracleMode == "all";
                    break;
                case "--llm-fake": llmFake = true; break;
                case "--max-seconds": maxSeconds = double.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                case "--dataset": dataset = Next(args, ref i); break;
                case "--trace": traceDir = Next(args, ref i); break;
                case "--llm-latency": llmLatency = double.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                case "--benchmark": benchmark = BenchmarkSettings.Preset(Next(args, ref i)); break;
                case "--opponent-income": opponentIncome = double.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                case "--opponent-credits": opponentCredits = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                case "--combat-noise": combatNoise = double.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                case "--allied-income": alliedIncome = double.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                case "--allied-credits": alliedCredits = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                case "--baseline": baseline = Next(args, ref i); break;
                case "--no-decisions": writeDecisions = false; break;
                case "--write-adoption": writeAdoption = Next(args, ref i); break;
                case "--personality":
                    personalities = [.. Split(args, ref i).Select(static p => p == "none" ? null : p)];
                    foreach (string? p in personalities)
                    {
                        if (p is not null && !Bindery.Ra2.Bot.Strategy.Personalities.TryGet(p, out _))
                        {
                            throw new ArgumentException($"Unknown personality '{p}'. Personalities: {string.Join(", ", Bindery.Ra2.Bot.Strategy.Personalities.All.Select(static x => x.Id))}, none.");
                        }
                    }
                    break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }

        if (opponentIncome is not null || opponentCredits is not null || combatNoise is not null || alliedIncome is not null || alliedCredits is not null)
        {
            benchmark = benchmark with
            {
                Name = benchmark == BenchmarkSettings.Standard ? "custom" : $"{benchmark.Name}+custom",
                OpponentIncomeMultiplier = opponentIncome ?? benchmark.OpponentIncomeMultiplier,
                OpponentStartingCredits = opponentCredits ?? benchmark.OpponentStartingCredits,
                CombatNoise = combatNoise ?? benchmark.CombatNoise,
                AlliedIncomeMultiplier = alliedIncome ?? benchmark.AlliedIncomeMultiplier,
                AlliedStartingCredits = alliedCredits ?? benchmark.AlliedStartingCredits,
            };
        }
        opponents ??= benchmark.DefaultOpponents is { } preset ? [.. preset] : ["balanced"];
        if (opponents.Count == 1 && opponents[0] == "all") opponents = [.. BotAgentFactory.AllOpponents];
        arms = [.. arms.SelectMany(static a => a switch
        {
            "all" => BotAgentFactory.Arms,
            "tiers" => (IEnumerable<string>)BotAgentFactory.TierArms.Keys,
            _ => [a],
        }).Distinct(StringComparer.Ordinal)];
        return new CliOptions(arms, mapSplit, opponents, seeds, outDir, oracle, llmFake, maxSeconds, dataset, llmLatency, traceDir)
        {
            Benchmark = benchmark,
            Baseline = baseline,
            OracleMode = oracleMode,
            WriteDecisions = writeDecisions,
            WriteAdoption = writeAdoption,
            PersonalityList = personalities,
        };
    }

    private static string Next(IReadOnlyList<string> args, ref int i)
    {
        if (i + 1 >= args.Count) throw new ArgumentException($"Missing value for '{args[i]}'.");
        return args[++i];
    }

    private static List<string> Split(IReadOnlyList<string> args, ref int i) =>
        [.. Next(args, ref i).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
