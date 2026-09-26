// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arbitration;

namespace Bindery.Ra2.Bot.Strategy;

/// <summary>Tunables for <see cref="ContextualBanditStrategist"/>.</summary>
/// <param name="Alpha">LinUCB exploration width.</param>
/// <param name="Ridge">Ridge prior: each arm's design matrix starts as <c>Ridge · I</c>.</param>
/// <param name="DefendThreatRatio">Base threat at which the bandit hands the decision to <c>generic-defend</c> without learning from it.</param>
public sealed record BanditOptions(double Alpha = 0.6, double Ridge = 1.0, double DefendThreatRatio = 1.3);

/// <summary>
/// LinUCB over the faction's playbooks with the shared <see cref="FeatureVector"/>
/// as context. Each arm (playbook id) keeps <c>A⁻¹</c> (updated by Sherman–Morrison,
/// so no matrix is ever inverted) and <c>b</c>; the chosen arm maximises
/// <c>θᵀx + α·sqrt(xᵀA⁻¹x)</c> with <c>θ = A⁻¹b</c>, ties broken by ordinal playbook id.
/// </summary>
/// <remarks>
/// <para>Learning: <see cref="Observe"/> is the <see cref="IOutcomeLearner"/> update for one
/// decision. The strategist also remembers the decisions it made since the last
/// <see cref="CompleteEpisode"/>, so a harness that only knows the match outcome can credit
/// every decision of that match with it. One instance is meant to live across the matches
/// of an arena run (the spec's "learns across matches within a run"); decisions are
/// deterministic given the same update order.</para>
/// <para>Base defence is not learned: at a base threat ratio of
/// <see cref="BanditOptions.DefendThreatRatio"/> the bandit proposes <c>generic-defend</c> and
/// records nothing, the same guard the selector applies.</para>
/// </remarks>
public sealed class ContextualBanditStrategist : IStrategist, IOutcomeLearner
{
    private readonly BanditOptions options;
    private readonly Dictionary<string, Arm> arms = new(StringComparer.Ordinal);
    private readonly List<(double[] Context, string PlaybookId)> episode = [];
    private readonly object gate = new();

    public ContextualBanditStrategist(BanditOptions? options = null, string id = "bandit")
    {
        this.options = options ?? new BanditOptions();
        Id = id;
    }

    public string Id { get; }

    public IntentSource Source => IntentSource.Bandit;

    /// <summary>Number of reward updates applied so far, across all arms.</summary>
    public int Updates { get; private set; }

    public Task<StrategistProposal?> ProposeAsync(StrategistContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        StrategicFeatures features = context.Features;
        IReadOnlyList<Playbook> candidates = context.Playbooks.For(features.Faction).OrderBy(static p => p.Id, StringComparer.Ordinal).ToList();
        if (candidates.Count == 0) return Task.FromResult<StrategistProposal?>(null);

        Personalities.TryGet(context.Personality, out PersonalityProfile? personality);
        string? preferred = personality is not null && personality.PreferredPlaybook.TryGetValue(features.Faction, out string? p) ? p : null;
        double threat = ConditionEvaluator.BaseThreatRatio(features);
        if (threat >= (personality?.DefendThreatRatio ?? options.DefendThreatRatio) && context.Playbooks.TryGet("generic-defend", out Playbook defend))
        {
            StrategicIntent guard = IntentComposer.Compose(defend, features, $"{Id}/{features.SnapshotVersion}", Source, 0.9,
                StrategyRationale.Explain(defend.Id, $"LinUCB not consulted: base threat ratio {threat:0.00} is at the defence guard", features));
            return Task.FromResult<StrategistProposal?>(new StrategistProposal(guard, new ProposalCost(0, 0, 0, 0, null), null));
        }

        double[] x = FeatureVector.Encode(features);
        Playbook? best = null;
        double bestScore = double.NegativeInfinity, bestMean = 0, bestWidth = 0;
        lock (gate)
        {
            foreach (Playbook playbook in candidates)
            {
                (double mean, double width) = ArmFor(playbook.Id).Score(x);
                // A personality leans the choice toward its playbook without stopping the learner from overruling it.
                double score = mean + options.Alpha * width + (playbook.Id == preferred ? personality!.BanditBonus : 0);
                if (score > bestScore + 1e-12)
                {
                    best = playbook;
                    bestScore = score;
                    bestMean = mean;
                    bestWidth = width;
                }
            }
            episode.Add((x, best!.Id));
        }

        double confidence = Math.Clamp(0.5 + 0.5 * Math.Tanh(bestMean) - 0.2 * Math.Min(1, bestWidth), 0.05, 0.95);
        StrategicIntent intent = IntentComposer.Compose(
            best!, features, $"{Id}/{features.SnapshotVersion}", Source, confidence,
            StrategyRationale.Explain(best!.Id, $"LinUCB: highest upper bound, mean {bestMean:0.000} + {options.Alpha:0.00} × width {bestWidth:0.000} over {candidates.Count} playbooks"
                + (personality is null ? string.Empty : $" (personality {personality.Id}, +{personality.BanditBonus:0.00} to {preferred})"), features),
            parameters: personality?.ScaledParameters(best!));
        return Task.FromResult<StrategistProposal?>(new StrategistProposal(intent, new ProposalCost(0, 0, 0, 0, null), null));
    }

