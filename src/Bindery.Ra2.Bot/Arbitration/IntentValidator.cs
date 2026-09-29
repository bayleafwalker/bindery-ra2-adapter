// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Arbitration;

/// <summary>
/// The single gate between any strategist and the planner (invariant 5). It
/// checks a proposal against the <em>current</em> context, rejects what is
/// illegal, stale or fog-violating with stable codes, and repairs what is merely
/// sloppy (clamped parameters, normalised budget, playbook defaults) with a
/// warning for every change, so the planner only ever sees a complete, legal intent.
/// </summary>
/// <remarks>
/// All checks run and all issues are reported (no early exit), except that
/// playbook-dependent checks are skipped when the playbook is unknown.
/// Region policy: the map is public knowledge, so a region that was never seen is
/// fine; only ids absent from <see cref="MapInfo.Regions"/> are rejected
/// (<c>region.unknown</c> for objectives and condition regions, <c>fog.region</c>
/// for <see cref="StrategicIntent.RegionsOfInterest"/>, which is where a strategist
/// would express knowledge about places). Type policy: an objective type that is not
/// buildable by the own faction and is absent from <see cref="EnemyFeatures.KnownTech"/>
/// is information the player cannot have (<c>fog.unknown_type</c>).
/// </remarks>
public sealed class IntentValidator : IIntentValidator
{
    private const double Epsilon = 1e-9;

    public IntentValidator(ValidatorOptions? options = null)
    {
        Options = options ?? ValidatorOptions.Default;
    }

    public ValidatorOptions Options { get; }

