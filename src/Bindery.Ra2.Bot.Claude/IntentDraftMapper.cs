// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Claude;

/// <summary>Outcome of mapping one draft: an intent, or a stable error code with detail.</summary>
public sealed record IntentMappingResult(StrategicIntent? Intent, string? Error)
{
    public bool Succeeded => Intent is not null;

    public static IntentMappingResult Ok(StrategicIntent intent) => new(intent, null);

    public static IntentMappingResult Fail(string error) => new(null, error);
}

/// <summary>
/// Structural mapping from the wire <see cref="IntentDraft"/> to a
/// <see cref="StrategicIntent"/>. It resolves enum names, collapses parameter
/// pairs into a map and turns the relative expiry into game time. It does NOT
/// judge the intent (unknown playbooks, fog violations, budget sums, parameter
/// ranges): that is <see cref="IIntentValidator"/>'s job, and duplicating it here
/// would let the two drift. A draft that cannot be represented as an intent at
/// all (an enum name that does not exist, a null where the schema demands a
/// value, a duplicated parameter) fails with a <c>map.*</c> code.
/// </summary>
public sealed class IntentDraftMapper
{
    /// <param name="minExpirySeconds">Shortest tenure an intent may ask for; below the strategic cadence it would expire before the next proposal.</param>
    /// <param name="maxExpirySeconds">Longest tenure; bounds how long a stale plan can survive without a replacement.</param>
    public IntentDraftMapper(double minExpirySeconds = 20, double maxExpirySeconds = 600)
    {
        if (!(minExpirySeconds > 0) || !(maxExpirySeconds >= minExpirySeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(minExpirySeconds), "Expiry bounds must satisfy 0 < min <= max.");
        }
        MinExpirySeconds = minExpirySeconds;
        MaxExpirySeconds = maxExpirySeconds;
    }

    public double MinExpirySeconds { get; }

    public double MaxExpirySeconds { get; }

    /// <summary>
    /// Maps a draft proposed against <paramref name="features"/>. The intent is
    /// stamped <see cref="IntentSource.Llm"/>, based on the features' snapshot
    /// version and issued at the features' time, so freshness checks measure
    /// from the evidence the model saw, not from when the reply arrived.
    /// </summary>
    public IntentMappingResult Map(IntentDraft draft, StrategicFeatures features, string intentId)
    {
        if (draft.PlaybookId is null || draft.Posture is null || draft.Budget is null
            || draft.Parameters is null || draft.Objectives is null || draft.Composition is null
            || draft.RegionsOfInterest is null || draft.AttackConditions is null || draft.AbortTriggers is null
            || draft.ReplanTriggers is null || draft.Assumptions is null || draft.Rationale is null)
        {
            return IntentMappingResult.Fail("map.null_field");
        }

        if (!TryParseEnum(draft.Posture, out StrategicPosture posture))
        {
            return IntentMappingResult.Fail($"map.unknown_posture: {draft.Posture}");
        }

        SortedDictionary<string, double> parameters = new(StringComparer.Ordinal);
        foreach (DraftParameter? parameter in draft.Parameters)
        {
            if (parameter?.Name is null)
            {
                return IntentMappingResult.Fail("map.null_field");
            }
            if (!parameters.TryAdd(parameter.Name, parameter.Value))
            {
                return IntentMappingResult.Fail($"map.duplicate_parameter: {parameter.Name}");
            }
        }

        List<Objective> objectives = new(draft.Objectives.Count);
        foreach (DraftObjective? objective in draft.Objectives)
        {
            if (objective?.Kind is null)
            {
                return IntentMappingResult.Fail("map.null_field");
            }
            if (!TryParseEnum(objective.Kind, out ObjectiveKind kind))
            {
                return IntentMappingResult.Fail($"map.unknown_objective_kind: {objective.Kind}");
            }
            objectives.Add(new Objective(kind, ToRegion(objective.RegionId), objective.TypeId, objective.Priority));
        }

        List<CompositionTarget> composition = new(draft.Composition.Count);
        foreach (DraftComposition? target in draft.Composition)
        {
            if (target?.Role is null)
            {
                return IntentMappingResult.Fail("map.null_field");
            }
            if (!TryParseEnum(target.Role, out UnitRole role))
            {
                return IntentMappingResult.Fail($"map.unknown_role: {target.Role}");
            }
            composition.Add(new CompositionTarget(role, target.MinShare, target.MaxShare));
        }

        if (!TryMapConditions(draft.AttackConditions, out List<Condition> attack, out string? error)
            || !TryMapConditions(draft.AbortTriggers, out List<Condition> abort, out error)
            || !TryMapConditions(draft.ReplanTriggers, out List<Condition> replan, out error))
        {
            return IntentMappingResult.Fail(error!);
        }

        foreach (string? assumption in draft.Assumptions)
        {
            if (assumption is null)
            {
                return IntentMappingResult.Fail("map.null_field");
            }
        }

        GameTime issuedAt = features.Time;
        double expiry = Math.Clamp(draft.ExpiresInSeconds, MinExpirySeconds, MaxExpirySeconds);
        StrategicIntent intent = new(
            IntentId: intentId,
            Source: IntentSource.Llm,
            BasedOnSnapshotVersion: features.SnapshotVersion,
            IssuedAt: issuedAt,
            ExpiresAt: issuedAt.Plus(expiry),
            Posture: posture,
            PlaybookId: draft.PlaybookId,
            PlaybookParameters: parameters,
            Objectives: objectives,
            Budget: new BudgetShares(draft.Budget.Economy, draft.Budget.Army, draft.Budget.Tech, draft.Budget.Defense),
            Composition: composition,
            RegionsOfInterest: draft.RegionsOfInterest.Select(r => new RegionId(r)).ToList(),
            AttackConditions: attack,
            AbortTriggers: abort,
            ReplanTriggers: replan,
            Confidence: draft.Confidence,
            Assumptions: draft.Assumptions.ToList(),
            Rationale: draft.Rationale);
        return IntentMappingResult.Ok(intent);
    }

    /// <summary>
    /// Exact, case-sensitive match on a declared member name. Numeric strings
    /// (<c>"3"</c>) are rejected even though <see cref="Enum.TryParse{TEnum}(string, out TEnum)"/>
    /// would accept them: the schema only allows names.
    /// </summary>
    internal static bool TryParseEnum<TEnum>(string value, out TEnum result)
        where TEnum : struct, Enum
    {
        foreach (TEnum candidate in Enum.GetValues<TEnum>())
        {
            if (string.Equals(candidate.ToString(), value, StringComparison.Ordinal))
            {
                result = candidate;
                return true;
            }
        }
        result = default;
        return false;
    }

    private static RegionId? ToRegion(int? region) => region is { } value ? new RegionId(value) : null;

    private static bool TryMapConditions(IReadOnlyList<DraftCondition> source, out List<Condition> conditions, out string? error)
    {
        conditions = new List<Condition>(source.Count);
        foreach (DraftCondition? condition in source)
        {
            if (condition?.Metric is null || condition.Op is null)
            {
                error = "map.null_field";
                return false;
            }
            if (!TryParseEnum(condition.Metric, out ConditionMetric metric))
            {
                error = $"map.unknown_metric: {condition.Metric}";
                return false;
            }
            if (!TryParseEnum(condition.Op, out Comparison op))
            {
                error = $"map.unknown_comparison: {condition.Op}";
                return false;
            }
            conditions.Add(new Condition(metric, op, condition.Threshold, ToRegion(condition.RegionId)));
        }
        error = null;
        return true;
    }
}