    public void Observe(StrategicFeatures atDecision, StrategicIntent intent, double reward)
    {
        ArgumentNullException.ThrowIfNull(atDecision);
        ArgumentNullException.ThrowIfNull(intent);
        Update(FeatureVector.Encode(atDecision), intent.PlaybookId, reward);
    }

    /// <summary>Updates one arm from an encoded context and a reward in [-1, 1].</summary>
    public void Update(double[] context, string playbookId, double reward)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(playbookId);
        if (context.Length != FeatureVector.Dimension) throw new ArgumentException($"Context has {context.Length} components; expected {FeatureVector.Dimension}.", nameof(context));
        double r = double.IsFinite(reward) ? Math.Clamp(reward, -1, 1) : 0;
        lock (gate)
        {
            ArmFor(playbookId).Update(context, r);
            Updates++;
        }
    }

    /// <summary>Credits every decision since the last call with <paramref name="reward"/> and starts a new episode.</summary>
    public int CompleteEpisode(double reward)
    {
        List<(double[] Context, string PlaybookId)> decisions;
        lock (gate)
        {
            decisions = [.. episode];
            episode.Clear();
        }
        foreach ((double[] context, string playbookId) in decisions) Update(context, playbookId, reward);
        return decisions.Count;
    }

    /// <summary>Forgets the decisions of the current episode without learning from them.</summary>
    public void AbandonEpisode()
    {
        lock (gate) episode.Clear();
    }

    private Arm ArmFor(string playbookId)
    {
        if (!arms.TryGetValue(playbookId, out Arm? arm))
        {
            arm = new Arm(FeatureVector.Dimension, options.Ridge);
            arms[playbookId] = arm;
        }
        return arm;
    }

    private sealed class Arm
    {
        private readonly double[,] inverse;
        private readonly double[] b;
        private readonly int d;

        public Arm(int dimension, double ridge)
        {
            d = dimension;
            inverse = new double[d, d];
            b = new double[d];
            double diagonal = 1.0 / Math.Max(ridge, 1e-6);
            for (int i = 0; i < d; i++) inverse[i, i] = diagonal;
        }

        public (double Mean, double Width) Score(double[] x)
        {
            double[] ax = Multiply(x);
            double mean = 0, quad = 0;
            for (int i = 0; i < d; i++)
            {
                // θ = A⁻¹ b, so θᵀx = bᵀ(A⁻¹x) because A⁻¹ is symmetric.
                mean += b[i] * ax[i];
                quad += x[i] * ax[i];
            }
            return (mean, Math.Sqrt(Math.Max(0, quad)));
        }

        public void Update(double[] x, double reward)
        {
            double[] ax = Multiply(x);
            double denominator = 1.0;
            for (int i = 0; i < d; i++) denominator += x[i] * ax[i];
            for (int i = 0; i < d; i++)
            {
                for (int j = 0; j < d; j++) inverse[i, j] -= ax[i] * ax[j] / denominator;
            }
            for (int i = 0; i < d; i++) b[i] += reward * x[i];
        }

        private double[] Multiply(double[] x)
        {
            double[] result = new double[d];
            for (int i = 0; i < d; i++)
            {
                double sum = 0;
                for (int j = 0; j < d; j++) sum += inverse[i, j] * x[j];
                result[i] = sum;
            }
            return result;
        }
    }
}