    public ValidationResult Validate(StrategicIntent proposal, ValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(context);
        List<ValidationIssue> issues = [];
        StrategicFeatures current = context.Current;
        GameTime now = current.Time;

        if (string.IsNullOrWhiteSpace(proposal.IntentId))
        {
            Reject(issues, ValidationCodes.IntentId, $"Intent id is empty.");
        }

        // Freshness (invariant 2).
        if (proposal.BasedOnSnapshotVersion > current.SnapshotVersion || proposal.IssuedAt > now)
        {
            Reject(issues, ValidationCodes.SnapshotFuture,
                $"Proposal claims snapshot {proposal.BasedOnSnapshotVersion} at frame {proposal.IssuedAt.Frame}, current is {current.SnapshotVersion} at frame {now.Frame}.");
        }
        double age = now.SecondsSince(proposal.IssuedAt);
        if (age > Options.MaxProposalAgeSeconds)
        {
            Reject(issues, ValidationCodes.StaleSnapshot,
                $"Proposal is {age:0.###} s old (limit {Options.MaxProposalAgeSeconds:0.###} s).");
        }
        foreach (StrategicEvent evt in current.Events)
        {
            if (evt.Severity >= Options.StaleEventSeverity && evt.Time > proposal.IssuedAt)
            {
                Reject(issues, ValidationCodes.StaleEvent,
                    $"{evt.Kind} (severity {evt.Severity:0.###}) at frame {evt.Time.Frame} happened after the proposal was issued.");
                break;
            }
        }

        // Expiry.
        GameTime expiresAt = proposal.ExpiresAt;
        if (expiresAt <= now)
        {
            Reject(issues, ValidationCodes.ExpiryPast, $"Intent expired at frame {expiresAt.Frame}, now is frame {now.Frame}.");
        }
        else if (expiresAt.SecondsSince(proposal.IssuedAt) > Options.MaxExpirySeconds + Epsilon)
        {
            expiresAt = proposal.IssuedAt.Plus(Options.MaxExpirySeconds);
            Warn(issues, ValidationCodes.ExpiryTooLong,
                $"Expiry capped to {Options.MaxExpirySeconds:0.###} s after issue (frame {expiresAt.Frame}).");
            if (expiresAt <= now)
            {
                Reject(issues, ValidationCodes.ExpiryPast, $"Capped expiry frame {expiresAt.Frame} is not after now (frame {now.Frame}).");
            }
        }

        // Confidence.
        double confidence = proposal.Confidence;
        if (!double.IsFinite(confidence))
        {
            Reject(issues, ValidationCodes.ConfidenceRange, $"Confidence is not a finite number.");
        }
        else if (confidence < 0 || confidence > 1)
        {
            confidence = Math.Clamp(confidence, 0, 1);
            Warn(issues, ValidationCodes.ConfidenceRange, $"Confidence {proposal.Confidence} clamped to {confidence}.");
        }

        // Playbook.
        Playbook? playbook = null;
        if (!context.Playbooks.TryGet(proposal.PlaybookId, out Playbook found))
        {
            Reject(issues, ValidationCodes.PlaybookUnknown, $"Playbook '{proposal.PlaybookId}' does not exist.");
        }
        else
        {
            playbook = found;
            if (playbook.Factions.Count > 0 && !playbook.Factions.Contains(current.Faction))
            {
                Reject(issues, ValidationCodes.PlaybookFaction, $"Playbook '{playbook.Id}' is not available to {current.Faction}.");
            }
        }

        BudgetShares budget = CheckBudget(proposal.Budget, playbook, issues);
        IReadOnlyList<CompositionTarget> composition = CheckComposition(proposal.Composition, playbook, issues);
        IReadOnlyDictionary<string, double> parameters = CheckParameters(proposal.PlaybookParameters, playbook, issues);

        HashSet<RegionId> mapRegions = context.Belief.Map.Regions.Select(static r => r.Id).ToHashSet();
        CheckObjectives(proposal.Objectives, context, mapRegions, issues);
        foreach (RegionId region in proposal.RegionsOfInterest)
        {
            if (!mapRegions.Contains(region))
            {
                Reject(issues, ValidationCodes.FogRegion, $"Region of interest {region} is not on the map.");
            }
        }

        IReadOnlyList<Condition> attack = FillConditions(proposal.AttackConditions, playbook?.AttackConditions, "attack conditions", issues);
        IReadOnlyList<Condition> abort = FillConditions(proposal.AbortTriggers, playbook?.AbortTriggers, "abort triggers", issues);
        CheckConditions(attack, mapRegions, "attack condition", issues);
        CheckConditions(abort, mapRegions, "abort trigger", issues);
        CheckConditions(proposal.ReplanTriggers, mapRegions, "replan trigger", issues);

        bool accepted = issues.All(static i => i.Severity != ValidationSeverity.Reject);
        if (!accepted) return new ValidationResult(false, null, issues);

        StrategicIntent sanitised = proposal with
        {
            ExpiresAt = expiresAt,
            Confidence = confidence,
            Budget = budget,
            Composition = composition,
            PlaybookParameters = parameters,
            AttackConditions = attack,
            AbortTriggers = abort,
        };
        return new ValidationResult(true, sanitised, issues);
    }

    private BudgetShares CheckBudget(BudgetShares budget, Playbook? playbook, List<ValidationIssue> issues)
    {
        double[] shares = [budget.Economy, budget.Army, budget.Tech, budget.Defense];
        if (shares.Any(static s => !double.IsFinite(s) || s < 0))
        {
            Reject(issues, ValidationCodes.BudgetNegative, $"Budget shares must be finite and non-negative: economy {budget.Economy}, army {budget.Army}, tech {budget.Tech}, defense {budget.Defense}.");
            return budget;
        }
        double sum = budget.Sum;
        if (sum == 0 && playbook is not null)
        {
            Warn(issues, ValidationCodes.DefaultFilled, $"Empty budget replaced by playbook '{playbook.Id}' budget.");
            return playbook.Budget;
        }
        double off = Math.Abs(sum - 1);
        if (off <= Options.BudgetSumTolerance + Epsilon) return budget;
        if (off <= Options.BudgetNormaliseWindow + Epsilon && sum > 0)
        {
            Warn(issues, ValidationCodes.BudgetSum, $"Budget shares summed to {sum:0.####}; normalised to 1.");
            return new BudgetShares(budget.Economy / sum, budget.Army / sum, budget.Tech / sum, budget.Defense / sum);
        }
        Reject(issues, ValidationCodes.BudgetSum, $"Budget shares sum to {sum:0.####}, too far from 1.");
        return budget;
    }

