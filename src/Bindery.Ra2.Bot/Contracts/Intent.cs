// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot;

public enum StrategicPosture { Boom, Expand, Tech, Turtle, Defend, Pressure, Harass, AllIn }

public enum ObjectiveKind { DefendRegion, AttackRegion, Expand, Scout, Harass, TechTo, DenyExpansion, Retreat }

/// <param name="Region">Target region, when the objective has one.</param>
/// <param name="TypeId">Tech or unit type, for <see cref="ObjectiveKind.TechTo"/>.</param>
/// <param name="Priority">Lower is more important; unique within an intent is not required.</param>
public sealed record Objective(ObjectiveKind Kind, RegionId? Region, string? TypeId, int Priority);

/// <summary>Share of spending per purpose. Shares are non-negative and sum to 1 (validator tolerance 0.01).</summary>
public sealed record BudgetShares(double Economy, double Army, double Tech, double Defense)
{
    public double Sum => Economy + Army + Tech + Defense;
}

/// <summary>Desired share of army value held by one role, as a closed range in [0, 1].</summary>
public sealed record CompositionTarget(UnitRole Role, double MinShare, double MaxShare);

public enum ConditionMetric
{
    GameSeconds,
    Credits,
    IncomePerMinute,
    OwnArmyValue,
    EnemyArmyValueEstimate,
    ArmyValueRatio,
    LocalForceRatio,
    HarvesterCount,
    ScoutingAgeSeconds,
    BaseThreatRatio,
    LossesValue15s,
    // Appended after the original metrics; none needs a region. The LLM prompt lists them only when
    // ClaudeStrategistOptions.ExtendedConditionMetrics is on.
    /// <summary>Aircraft share of the seen enemy army value, in [0, 1]; 0 when no army is seen.</summary>
    EnemyAirShare,
    /// <summary>Vehicle share of the seen enemy army value, in [0, 1]; 0 when no army is seen.</summary>
    EnemyVehicleShare,
    /// <summary>Infantry share of the seen enemy army value, in [0, 1]; 0 when no army is seen.</summary>
    EnemyInfantryShare,
    /// <summary>Confidence in the enemy army estimate, in [0, 1]; 0 when no army is seen.</summary>
    EnemyArmyConfidence,
    /// <summary>Number of regions where we hold presence and no enemy is known to be.</summary>
    OwnedRegions,
}

public enum Comparison { Lt, Le, Gt, Ge }

/// <summary>
/// A deterministic predicate over <see cref="StrategicFeatures"/>. Region-scoped
/// metrics (<see cref="ConditionMetric.LocalForceRatio"/>,
/// <see cref="ConditionMetric.ScoutingAgeSeconds"/>) require <see cref="Region"/>.
/// </summary>
public sealed record Condition(ConditionMetric Metric, Comparison Op, double Threshold, RegionId? Region = null);

public enum IntentSource { Fallback, Selector, Bandit, Distilled, Llm, Scripted }

/// <summary>
/// The only thing a strategist may emit: a small typed statement of intent.
/// It never names individual clicks or unit IDs. Execution belongs to the
/// operational planner and tactical controllers.
/// </summary>
public sealed record StrategicIntent(
    string IntentId,
    IntentSource Source,
    long BasedOnSnapshotVersion,
    GameTime IssuedAt,
    GameTime ExpiresAt,
    StrategicPosture Posture,
    string PlaybookId,
    IReadOnlyDictionary<string, double> PlaybookParameters,
    IReadOnlyList<Objective> Objectives,
    BudgetShares Budget,
    IReadOnlyList<CompositionTarget> Composition,
    IReadOnlyList<RegionId> RegionsOfInterest,
    IReadOnlyList<Condition> AttackConditions,
    IReadOnlyList<Condition> AbortTriggers,
    IReadOnlyList<Condition> ReplanTriggers,
    double Confidence,
    IReadOnlyList<string> Assumptions,
    string? Rationale = null);

public sealed record PlaybookParameter(string Name, double Min, double Max, double Default, string Description);

/// <summary>
/// One stage of a phased playbook. The deterministic lane advances through a playbook's phases in order, forward
/// only, at tick rate and with no model call (<see cref="Arbitration.PhaseTracker"/>). A phase is entered when all of
/// <paramref name="EnterWhen"/> hold; the first phase has none and is the start phase. Non-null fields override the
/// active intent's posture, budget, composition and attack conditions while the phase is current. The start phase's
/// overrides are ignored: the intent's own fields are the start-phase values.
/// </summary>
public sealed record PlaybookPhase(
    string Name,
    IReadOnlyList<Condition> EnterWhen,
    StrategicPosture? Posture = null,
    BudgetShares? Budget = null,
    IReadOnlyList<CompositionTarget>? Composition = null,
    IReadOnlyList<Condition>? AttackConditions = null);

/// <summary>
/// An authored strategy template. Strategists choose and parameterise playbooks;
/// the playbook supplies defaults for everything an intent leaves unsaid.
/// </summary>
public sealed record Playbook(
    string Id,
    string Description,
    IReadOnlyList<Faction> Factions,
    StrategicPosture Posture,
    BudgetShares Budget,
    IReadOnlyList<CompositionTarget> Composition,
    IReadOnlyList<string> TechGoals,
    IReadOnlyList<Condition> AttackConditions,
    IReadOnlyList<Condition> AbortTriggers,
    IReadOnlyList<PlaybookParameter> Parameters,
    double MinCommitSeconds,
    IReadOnlyList<PlaybookPhase>? Phases = null);

public interface IPlaybookLibrary
{
    IReadOnlyList<Playbook> All { get; }

    bool TryGet(string id, out Playbook playbook);

    IReadOnlyList<Playbook> For(Faction faction);
}
