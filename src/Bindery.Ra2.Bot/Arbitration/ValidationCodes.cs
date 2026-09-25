// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Arbitration;

/// <summary>
/// Stable validator issue codes. Arena metrics (invalid plans, hidden-information
/// leakage via <c>fog.*</c>) are counted by these strings, so they never change meaning.
/// </summary>
public static class ValidationCodes
{
    public const string IntentId = "intent.id";
    public const string PlaybookUnknown = "playbook.unknown";
    public const string PlaybookFaction = "playbook.faction";
    public const string BudgetNegative = "budget.negative";
    public const string BudgetSum = "budget.sum";
    public const string CompositionRange = "composition.range";
    public const string CompositionDuplicate = "composition.duplicate";
    public const string ParamUnknown = "param.unknown";
    public const string ParamClamped = "param.clamped";
    public const string RegionUnknown = "region.unknown";
    public const string ObjectiveRegionRequired = "objective.region_required";
    public const string ObjectiveTypeRequired = "objective.type_required";
    public const string TypeUnknown = "type.unknown";
    public const string TechUnreachable = "tech.unreachable";
    public const string FogUnknownType = "fog.unknown_type";
    public const string FogRegion = "fog.region";
    public const string StaleSnapshot = "stale.snapshot";
    public const string StaleEvent = "stale.event";
    public const string SnapshotFuture = "snapshot.future";
    public const string ExpiryPast = "expiry.past";
    public const string ExpiryTooLong = "expiry.too_long";
    public const string ConfidenceRange = "confidence.range";
    public const string ConditionRegionRequired = ConditionEvaluator.RegionRequiredCode;
    public const string ConditionThreshold = "condition.threshold";
    public const string DefaultFilled = "default.filled";

    /// <summary>True for codes that mean "the world moved on", which the scheduler reports as late, not invalid.</summary>
    public static bool IsStale(string code) =>
        code is StaleSnapshot or StaleEvent;
}