    internal static IReadOnlyList<CompositionTarget> CheckComposition(IReadOnlyList<CompositionTarget> composition, Playbook? playbook, List<ValidationIssue> issues)
    {
        if (composition.Count == 0)
        {
            if (playbook is not null && playbook.Composition.Count > 0)
            {
                Warn(issues, ValidationCodes.DefaultFilled, $"Composition taken from playbook '{playbook.Id}'.");
                return playbook.Composition;
            }
            return composition;
        }
        HashSet<UnitRole> seen = [];
        double minSum = 0;
        foreach (CompositionTarget target in composition)
        {
            if (!seen.Add(target.Role))
            {
                Reject(issues, ValidationCodes.CompositionDuplicate, $"Role {target.Role} appears more than once.");
            }
            if (!double.IsFinite(target.MinShare) || !double.IsFinite(target.MaxShare)
                || target.MinShare < 0 || target.MaxShare > 1 || target.MinShare > target.MaxShare)
            {
                Reject(issues, ValidationCodes.CompositionRange,
                    $"Role {target.Role} range [{target.MinShare}, {target.MaxShare}] is not a range within [0, 1].");
                continue;
            }
            minSum += target.MinShare;
        }
        if (minSum > 1 + Epsilon)
        {
            Reject(issues, ValidationCodes.CompositionRange, $"Minimum shares sum to {minSum:0.####} > 1, which no army satisfies.");
        }
        return composition;
    }

    private static IReadOnlyDictionary<string, double> CheckParameters(IReadOnlyDictionary<string, double> given, Playbook? playbook, List<ValidationIssue> issues)
    {
        SortedDictionary<string, double> result = new(StringComparer.Ordinal);
        if (playbook is null)
        {
            foreach (KeyValuePair<string, double> pair in given) result[pair.Key] = pair.Value;
            return result;
        }
        Dictionary<string, PlaybookParameter> declared = new(StringComparer.Ordinal);
        foreach (PlaybookParameter parameter in playbook.Parameters) declared[parameter.Name] = parameter;

        foreach (string name in given.Keys.OrderBy(static k => k, StringComparer.Ordinal))
        {
            double value = given[name];
            if (!declared.TryGetValue(name, out PlaybookParameter? parameter))
            {
                Warn(issues, ValidationCodes.ParamUnknown, $"Parameter '{name}' is not declared by playbook '{playbook.Id}'; dropped.");
                continue;
            }
            if (double.IsNaN(value))
            {
                Warn(issues, ValidationCodes.ParamClamped, $"Parameter '{name}' was NaN; default {parameter.Default} used.");
                value = parameter.Default;
            }
            else if (value < parameter.Min || value > parameter.Max)
            {
                double clamped = Math.Clamp(value, parameter.Min, parameter.Max);
                Warn(issues, ValidationCodes.ParamClamped, $"Parameter '{name}' = {value} clamped to {clamped} (range [{parameter.Min}, {parameter.Max}]).");
                value = clamped;
            }
            result[name] = value;
        }

        List<string> filled = [];
        foreach (PlaybookParameter parameter in playbook.Parameters.OrderBy(static p => p.Name, StringComparer.Ordinal))
        {
            if (result.ContainsKey(parameter.Name)) continue;
            result[parameter.Name] = parameter.Default;
            filled.Add(parameter.Name);
        }
        if (filled.Count > 0)
        {
            Warn(issues, ValidationCodes.DefaultFilled, $"Parameters defaulted from playbook: {string.Join(", ", filled)}.");
        }
        return result;
    }

