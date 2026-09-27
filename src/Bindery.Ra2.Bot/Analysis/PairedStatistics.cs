// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Analysis;

/// <summary>One metric compared pair by pair (same opponent, map and seed) between a baseline and another arm.</summary>
/// <param name="Pairs">Matched pairs.</param>
/// <param name="MeanDifference">Mean of (arm − baseline).</param>
/// <param name="CiLow">Lower end of the 95% percentile-bootstrap interval of the mean difference.</param>
/// <param name="CiHigh">Upper end of that interval.</param>
/// <param name="Better">Pairs where the arm did better (by the metric's direction).</param>
/// <param name="Worse">Pairs where the arm did worse.</param>
/// <param name="Ties">Pairs with no difference.</param>
/// <param name="SignTestP">Exact two-sided sign test over the non-tied pairs.</param>
public sealed record PairedDifference(
    string Metric,
    int Pairs,
    double BaselineMean,
    double ArmMean,
    double MeanDifference,
    double CiLow,
    double CiHigh,
    int Better,
    int Worse,
    int Ties,
    double SignTestP);

/// <summary>
/// Paired, significance-tested comparisons for the arena and the adoption rules. Deliberately distribution-free:
/// match metrics are bounded, skewed and often tied (a win is 0, ½ or 1), so the test is the exact sign test on
/// the direction of each pair, and the interval is a seeded percentile bootstrap of the mean difference.
/// Everything is deterministic.
/// </summary>
public static class PairedStatistics
{
    public const int DefaultBootstrapSamples = 2000;

    /// <summary>Compares paired values; <paramref name="higherIsBetter"/> sets what counts as better.</summary>
    public static PairedDifference Compare(
        string metric,
        IReadOnlyList<(double Baseline, double Arm)> pairs,
        bool higherIsBetter,
        int bootstrapSamples = DefaultBootstrapSamples,
        ulong seed = 0x5EED)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        if (pairs.Count == 0) return new PairedDifference(metric, 0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, 0, 0, 0, 1.0);
        double[] diffs = [.. pairs.Select(static p => p.Arm - p.Baseline)];
        int better = 0, worse = 0, ties = 0;
        foreach (double d in diffs)
        {
            if (Math.Abs(d) < 1e-12) ties++;
            else if (d > 0 == higherIsBetter) better++;
            else worse++;
        }
        (double low, double high) = BootstrapMeanCi(diffs, bootstrapSamples, seed);
        return new PairedDifference(
            metric,
            pairs.Count,
            pairs.Average(static p => p.Baseline),
            pairs.Average(static p => p.Arm),
            diffs.Average(),
            low,
            high,
            better,
            worse,
            ties,
            SignTestTwoSided(better, worse));
    }

    /// <summary>Exact two-sided sign test: P(a split at least this uneven | no difference), from the binomial(n, ½).</summary>
    public static double SignTestTwoSided(int better, int worse)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(better);
        ArgumentOutOfRangeException.ThrowIfNegative(worse);
        int n = better + worse;
        if (n == 0) return 1.0;
        int k = Math.Min(better, worse);
        // Sum C(n, i) / 2^n for i ≤ k in log space so large n neither overflows nor underflows to garbage.
        double logHalfN = n * Math.Log(0.5);
        double logC = 0; // log C(n, 0)
        double tail = Math.Exp(logC + logHalfN);
        for (int i = 1; i <= k; i++)
        {
            logC += Math.Log(n - i + 1) - Math.Log(i);
            tail += Math.Exp(logC + logHalfN);
        }
        return Math.Min(1.0, 2 * tail);
    }

    /// <summary>Holm's step-down adjustment (family-wise error control), in the input order.</summary>
    public static IReadOnlyList<double> HolmAdjust(IReadOnlyList<double> pValues)
    {
        ArgumentNullException.ThrowIfNull(pValues);
        int m = pValues.Count;
        int[] order = [.. Enumerable.Range(0, m).OrderBy(i => pValues[i]).ThenBy(static i => i)];
        double[] adjusted = new double[m];
        double running = 0;
        for (int rank = 0; rank < m; rank++)
        {
            int i = order[rank];
            running = Math.Max(running, Math.Min(1.0, (m - rank) * pValues[i]));
            adjusted[i] = Math.Round(running, 12);
        }
        return adjusted;
    }

    /// <summary>95% percentile bootstrap interval of the mean, from a seeded xorshift64* stream.</summary>
    public static (double Low, double High) BootstrapMeanCi(IReadOnlyList<double> values, int samples = DefaultBootstrapSamples, ulong seed = 0x5EED)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) return (double.NaN, double.NaN);
        if (values.Count == 1 || samples <= 0) return (values[0], values[0]);
        ulong state = seed ^ 0x9E3779B97F4A7C15UL;
        if (state == 0) state = 0x9E3779B97F4A7C15UL;
        double[] means = new double[samples];
        int n = values.Count;
        for (int s = 0; s < samples; s++)
        {
            double sum = 0;
            for (int j = 0; j < n; j++)
            {
                state ^= state >> 12;
                state ^= state << 25;
                state ^= state >> 27;
                ulong r = state * 0x2545F4914F6CDD1DUL;
                sum += values[(int)((r >> 11) * (1.0 / (1UL << 53)) * n)];
            }
            means[s] = sum / n;
        }
        Array.Sort(means);
        return (means[(int)Math.Floor(0.025 * (samples - 1))], means[(int)Math.Ceiling(0.975 * (samples - 1))]);
    }
}
