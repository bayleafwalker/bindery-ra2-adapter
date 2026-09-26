// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Strategy;

/// <summary>Training and escalation settings for <see cref="DistilledStrategist"/>.</summary>
/// <param name="Epochs">Full-batch gradient descent passes.</param>
/// <param name="LearningRate">Step size on the standardised features.</param>
/// <param name="L2">Weight decay (not applied to the intercept).</param>
/// <param name="MaxDistance">
/// Out-of-distribution threshold: the root-mean-square standardised distance of the current vector
/// from the training mean (a Mahalanobis distance with a diagonal covariance). Above it, the decision
/// escalates to the inner strategist.
/// </param>
/// <param name="MinProbability">Escalate when the most likely playbook's probability is below this.</param>
/// <param name="MinExamples">Escalate always while the dataset has fewer examples than this.</param>
public sealed record DistilledOptions(
    int Epochs = 400,
    double LearningRate = 0.5,
    double L2 = 1e-3,
    double MaxDistance = 3.0,
    double MinProbability = 0.0,
    int MinExamples = 10);

/// <summary>
/// A multinomial logistic regression over playbooks, trained deterministically from a
/// <see cref="DecisionDataset"/> (for example the <c>llm</c> arm's decision logs), that
/// escalates to an inner strategist when the current state is out of distribution.
/// </summary>
/// <remarks>
/// Features are standardised with the training mean and standard deviation (a
/// component with zero spread keeps unit scale). Only playbooks serving the current
/// faction compete at inference; if none of the trained classes serves it, the
/// decision escalates. Escalations are counted in <see cref="Escalations"/>, and the
/// escalated proposal is the inner strategist's own, unchanged, so the decision log
/// shows who actually decided.
/// <para>The inner strategist is normally the LLM (the proposal reserves it for unusual states), which answers
/// through a game-time latency wrapper; this strategist forwards <see cref="OnFrame"/> to it, so an escalated
/// request completes on the frame its latency elapses. Being frame-aware also keeps the scheduler from waiting
/// for it inline; the model's own decisions are synchronous and are collected on the frame they are asked.</para>
/// </remarks>
public sealed class DistilledStrategist : IStrategist, Runtime.IFrameAwareStrategist
{
    private readonly IStrategist inner;
    private readonly DistilledOptions options;
    private readonly string[] classes;
    private readonly double[] mean;
    private readonly double[] scale;
    private readonly double[,] weights;
    private readonly int trainedOn;

    public DistilledStrategist(DecisionDataset dataset, IStrategist inner, DistilledOptions? options = null, string id = "distilled")
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(inner);
        this.inner = inner;
        this.options = options ?? new DistilledOptions();
        Id = id;

        int d = FeatureVector.Dimension;
        List<DecisionExample> examples = dataset.Examples
            .Where(static e => e.FeatureVersion == FeatureVector.Version && e.Features.Count == FeatureVector.Dimension)
            .ToList();
        trainedOn = examples.Count;
        classes = [.. examples.Select(static e => e.PlaybookId).Distinct(StringComparer.Ordinal).OrderBy(static c => c, StringComparer.Ordinal)];
        mean = new double[d];
        scale = new double[d];
        weights = new double[classes.Length, d + 1];
        if (examples.Count == 0) return;

