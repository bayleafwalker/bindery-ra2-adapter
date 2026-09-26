// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Diagnostics;
using System.Text;
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Sim;
using Bindery.Ra2.Bot.Tuning;
using static Bindery.Ra2.Bot.Tune.Scoring;

namespace Bindery.Ra2.Bot.Tune;

/// <summary>
/// Evolutionary self-play tuning of playbook parameters and option knobs (<see cref="TuningSpace"/>).
/// <list type="bullet">
/// <item><c>search</c>: separable CMA-ES (<see cref="EvolutionStrategy"/>) on the TRAINING maps only. Every
/// candidate of a generation plays the same matches (common random numbers: same opponents, maps and seeds),
/// alongside the current distribution mean and the untuned defaults, so "better than default" is measured on
/// identical games each generation. Writes <c>tuned-candidate.json</c>, <c>curve.csv</c> and <c>search.md</c>.</item>
/// <item><c>validate</c>: the candidate against the untuned defaults on the HELD-OUT maps, plus a head-to-head
/// against the untuned bot with a default-vs-default control; applies the adoption rule and writes the result
/// (to <c>--write</c>, normally <c>src/Bindery.Ra2.Bot/Data/tuned-parameters.json</c>) either way.</item>
/// </list>
/// </summary>
public static class Program
{
    /// <summary>The adoption rule, fixed before any validation ran.</summary>
    public const string AdoptionRule =
        "Adopt only if, on held-out maps: (1) the tuned bot wins at least as many benchmark matches as the untuned bot; " +
        "(2) the paired per-match fitness difference (tuned - untuned, same opponent/map/seed) has a 95% interval entirely above 0; and " +
        "(3) in head-to-head play against the untuned bot the tuned bot wins at least as often as the untuned bot does against itself (the seat-bias control).";

