// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Tuning;

/// <summary>
/// SplitMix64 with Box–Muller normals: a tiny, fully specified generator so a seeded search draws the same
/// numbers on every runtime version (<see cref="System.Random"/>'s algorithm is an implementation detail).
/// </summary>
public sealed class SeededNormal(ulong seed)
{
    private ulong state = seed;
    private double? spare;

    public ulong NextUInt64()
    {
        ulong z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform in (0, 1): 53 random bits, offset by half a step so 0 is never returned (log(0) in Box–Muller).</summary>
    public double NextUnit() => ((NextUInt64() >> 11) + 0.5) * (1.0 / (1UL << 53));

    public double NextGaussian()
    {
        if (spare is { } s)
        {
            spare = null;
            return s;
        }
        double r = Math.Sqrt(-2.0 * Math.Log(NextUnit()));
        double theta = 2.0 * Math.PI * NextUnit();
        spare = r * Math.Sin(theta);
        return r * Math.Cos(theta);
    }
}

/// <summary>Per-generation summary, in the order generations ran.</summary>
public sealed record GenerationSummary(int Generation, double BestFitness, double MedianFitness, double WorstFitness, double Sigma, int BestIndex);

/// <summary>
/// Separable CMA-ES (Ros &amp; Hansen 2008) maximising over the unit hypercube: a (μ/μ_w, λ)-ES with a diagonal
/// covariance and cumulative step-size adaptation. Diagonal rather than full covariance because the budget
/// allows a few hundred evaluations at most, far too few to learn an n² covariance, while per-coordinate
/// scaling still matters (knob sensitivities differ by orders of magnitude). Bounds are handled by
/// reflecting samples into [0, 1] and updating from the reflected points, so every point the caller
/// evaluates is inside the declared ranges and the update learns from what was actually evaluated.
/// </summary>
/// <remarks>
/// Deterministic: all randomness comes from <see cref="SeededNormal"/> seeded at construction, samples are drawn
/// in index order, and fitness ties are broken by sample index. Call <see cref="Ask"/> then <see cref="Tell"/>
/// alternately.
/// </remarks>
public sealed class EvolutionStrategy
{
    private readonly int n;
    private readonly SeededNormal rng;
    private readonly double[] weights;
    private readonly double muEff;
    private readonly double cSigma;
    private readonly double dSigma;
    private readonly double cc;
    private readonly double c1;
    private readonly double cMu;
    private readonly double chiN;
    private readonly double[] mean;
    private readonly double[] diag;
    private readonly double[] pSigma;
    private readonly double[] pc;
    private double[][]? pending;
    private readonly List<GenerationSummary> history = [];

    /// <param name="initialMean">Starting point in [0, 1]^n (e.g. the authored defaults).</param>
    /// <param name="sigma">Initial step size in unit-interval coordinates.</param>
    /// <param name="lambda">Offspring per generation (≥ 2).</param>
    /// <param name="mu">Parents recombined per generation (1 ≤ μ ≤ λ); λ/2 when null.</param>
    public EvolutionStrategy(IReadOnlyList<double> initialMean, double sigma, int lambda, ulong seed, int? mu = null)
    {
        ArgumentNullException.ThrowIfNull(initialMean);
        if (initialMean.Count == 0) throw new ArgumentException("The search space is empty.", nameof(initialMean));
        if (lambda < 2) throw new ArgumentOutOfRangeException(nameof(lambda), "λ must be at least 2.");
        if (!(sigma > 0)) throw new ArgumentOutOfRangeException(nameof(sigma));
        int parents = mu ?? lambda / 2;
        if (parents < 1 || parents > lambda) throw new ArgumentOutOfRangeException(nameof(mu));

        n = initialMean.Count;
        Lambda = lambda;
        Mu = parents;
        Sigma = sigma;
        rng = new SeededNormal(seed);
        mean = [.. initialMean.Select(static v => Math.Clamp(v, 0, 1))];
        diag = [.. Enumerable.Repeat(1.0, n)];
        pSigma = new double[n];
        pc = new double[n];

        double[] raw = [.. Enumerable.Range(1, Mu).Select(i => Math.Log(Mu + 0.5) - Math.Log(i))];
        double sum = raw.Sum();
        weights = [.. raw.Select(w => w / sum)];
        muEff = 1.0 / weights.Sum(static w => w * w);

        cSigma = (muEff + 2) / (n + muEff + 5);
        dSigma = 1 + 2 * Math.Max(0, Math.Sqrt((muEff - 1) / (n + 1)) - 1) + cSigma;
        cc = (4 + muEff / n) / (n + 4 + 2 * muEff / n);
        double c1Full = 2 / ((n + 1.3) * (n + 1.3) + muEff);
        double cMuFull = Math.Min(1 - c1Full, 2 * (muEff - 2 + 1 / muEff) / ((n + 2) * (n + 2) + muEff));
        // Separable learning rates are (n + 2) / 3 times the full-covariance ones (Ros & Hansen, eq. 5).
        c1 = Math.Min(1, c1Full * (n + 2) / 3.0);
        cMu = Math.Min(1 - c1, cMuFull * (n + 2) / 3.0);
        chiN = Math.Sqrt(n) * (1 - 1.0 / (4 * n) + 1.0 / (21.0 * n * n));
    }