        for (int j = 0; j < d; j++)
        {
            double m = examples.Average(e => e.Features[j]);
            double variance = examples.Average(e => (e.Features[j] - m) * (e.Features[j] - m));
            mean[j] = m;
            scale[j] = variance > 1e-12 ? Math.Sqrt(variance) : 1.0;
        }
        Train(examples);
    }

    public string Id { get; }

    public IntentSource Source => IntentSource.Distilled;

    /// <summary>The strategist escalations go to.</summary>
    public IStrategist Inner => inner;

    public void OnFrame(GameTime now)
    {
        if (inner is Runtime.IFrameAwareStrategist aware) aware.OnFrame(now);
    }

    public IReadOnlyList<string> Classes => classes;

    public int TrainedOn => trainedOn;

    public int Decisions { get; private set; }

    public int Escalations { get; private set; }

    /// <summary>Why the most recent decision escalated, or null when the model decided.</summary>
    public string? LastEscalationReason { get; private set; }

    /// <remarks>
    /// Not an <c>async</c> method on purpose: an escalation returns the inner strategist's own task, so it completes
    /// exactly when the inner one does (on its latency frame). Awaiting it here would complete this task in a
    /// thread-pool continuation instead, a moment later, and the frame the scheduler first sees it complete would
    /// depend on thread timing, breaking determinism and replay.
    /// </remarks>
    public Task<StrategistProposal?> ProposeAsync(StrategistContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Decisions++;
        StrategicFeatures features = context.Features;
        double[] x = FeatureVector.Encode(features);
        string? escalate = null;
        (string PlaybookId, double Probability)? best = null;

        if (trainedOn < options.MinExamples)
        {
            escalate = $"dataset has {trainedOn} examples (< {options.MinExamples})";
        }
        else
        {
            double distance = Distance(x);
            if (distance > options.MaxDistance)
            {
                escalate = $"out of distribution: distance {distance:0.00} > {options.MaxDistance:0.00}";
            }
            else
            {
                best = Predict(x, features.Faction, context.Playbooks);
                if (best is null) escalate = "no trained playbook serves this faction";
                else if (best.Value.Probability < options.MinProbability) escalate = $"low confidence {best.Value.Probability:0.00}";
            }
        }

        if (escalate is not null || best is null || !context.Playbooks.TryGet(best.Value.PlaybookId, out Playbook playbook))
        {
            Escalations++;
            LastEscalationReason = escalate ?? "predicted playbook missing from the library";
            return inner.ProposeAsync(context, cancellationToken);
        }

        LastEscalationReason = null;
        StrategicIntent intent = IntentComposer.Compose(
            playbook, features, $"{Id}/{features.SnapshotVersion}", Source, best.Value.Probability,
            StrategyRationale.Explain(playbook.Id, $"distilled: p={best.Value.Probability:0.00} over {classes.Length} playbooks from {trainedOn} examples", features));
        return Task.FromResult<StrategistProposal?>(new StrategistProposal(intent, new ProposalCost(0, 0, 0, 0, null), null));
    }

    /// <summary>RMS standardised distance from the training mean (diagonal Mahalanobis).</summary>
    public double Distance(double[] x)
    {
        ArgumentNullException.ThrowIfNull(x);
        double sum = 0;
        int counted = 0;
        for (int j = 0; j < mean.Length; j++)
        {
            if (j == 0) continue; // bias is constant
            double z = (x[j] - mean[j]) / scale[j];
            sum += z * z;
            counted++;
        }
        return counted == 0 ? 0 : Math.Sqrt(sum / counted);
    }

    /// <summary>Class probabilities for the given vector over all trained playbooks, in <see cref="Classes"/> order.</summary>
    public double[] Probabilities(double[] x)
    {
        ArgumentNullException.ThrowIfNull(x);
        return Softmax(Logits(Standardise(x)), Enumerable.Repeat(true, classes.Length).ToArray());
    }

    private (string, double)? Predict(double[] x, Faction faction, IPlaybookLibrary playbooks)
    {
        bool[] allowed = classes
            .Select(c => playbooks.TryGet(c, out Playbook p) && (p.Factions.Count == 0 || p.Factions.Contains(faction)))
            .ToArray();
        if (!allowed.Any(static a => a)) return null;
        double[] probabilities = Softmax(Logits(Standardise(x)), allowed);
        int best = -1;
        for (int k = 0; k < classes.Length; k++)
        {
            if (allowed[k] && (best < 0 || probabilities[k] > probabilities[best] + 1e-12)) best = k;
        }
        return (classes[best], probabilities[best]);
    }

    private void Train(List<DecisionExample> examples)
    {
        int n = examples.Count, d = mean.Length, k = classes.Length;
        double[][] z = examples.Select(e => Standardise([.. e.Features])).ToArray();
        int[] y = examples.Select(e => Array.IndexOf(classes, e.PlaybookId)).ToArray();
        bool[] all = Enumerable.Repeat(true, k).ToArray();
        double[,] gradient = new double[k, d + 1];
        for (int epoch = 0; epoch < options.Epochs; epoch++)
        {
            Array.Clear(gradient);
            for (int i = 0; i < n; i++)
            {
                double[] p = Softmax(Logits(z[i]), all);
                for (int c = 0; c < k; c++)
                {
                    double error = p[c] - (y[i] == c ? 1 : 0);
                    gradient[c, 0] += error;
                    for (int j = 0; j < d; j++) gradient[c, j + 1] += error * z[i][j];
                }
            }
            for (int c = 0; c < k; c++)
            {
                weights[c, 0] -= options.LearningRate * gradient[c, 0] / n;
                for (int j = 1; j <= d; j++)
                {
                    weights[c, j] -= options.LearningRate * (gradient[c, j] / n + options.L2 * weights[c, j]);
                }
            }
        }
    }

    private double[] Standardise(double[] x)
    {
        double[] z = new double[mean.Length];
        for (int j = 0; j < mean.Length; j++) z[j] = j == 0 ? 0 : (x[j] - mean[j]) / scale[j];
        return z;
    }

    private double[] Logits(double[] z)
    {
        double[] logits = new double[classes.Length];
        for (int c = 0; c < classes.Length; c++)
        {
            double sum = weights[c, 0];
            for (int j = 0; j < z.Length; j++) sum += weights[c, j + 1] * z[j];
            logits[c] = sum;
        }
        return logits;
    }

    private static double[] Softmax(double[] logits, bool[] allowed)
    {
        double max = double.NegativeInfinity;
        for (int i = 0; i < logits.Length; i++) if (allowed[i] && logits[i] > max) max = logits[i];
        double[] p = new double[logits.Length];
        double total = 0;
        for (int i = 0; i < logits.Length; i++)
        {
            p[i] = allowed[i] ? Math.Exp(logits[i] - max) : 0;
            total += p[i];
        }
        if (total > 0) for (int i = 0; i < p.Length; i++) p[i] /= total;
        return p;
    }
}
