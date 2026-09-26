// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Operations;

/// <summary>
/// Tunables for <see cref="OperationalPlanner"/>. Every default is the
/// conventional RTS-AI answer where the spec is silent, documented at the
/// point of use rather than repeated here.
/// </summary>
/// <param name="ControllerId">
/// The single budget-pool controller name the planner reserves credits under.
/// </param>
/// <param name="DefaultHarvesterTargetPerRefinery">
/// Harvesters wanted per owned refinery when a playbook does not supply the
/// <c>harvesterTarget</c> parameter.
/// </param>
/// <param name="DefaultRetreatBelowForceRatio">
/// Squad retreat threshold used when an intent does not specify one (spec
/// invariant 3 default: 0.6).
/// </param>
/// <param name="SquadLeaseMinHoldSeconds">Minimum hold time for a squad's unit leases.</param>
/// <param name="SquadLeaseTtlSeconds">Lease time-to-live; the planner renews every <see cref="OperationalPlanner.Plan"/> call.</param>
/// <param name="SquadLeasePriority">Priority used when acquiring unit leases for squads.</param>
/// <param name="HarassSquadSize">Unit count for a harass squad.</param>
/// <param name="ReinforceSquadTargetSize">
/// A squad below this unit count is considered "under strength" and receives reinforcements.
/// </param>
/// <param name="BuildSearchRings">How many square rings the placement search expands outward.</param>
/// <param name="BuildGridStep">Cell spacing between placement candidates and the clearance from existing buildings.</param>
/// <param name="PowerBuffer">Power surplus below which, with nothing else to build, another power plant is queued.</param>
/// <param name="MaxProductionBuildings">Production buildings (each speeds every queue) the planner adds up to when rich.</param>
/// <param name="ExtraProductionCredits">Credits on hand before an extra production building is considered.</param>
/// <param name="DefaultExpandAtSeconds">Game time of the second refinery when the playbook has no <c>expandAtSeconds</c>.</param>
/// <param name="MaxRefineries">Refinery cap (also capped by the map's ore regions).</param>
/// <param name="MaxDefenses">Static defense cap for defensive budgets.</param>
/// <param name="MinAttackArmyValue">Army value an attack waits for when the playbook has no <c>attackArmyValue</c>.</param>
/// <param name="AttackHoldFraction">An attack in progress continues while army value stays above this fraction of that threshold.</param>
/// <param name="ScoutRevisitSeconds">A start location seen within this many seconds is not re-scouted first.</param>
/// <param name="DefaultDefendThreatRatio">Base threat ratio that pulls the army home to defend when the playbook has no <c>defendThreatRatio</c>.</param>
/// <param name="DefaultHarassIntervalSeconds">Seconds between harass sorties when the playbook has no <c>harassIntervalSeconds</c>.</param>
/// <param name="HarassDwellSeconds">Seconds a harass sortie spends in its target region before it heads home to regroup.</param>
/// <param name="SuperweaponTargetRadiusCells">Radius used to score superweapon targets: the known enemy building value within it.</param>
/// <param name="ConditionEvaluator">
/// Evaluates one <see cref="Condition"/> against <see cref="StrategicFeatures"/>. Left null,
/// the planner uses the canonical <see cref="Arbitration.ConditionEvaluator.Holds"/>, the single
/// definition of every metric shared with the validator and arbiter; a test may substitute a stub.
/// </param>
public sealed record OperationalOptions(
    string ControllerId = "ops",
    double DefaultHarvesterTargetPerRefinery = 3.0,
    double DefaultRetreatBelowForceRatio = 0.6,
    double SquadLeaseMinHoldSeconds = 10.0,
    double SquadLeaseTtlSeconds = 5.0,
    int SquadLeasePriority = 10,
    int HarassSquadSize = 3,
    int ReinforceSquadTargetSize = 4,
    int BuildSearchRings = 16,
    int BuildGridStep = 3,
    Func<Condition, StrategicFeatures, bool>? ConditionEvaluator = null,
    int PowerBuffer = 50,
    int MaxProductionBuildings = 6,
    int ExtraProductionCredits = 2000,
    double DefaultExpandAtSeconds = 150,
    int MaxRefineries = 4,
    int MaxDefenses = 8,
    double MinAttackArmyValue = 1500,
    double AttackHoldFraction = 0.5,
    double ScoutRevisitSeconds = 60,
    double DefaultDefendThreatRatio = 1.0,
    double DefaultHarassIntervalSeconds = 60,
    double HarassDwellSeconds = 10,
    double SuperweaponTargetRadiusCells = 6)
{
    /// <summary>The evaluator actually used: <see cref="ConditionEvaluator"/> when set, else the default.</summary>
    public Func<Condition, StrategicFeatures, bool> Evaluator => ConditionEvaluator ?? Arbitration.ConditionEvaluator.Holds;
}