    public static int Main(string[] args)
    {
        try
        {
            TuneOptions options = TuneOptions.Parse(args);
            if (options.Command == "search") Search(options);
            else Validate(options);
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void Search(TuneOptions options)
    {
        Stopwatch wall = Stopwatch.StartNew();
        IRulesDatabase rules = LoadRules(options.Rules);
        BotAgentFactory arena = new(rules, BotVariant.Fit(PlaybookLibrary.LoadAuthored(), rules), new ArenaRunContext(false, null));
        TuningSpace space = new(PlaybookLibrary.LoadAuthored().All, new(), new());
        EvolutionStrategy es = new(space.DefaultPoint(), options.Sigma, options.Population, options.Seed, options.Parents);
        IReadOnlyList<SimMap> maps = SimMaps.Training;
        Directory.CreateDirectory(options.OutDir);

        StringBuilder curve = new("generation,seeds,best,median,worst,mean_point,default,best_minus_default,mean_minus_default,sigma,best_wins,default_wins,matches\n");
        BotVariant champion = BotVariant.Authored;
        double[]? bestPoint = null;
        double bestAdvantage = double.NegativeInfinity;
        int matchesPlayed = 0;
        List<int> allSeeds = [];

        for (int g = 0; g < options.Generations; g++)
        {
            int[] seeds = [.. Enumerable.Range(g * options.SeedsPerGeneration + 1, options.SeedsPerGeneration)];
            allSeeds.AddRange(seeds);
            IReadOnlyList<double[]> points = es.Ask();
            List<BotVariant> variants = [.. points.Select((p, k) => BotVariant.From($"g{g}-{k}", space.Decode(p)))];
            variants.Add(BotVariant.From($"g{g}-mean", space.Decode([.. es.Mean])));
            variants.Add(BotVariant.Authored);
            (double[] fitness, int[] wins, int perCandidate) = Evaluate(variants, champion, options, rules, arena, maps, "training", seeds);
            matchesPlayed += perCandidate * variants.Count;

            double defaultFitness = fitness[^1];
            double meanFitness = fitness[^2];
            double[] sampleFitness = fitness[..options.Population];
            int best = Enumerable.Range(0, options.Population).OrderByDescending(k => sampleFitness[k]).ThenBy(static k => k).First();
            es.Tell(sampleFitness);
            if (sampleFitness[best] - defaultFitness > bestAdvantage)
            {
                bestAdvantage = sampleFitness[best] - defaultFitness;
                bestPoint = points[best];
            }
            champion = variants[best] with { Label = $"champion-g{g}" };

            GenerationSummary s = es.History[^1];
            curve.AppendLine(string.Join(',', g, string.Join(' ', seeds), F(s.BestFitness), F(s.MedianFitness), F(s.WorstFitness), F(meanFitness),
                F(defaultFitness), F(s.BestFitness - defaultFitness), F(meanFitness - defaultFitness), F(s.Sigma), wins[best], wins[^1], perCandidate));
            Console.WriteLine($"gen {g}: best {F(s.BestFitness)} median {F(s.MedianFitness)} mean-point {F(meanFitness)} default {F(defaultFitness)} " +
                string.Create(CultureInfo.InvariantCulture, $"(wins best {wins[best]}/{perCandidate}, default {wins[^1]}/{perCandidate}) sigma {F(s.Sigma)} [{wall.Elapsed.TotalSeconds:0}s]"));
            File.WriteAllText(Path.Combine(options.OutDir, "curve.csv"), curve.ToString());
        }

        // Final pick on fresh training seeds: the final distribution mean vs the best-advantage sample, both against the defaults.
        int[] finalSeeds = [.. Enumerable.Range(options.Generations * options.SeedsPerGeneration + 1, options.FinalSeeds)];
        allSeeds.AddRange(finalSeeds);
        TunedParameterSet meanSet = space.Decode([.. es.Mean]);
        TunedParameterSet bestSet = space.Decode(bestPoint!);
        List<BotVariant> finalists = [BotVariant.From("final-mean", meanSet), BotVariant.From("best-sample", bestSet), BotVariant.Authored];
        (double[] finalFitness, int[] finalWins, int finalPer) = Evaluate(finalists, champion, options, rules, arena, maps, "training", finalSeeds);
        matchesPlayed += finalPer * finalists.Count;
        bool meanWins = finalFitness[0] >= finalFitness[1];
        TunedParameterSet chosen = meanWins ? meanSet : bestSet;
        double tunedFitness = meanWins ? finalFitness[0] : finalFitness[1];

        TuningProvenance provenance = new(
            "tools/Bindery.Ra2.Bot.Tune search",
            $"sep-CMA-ES (mu/mu_w, lambda) lambda={options.Population} mu={es.Mu} sigma0={F(options.Sigma, "0.###")}, unit-cube reflection, SplitMix64",
            options.Mode, options.Generations, options.Population, es.Mu, options.Seed, allSeeds, options.Opponents,
            [.. maps.Select(static m => m.Map.MapId)], options.TradeWeight, matchesPlayed, finalFitness[2], tunedFitness, options.Date, options.Commit);
        chosen = chosen with { Provenance = provenance };
        File.WriteAllText(Path.Combine(options.OutDir, "tuned-candidate.json"), chosen.ToJson() + "\n");

        StringBuilder md = new();
        md.AppendLine("# Tuning search");
        md.AppendLine();
        md.AppendLine($"Rules `{rules.RulesetId}`; mode {options.Mode}; opponents {string.Join(", ", options.Opponents)}; training maps {string.Join(", ", provenance.Maps)}; " +
            string.Create(CultureInfo.InvariantCulture, $"{options.Generations} generations × ({options.Population} samples + mean + default) × {options.Opponents.Count} opponents × {maps.Count} maps × {options.SeedsPerGeneration} seeds; {matchesPlayed} matches in {wall.Elapsed.TotalSeconds:0} s."));
        md.AppendLine();
        md.AppendLine($"Final check on fresh training seeds {string.Join(' ', finalSeeds)} ({finalPer} matches each): final mean {F(finalFitness[0])} ({finalWins[0]} wins), " +
            $"best sample {F(finalFitness[1])} ({finalWins[1]} wins), untuned {F(finalFitness[2])} ({finalWins[2]} wins). Chosen: {(meanWins ? "final mean" : "best sample")}.");
        md.AppendLine();
        md.AppendLine("Inert playbook parameters (not searched, nothing reads them): " + string.Join(", ", space.InertPlaybookParameters) + ".");
        md.AppendLine();
        md.AppendLine("| Dimension | Range | Default | Tuned |");
        md.AppendLine("|---|---|---|---|");
        double[] chosenPoint = meanWins ? [.. es.Mean] : bestPoint!;
        for (int i = 0; i < space.Dimensions.Count; i++)
        {
            TuningDimension d = space.Dimensions[i];
            md.AppendLine($"| {d.Key} | {F(d.Min, "0.##")}–{F(d.Max, "0.##")} | {F(d.Default, "0.###")} | {F(d.Decode(chosenPoint[i]), "0.###")} |");
        }
        md.AppendLine();
        md.AppendLine("Curve (fitness = win 1 / draw 0.5 / loss 0 + trade weight × trade share, mean per candidate):");
        md.AppendLine();
        md.AppendLine("```");
        md.Append(curve);
        md.AppendLine("```");
        File.WriteAllText(Path.Combine(options.OutDir, "search.md"), md.ToString());
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Chose {(meanWins ? "final mean" : "best sample")}: training fitness {F(tunedFitness)} vs untuned {F(finalFitness[2])}. Wrote {options.OutDir} in {wall.Elapsed.TotalSeconds:0}s."));
    }

    /// <summary>The embedded fixture, or the <c>--rules</c> file.</summary>
    private static IRulesDatabase LoadRules(string? path)
    {
        if (path is null) return RulesDatabase.LoadEmbeddedFixture();
        if (!File.Exists(path)) throw new ArgumentException($"No rules file at {path}.");
        return RulesDatabase.LoadJson(File.ReadAllText(path));
    }

    private static void Validate(TuneOptions options)
    {
        Stopwatch wall = Stopwatch.StartNew();
        IRulesDatabase rules = LoadRules(options.Rules);
        BotAgentFactory arena = new(rules, BotVariant.Fit(PlaybookLibrary.LoadAuthored(), rules), new ArenaRunContext(false, null));
        TunedParameterSet tuned = TunedParameterSet.LoadJson(File.ReadAllText(options.Tuned!));
        IReadOnlyList<SimMap> maps = SimMaps.HeldOut;
        Directory.CreateDirectory(options.OutDir);
        int[] seeds = [.. Enumerable.Range(1, options.Seeds)];
        List<BotVariant> variants = [BotVariant.Authored, BotVariant.From("tuned", tuned)];

        // Benchmark: both variants play identical held-out matches; per-match scores are paired by job index.
        List<TuneJob> jobs = [.. Jobs(0, options.Opponents, maps, "heldout", seeds), .. Jobs(1, options.Opponents, maps, "heldout", seeds)];
        MatchRecord[] results = Play(jobs, k => new VariantAgentFactory(rules, arena, variants[k], BotVariant.Authored), rules, options.MaxSeconds, options.Threads);
        int per = jobs.Count / 2;
        double[] defaultScores = [.. results[..per].Select(r => Score(r, options.TradeWeight))];
        double[] tunedScores = [.. results[per..].Select(r => Score(r, options.TradeWeight))];
        int defaultWins = results[..per].Count(static r => r.Winner == 0);
        int tunedWins = results[per..].Count(static r => r.Winner == 0);
        (double diff, double low, double high) = Paired(tunedScores, defaultScores);

        // Head-to-head against the untuned bot, with the seat-bias control (untuned vs untuned) on the same seeds.
        int[] h2hSeeds = [.. Enumerable.Range(1, options.Seeds * 2)];
        List<TuneJob> h2h = [.. Jobs(1, [VariantAgentFactory.DefaultBot], maps, "heldout", h2hSeeds), .. Jobs(0, [VariantAgentFactory.DefaultBot], maps, "heldout", h2hSeeds)];
        MatchRecord[] h2hResults = Play(h2h, k => new VariantAgentFactory(rules, arena, variants[k], BotVariant.Authored), rules, options.MaxSeconds, options.Threads);
        int h2hPer = h2h.Count / 2;
        int h2hWins = h2hResults[..h2hPer].Count(static r => r.Winner == 0);
        int controlWins = h2hResults[h2hPer..].Count(static r => r.Winner == 0);

        bool adopted = tunedWins >= defaultWins && low > 0 && h2hWins >= controlWins;
        TuningValidation validation = new(
            [.. maps.Select(static m => m.Map.MapId)], options.Opponents, options.Seeds, per, defaultWins, tunedWins,
            defaultScores.Average(), tunedScores.Average(), diff, low, high, h2hWins, h2hPer, controlWins, h2hPer, AdoptionRule, adopted, options.Date);
        TunedParameterSet result = tuned with { Adopted = adopted, Validation = validation };
        string json = result.ToJson() + "\n";
        File.WriteAllText(Path.Combine(options.OutDir, "tuned-validated.json"), json);
        if (options.Write is not null) File.WriteAllText(options.Write, json);

        StringBuilder md = new();
        md.AppendLine("# Held-out validation");
        md.AppendLine();
        md.AppendLine($"Rules `{rules.RulesetId}`; maps {string.Join(", ", validation.Maps)}; opponents {string.Join(", ", options.Opponents)}; seeds 1–{options.Seeds} (head-to-head 1–{options.Seeds * 2}).");
        md.AppendLine();
        md.AppendLine("| Measure | Untuned | Tuned |");
        md.AppendLine("|---|---|---|");
        md.AppendLine($"| Benchmark wins | {Rate(defaultWins, per)} | {Rate(tunedWins, per)} |");
        md.AppendLine($"| Mean fitness | {F(validation.DefaultFitness)} | {F(validation.TunedFitness)} |");
        md.AppendLine($"| Mean trade share | {F(results[..per].Average(TradeShare))} | {F(results[per..].Average(TradeShare))} |");
        md.AppendLine($"| Mean match length (s) | {F(results[..per].Average(static r => r.DurationSeconds), "0")} | {F(results[per..].Average(static r => r.DurationSeconds), "0")} |");
        md.AppendLine($"| vs untuned bot (head-to-head) | {Rate(controlWins, h2hPer)} (control) | {Rate(h2hWins, h2hPer)} |");
        md.AppendLine();
        md.AppendLine($"Paired fitness difference (tuned - untuned): {F(diff)} [{F(low)}, {F(high)}] over {per} matched pairs.");
        md.AppendLine();
        foreach (string opponent in options.Opponents)
        {
            int d = Enumerable.Range(0, per).Count(i => jobs[i].Opponent == opponent && results[i].Winner == 0);
            int t = Enumerable.Range(per, per).Count(i => jobs[i].Opponent == opponent && results[i].Winner == 0);
            int n = Enumerable.Range(0, per).Count(i => jobs[i].Opponent == opponent);
            md.AppendLine($"- {opponent}: untuned {d}/{n}, tuned {t}/{n}");
        }
        md.AppendLine();
        md.AppendLine($"Rule: {AdoptionRule}");
        md.AppendLine();
        md.AppendLine($"Decision: {(adopted ? "ADOPTED" : "NOT ADOPTED")}.");
        File.WriteAllText(Path.Combine(options.OutDir, "validation.md"), md.ToString());
        Console.Write(md.ToString());
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[{wall.Elapsed.TotalSeconds:0}s]"));
    }

    /// <summary>Plays one generation's matches for every variant and returns each variant's mean fitness and win count.</summary>
    private static (double[] Fitness, int[] Wins, int PerCandidate) Evaluate(
        IReadOnlyList<BotVariant> variants, BotVariant champion, TuneOptions options, IRulesDatabase rules, BotAgentFactory arena,
        IReadOnlyList<SimMap> maps, string split, IReadOnlyList<int> seeds)
    {
        List<TuneJob> jobs = [.. Enumerable.Range(0, variants.Count).SelectMany(k => Jobs(k, options.Opponents, maps, split, seeds))];
        MatchRecord[] results = Play(jobs, k => new VariantAgentFactory(rules, arena, variants[k], champion), rules, options.MaxSeconds, options.Threads);
        int per = jobs.Count / variants.Count;
        double[] fitness = new double[variants.Count];
        int[] wins = new int[variants.Count];
        for (int k = 0; k < variants.Count; k++)
        {
            ArraySegment<MatchRecord> own = new(results, k * per, per);
            fitness[k] = own.Average(r => Score(r, options.TradeWeight));
            wins[k] = own.Count(static r => r.Winner == 0);
        }
        return (fitness, wins, per);
    }

    private static IEnumerable<TuneJob> Jobs(int candidate, IReadOnlyList<string> opponents, IReadOnlyList<SimMap> maps, string split, IReadOnlyList<int> seeds)
    {
        foreach (string opponent in opponents)
        {
            foreach (SimMap map in maps)
            {
                foreach (int seed in seeds) yield return new TuneJob(candidate, opponent, map, split, seed);
            }
        }
    }
}