    public int Lambda { get; }

    public int Mu { get; }

    public double Sigma { get; private set; }

    public int Generation { get; private set; }

    public IReadOnlyList<double> Mean => mean;

    /// <summary>Per-coordinate standard deviation of the next samples (σ·√D).</summary>
    public IReadOnlyList<double> StandardDeviations => [.. diag.Select(d => Sigma * Math.Sqrt(d))];

    public IReadOnlyList<GenerationSummary> History => history;

    /// <summary>Best point told so far and its fitness (first seen wins ties).</summary>
    public (double[] Point, double Fitness)? Best { get; private set; }

    /// <summary>Draws this generation's λ points, all inside [0, 1]^n.</summary>
    public IReadOnlyList<double[]> Ask()
    {
        if (pending is not null) throw new InvalidOperationException("Ask called twice without Tell.");
        double[][] points = new double[Lambda][];
        for (int k = 0; k < Lambda; k++)
        {
            double[] x = new double[n];
            for (int j = 0; j < n; j++) x[j] = Reflect(mean[j] + Sigma * Math.Sqrt(diag[j]) * rng.NextGaussian());
            points[k] = x;
        }
        pending = points;
        return [.. points.Select(static p => (double[])p.Clone())];
    }

    /// <summary>Updates the distribution from the fitness (higher is better) of the points <see cref="Ask"/> returned, in order.</summary>
    public void Tell(IReadOnlyList<double> fitness)
    {
        ArgumentNullException.ThrowIfNull(fitness);
        double[][] points = pending ?? throw new InvalidOperationException("Tell called before Ask.");
        if (fitness.Count != Lambda) throw new ArgumentException($"Expected {Lambda} fitness values, got {fitness.Count}.", nameof(fitness));
        if (fitness.Any(static f => double.IsNaN(f))) throw new ArgumentException("Fitness contains NaN.", nameof(fitness));
        pending = null;

        int[] order = [.. Enumerable.Range(0, Lambda).OrderByDescending(i => fitness[i]).ThenBy(static i => i)];
        if (Best is null || fitness[order[0]] > Best.Value.Fitness) Best = ((double[])points[order[0]].Clone(), fitness[order[0]]);

        // y_i = (x_i - m) / σ, from the reflected points actually evaluated.
        double[][] y = [.. order.Take(Mu).Select(i => points[i].Select((v, j) => (v - mean[j]) / Sigma).ToArray())];
        double[] yw = new double[n];
        for (int r = 0; r < Mu; r++)
        {
            for (int j = 0; j < n; j++) yw[j] += weights[r] * y[r][j];
        }

        for (int j = 0; j < n; j++) mean[j] = Math.Clamp(mean[j] + Sigma * yw[j], 0, 1);

        double sigmaNorm = 0;
        double scale = Math.Sqrt(cSigma * (2 - cSigma) * muEff);
        for (int j = 0; j < n; j++)
        {
            pSigma[j] = (1 - cSigma) * pSigma[j] + scale * yw[j] / Math.Sqrt(diag[j]);
            sigmaNorm += pSigma[j] * pSigma[j];
        }
        sigmaNorm = Math.Sqrt(sigmaNorm);

        Generation++;
        double hSigmaBound = (1.4 + 2.0 / (n + 1)) * chiN * Math.Sqrt(1 - Math.Pow(1 - cSigma, 2 * Generation));
        double hSigma = sigmaNorm < hSigmaBound ? 1 : 0;
        double ccScale = Math.Sqrt(cc * (2 - cc) * muEff);
        for (int j = 0; j < n; j++)
        {
            pc[j] = (1 - cc) * pc[j] + hSigma * ccScale * yw[j];
            double rankMu = 0;
            for (int r = 0; r < Mu; r++) rankMu += weights[r] * y[r][j] * y[r][j];
            double correction = (1 - hSigma) * cc * (2 - cc);
            diag[j] = Math.Max(1e-12, (1 - c1 - cMu) * diag[j] + c1 * (pc[j] * pc[j] + correction * diag[j]) + cMu * rankMu);
        }

        // Kept within [1e-4, 0.5]: beyond half the unit interval every sample reflects and the search is a random walk.
        Sigma = Math.Clamp(Sigma * Math.Exp(cSigma / dSigma * (sigmaNorm / chiN - 1)), 1e-4, 0.5);

        double[] sorted = [.. fitness.OrderBy(static f => f)];
        history.Add(new GenerationSummary(Generation, sorted[^1], Median(sorted), sorted[0], Sigma, order[0]));
    }

    /// <summary>Folds a coordinate back into [0, 1] (mirror at each bound).</summary>
    public static double Reflect(double v)
    {
        if (v is >= 0 and <= 1) return v;
        double m = v % 2.0;
        if (m < 0) m += 2.0;
        return m <= 1 ? m : 2 - m;
    }

    private static double Median(double[] sorted) =>
        sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
}
