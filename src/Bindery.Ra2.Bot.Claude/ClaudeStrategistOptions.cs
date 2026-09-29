// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Claude;

/// <summary>
/// What the model is allowed to change. <see cref="Strategic"/> chooses and
/// parameterises any playbook of the faction. <see cref="Refine"/> is the
/// <c>llm+fast</c> evaluation arm: a small fast model re-tunes the parameters of
/// the playbook that is already active and nothing else, so it can run at a
/// short cadence without causing strategic churn.
/// </summary>
public enum StrategistMode { Strategic, Refine }

/// <summary>
/// Configuration for <see cref="ClaudeStrategist"/>. Defaults follow the spec:
/// <c>claude-opus-5</c> with adaptive thinking at effort <c>low</c> (the cadence
/// is 10–30 s, so latency matters more than depth), and <c>claude-haiku-4-5</c>
/// for <see cref="StrategistMode.Refine"/>.
/// </summary>
public sealed record ClaudeStrategistOptions
{
    public const string DefaultStrategicModel = "claude-opus-5";

    public const string DefaultRefineModel = "claude-haiku-4-5";

    public StrategistMode Mode { get; init; } = StrategistMode.Strategic;

    /// <summary>
    /// Which intent fields the model's answer may set (<see cref="VocabularyTier"/>); the rest come from the chosen
    /// playbook. Defaults to the tier adopted in the embedded record (<see cref="VocabularyAdoption.Embedded"/>),
    /// which is <see cref="VocabularyTier.Parameters"/> until a live held-out test justifies more.
    /// </summary>
    public VocabularyTier Vocabulary { get; init; } = VocabularyAdoption.Embedded.AdoptedTier;

    /// <summary>Model id; null picks <see cref="DefaultStrategicModel"/> or <see cref="DefaultRefineModel"/> by <see cref="Mode"/>.</summary>
    public string? Model { get; init; }

    /// <summary>
    /// Effort (<c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c>, <c>max</c>).
    /// Omitted automatically for models without effort support (Haiku 4.5).
    /// </summary>
    public string Effort { get; init; } = "low";

    /// <summary>Output token ceiling including thinking. Generous so adaptive thinking never truncates the JSON.</summary>
    public int MaxTokens { get; init; } = 16000;

    /// <summary>Wall-clock bound on one request. A reply later than this is worthless anyway: freshness discards it.</summary>
    public double RequestTimeoutSeconds { get; init; } = 25;

    /// <summary>
    /// Opt into server-side refusal fallbacks (<c>fallbacks: "default"</c>, beta
    /// <c>server-side-fallback-2026-07-01</c>, exposed by SDK 12.50.0 on
    /// <c>client.Beta.Messages</c>). Sent only for models that have a default
    /// fallback configuration (see <see cref="ModelCapabilities.SupportsServerFallbacks"/>).
    /// </summary>
    public bool EnableServerFallbacks { get; init; } = true;

    /// <summary>
    /// Offer the model the enemy-composition and map-control condition metrics (<see cref="ConditionMetrics.Extended"/>)
    /// in the system prompt, the situation's conditionMetrics and the output schema. Off by default so prompts and
    /// schemas are byte-identical to those of earlier runs.
    /// </summary>
    public bool ExtendedConditionMetrics { get; init; }

    /// <summary>Free-text style guidance (e.g. "aggressive, favours early pressure"); passed as data, not instructions.</summary>
    public string? Personality { get; init; }

    /// <summary>Strategist id for decision logs; null derives <c>claude-strategic</c> / <c>claude-refine</c>.</summary>
    public string? StrategistId { get; init; }

    /// <summary>Resolved model id.</summary>
    public string ResolvedModel => Model ?? (Mode == StrategistMode.Refine ? DefaultRefineModel : DefaultStrategicModel);

    /// <summary>Resolved strategist id.</summary>
    public string ResolvedId => StrategistId ?? (Mode == StrategistMode.Refine ? "claude-refine" : "claude-strategic");

    /// <summary>Options for the <c>llm+fast</c> arm.</summary>
    public static ClaudeStrategistOptions ForRefine() => new() { Mode = StrategistMode.Refine };
}