    private static void CheckObjectives(IReadOnlyList<Objective> objectives, ValidationContext context, HashSet<RegionId> mapRegions, List<ValidationIssue> issues)
    {
        Faction faction = context.Current.Faction;
        IReadOnlySet<string>? owned = null;
        foreach (Objective objective in objectives)
        {
            if (objective.Region is RegionId region)
            {
                if (!mapRegions.Contains(region))
                {
                    Reject(issues, ValidationCodes.RegionUnknown, $"{objective.Kind} objective names region {region}, which is not on the map.");
                }
            }
            else if (NeedsRegion(objective.Kind))
            {
                Reject(issues, ValidationCodes.ObjectiveRegionRequired, $"{objective.Kind} objective needs a region.");
            }

            if (objective.Kind == ObjectiveKind.TechTo && string.IsNullOrWhiteSpace(objective.TypeId))
            {
                Reject(issues, ValidationCodes.ObjectiveTypeRequired, $"TechTo objective needs a type id.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(objective.TypeId)) continue;

            string typeId = objective.TypeId;
            if (!context.Rules.TryGet(typeId, out UnitRule rule))
            {
                Reject(issues, ValidationCodes.TypeUnknown, $"{objective.Kind} objective names type '{typeId}', which the rules do not define.");
                continue;
            }
            bool ownFactionType = rule.Factions.Contains(faction);
            if (!ownFactionType && !context.Current.Enemy.KnownTech.Contains(typeId))
            {
                Reject(issues, ValidationCodes.FogUnknownType,
                    $"{objective.Kind} objective names enemy type '{typeId}', which has not been observed.");
                continue;
            }
            if (objective.Kind == ObjectiveKind.TechTo)
            {
                owned ??= context.Belief.OwnBuildingTypes;
                if (!ownFactionType || context.Rules.PathTo(faction, owned, typeId) is null)
                {
                    Reject(issues, ValidationCodes.TechUnreachable, $"'{typeId}' is not reachable for {faction}.");
                }
            }
        }
    }

    /// <summary>
    /// Objectives that are meaningless without a place. Expand, Scout, TechTo and
    /// Retreat have sensible defaults (best expansion candidate, stalest region,
    /// no place, own base) and so may omit the region.
    /// </summary>
    private static bool NeedsRegion(ObjectiveKind kind) =>
        kind is ObjectiveKind.DefendRegion or ObjectiveKind.AttackRegion or ObjectiveKind.Harass or ObjectiveKind.DenyExpansion;

    private static IReadOnlyList<Condition> FillConditions(IReadOnlyList<Condition> given, IReadOnlyList<Condition>? defaults, string what, List<ValidationIssue> issues)
    {
        if (given.Count > 0 || defaults is null || defaults.Count == 0) return given;
        Warn(issues, ValidationCodes.DefaultFilled, $"Empty {what} taken from playbook.");
        return defaults;
    }

    /// <param name="mapRegions">The map's regions, or null to skip the region-on-map check (a playbook checked at load time has no map).</param>
    internal static void CheckConditions(IReadOnlyList<Condition> conditions, HashSet<RegionId>? mapRegions, string what, List<ValidationIssue> issues)
    {
        foreach (Condition condition in conditions)
        {
            if (double.IsNaN(condition.Threshold))
            {
                Reject(issues, ValidationCodes.ConditionThreshold, $"{what} on {condition.Metric} has a NaN threshold.");
            }
            if (condition.Region is RegionId region)
            {
                if (mapRegions is not null && !mapRegions.Contains(region))
                {
                    Reject(issues, ValidationCodes.RegionUnknown, $"{what} on {condition.Metric} names region {region}, which is not on the map.");
                }
            }
            else if (ConditionEvaluator.RequiresRegion(condition.Metric))
            {
                Reject(issues, ValidationCodes.ConditionRegionRequired, $"{what} on {condition.Metric} needs a region.");
            }
        }
    }

    // Messages are formatted with the invariant culture: they are written to the
    // decision log, whose hash must not depend on the machine's locale.
    private static void Reject(List<ValidationIssue> issues, string code, FormattableString message) =>
        issues.Add(new ValidationIssue(code, ValidationSeverity.Reject, FormattableString.Invariant(message)));

    private static void Warn(List<ValidationIssue> issues, string code, FormattableString message) =>
        issues.Add(new ValidationIssue(code, ValidationSeverity.Warning, FormattableString.Invariant(message)));
}
