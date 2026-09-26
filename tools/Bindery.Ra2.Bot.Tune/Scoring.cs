// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Globalization;
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Sim;

namespace Bindery.Ra2.Bot.Tune;

/// <summary>One match to play: the arm is candidate number <see cref="Candidate"/> of the batch, the other side <see cref="Opponent"/>.</summary>
public sealed record TuneJob(int Candidate, string Opponent, SimMap Map, string Split, int Seed);

/// <summary>Per-match fitness and the statistics the tuner reports.</summary>
public static class Scoring
{
    /// <summary>
    /// A match's fitness: 1 for a win, 0.5 for a draw, 0 for a loss, plus <paramref name="tradeWeight"/> × trade share.
    /// Trade share is destroyed / (destroyed + lost) in [0, 1] (0.5 when neither side lost anything), not the arena's
    /// destroyed / lost ratio: that ratio is unbounded (one lost rifleman against a whole base reads as 40+), so a
    /// search on it would chase the size of blowouts instead of wins.
    /// </summary>
    public static double Score(MatchRecord match, double tradeWeight)
    {
        ArgumentNullException.ThrowIfNull(match);
        double result = match.Winner switch { 0 => 1.0, 1 => 0.0, _ => 0.5 };
        return result + tradeWeight * TradeShare(match);
    }

    public static double TradeShare(MatchRecord match)
    {
        ArgumentNullException.ThrowIfNull(match);
        PlayerMatchMetrics arm = match.Players["arm"];
        double destroyed = arm.AssetValueDestroyedByOpponent;
        double lost = arm.AssetValueLostByPlayer;
        return destroyed + lost <= 0 ? 0.5 : destroyed / (destroyed + lost);
    }

    /// <summary>
    /// Plays every job, in parallel across cores, and returns the records in job order. Each match is self-contained
    /// (own simulation, own agents, seed from the job), so the result does not depend on scheduling or core count.
    /// </summary>
    public static MatchRecord[] Play(IReadOnlyList<TuneJob> jobs, Func<int, IArenaAgentFactory> factoryFor, IRulesDatabase rules, double maxSeconds, int threads)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(factoryFor);
        MatchRecord[] results = new MatchRecord[jobs.Count];
        ConcurrentDictionary<int, IArenaAgentFactory> factories = new();
        ArmSpec arm = new(VariantAgentFactory.CandidateArm, false, false);
        Parallel.For(0, jobs.Count, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
        {
            TuneJob job = jobs[i];
            IArenaAgentFactory factory = factories.GetOrAdd(job.Candidate, factoryFor);
            results[i] = MatchRunner.Run(arm, job.Opponent, job.Map, job.Split, job.Seed, maxSeconds, rules, factory);
        });
        return results;
    }

    /// <summary>Wilson score interval at 95%.</summary>
    public static (double Low, double High) Wilson(int successes, int total)
    {
        if (total == 0) return (0, 1);
        const double z = 1.959964;
        double p = successes / (double)total;
        double denominator = 1 + z * z / total;
        double centre = (p + z * z / (2 * total)) / denominator;
        double half = z * Math.Sqrt(p * (1 - p) / total + z * z / (4.0 * total * total)) / denominator;
        return (Math.Max(0, centre - half), Math.Min(1, centre + half));
    }

    /// <summary>Mean of paired differences with a normal-approximation 95% interval.</summary>
    public static (double Mean, double Low, double High) Paired(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count != b.Count || a.Count == 0) throw new ArgumentException("Paired samples must be non-empty and the same length.");
        double[] d = [.. a.Zip(b, static (x, y) => x - y)];
        double mean = d.Average();
        double variance = d.Length > 1 ? d.Sum(v => (v - mean) * (v - mean)) / (d.Length - 1) : 0;
        double half = 1.959964 * Math.Sqrt(variance / d.Length);
        return (mean, mean - half, mean + half);
    }

    public static string F(double value, string format = "0.0000") => value.ToString(format, CultureInfo.InvariantCulture);

    public static string Rate(int wins, int total)
    {
        (double low, double high) = Wilson(wins, total);
        return $"{wins}/{total}, {F(total == 0 ? 0 : wins / (double)total, "0.000")} [{F(low, "0.000")}, {F(high, "0.000")}]";
    }
}
