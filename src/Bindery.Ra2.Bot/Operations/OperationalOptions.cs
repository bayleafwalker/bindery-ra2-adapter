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
/// Reinforcement batch size: units built while an attack runs gather at the staging region until this many have
/// assembled, then join the attack together.
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
/// <param name="SuperweaponTargetRadiusCells">Radius used to score superweapon targets: the known enemy building value within it minus the own object value within it (the strike hits both sides)</param>
/// <param name="DefendReleaseFraction">
/// Hysteresis of the defend switch: the army, once pulled home by a base threat at the defend ratio, stays until
/// the threat drops below this fraction of that ratio (and <paramref name="DefendMinSeconds"/> have passed).
/// </param>
/// <param name="DefendMinSeconds">Minimum seconds the army defends before it may leave for an attack again.</param>
/// <param name="PlacementRetrySeconds">
/// A finished building still unplaced this long after it was ordered onto a cell means the game refused the cell
/// (footprint overlap, cliff, ore, a unit in the way); the next placement searches elsewhere.
/// </param>
/// <param name="RejectedPlacementMemorySeconds">How long a refused cell stays excluded (a blocking unit moves on).</param>
/// <param name="ProductionChargedWhileBuilding">
/// True where the game debits an item's cost as it builds (retail RA2 and the simulator): the planner's spendable
/// credits then exclude what queued items still owe (<see cref="ProductionDebt"/>), as the budget ledger does, or
/// it would plan purchases the gate can only drop. Set from <see cref="Runtime.BotOptions.ProductionChargedWhileBuilding"/>.
/// </param>
/// <param name="UnknownQueueReorderSeconds">
/// Where the source does not report queues (<see cref="BeliefSnapshot.QueuesKnown"/> false) an absent queue is not
/// proof of an idle one: the planner orders into a queue at most once per the ordered item's build time plus this
/// margin, from its own record of what it ordered, instead of re-ordering (and re-charging the budget) every pass.
/// </param>
/// <param name="ConditionEvaluator">
/// Evaluates one <see cref="Condition"/> against <see cref="StrategicFeatures"/>. Left null,
/// the planner uses the canonical <see cref="Arbitration.ConditionEvaluator.Holds"/>, the single
/// definition of every metric shared with the validator and arbiter; a test may substitute a stub.
/// </param>
/// <param name="MinAttackForceRatio">
/// Every fresh attack, whatever the playbook's own conditions, also needs own army value over
/// <see cref="EnemyArmyBound.Upper"/> to reach this. A running attack is held by <see cref="AttackHoldFraction"/>
/// and the retreat ratio, not re-gated.
/// </param>
/// <param name="SeenAttackForceRatio">
/// The launch ratio when the enemy estimate rests on a fresh, fully confident sighting. The required ratio slides
/// linearly from <see cref="MinAttackForceRatio"/> (no usable evidence) to this by <see cref="EnemyArmyBound.EvidenceWeight"/>.
/// </param>
/// <param name="BaseSightingWeightCap">
/// Ceiling in [0, 1] on the base-sighting term of <see cref="EnemyArmyBound.EvidenceWeight"/> (the army-sighting term
/// is not capped). 1 leaves the weight as the max of both freshness terms; lower values stop a recently seen base
/// with no army in it from counting as full evidence of a small army. Diagnostic knob, never tuned.
/// </param>
/// <param name="EnemyPriorValuePerSecond">
/// Army value an unseen enemy is assumed to add per second after <see cref="EnemyPriorStartSeconds"/>. From the
/// 75th percentile of running-peak opponent army value on training maps against training opponents (0 until
/// ~75 s, 1300 at 180 s); held-out maps and opponents were not used.
/// </param>
/// <param name="EnemyPriorStartSeconds">Match time before which the prior is 0 (openings field no army).</param>
/// <param name="EnemyPriorMaxValue">Ceiling of the prior: the same training p75 levels off at 1600 from ~210 s.</param>
/// <param name="EnemyEvidenceSeconds">Age of the newest enemy sighting at which the sighting no longer counts.</param>
/// <param name="EnemyUncertaintyMargin">
/// Fraction the enemy estimate is raised by with no usable evidence (scaled by the missing evidence weight),
/// so the gate reads a lower confidence bound on the force ratio.
/// </param>
public sealed record OperationalOptions(
    string ControllerId = "ops",
    double DefaultHarvesterTargetPerRefinery = 3.0,
    double DefaultRetreatBelowForceRatio = 0.6,
    double SquadLeaseMinHoldSeconds = 10.0,
    double SquadLeaseTtlSeconds = 5.0,
    int SquadLeasePriority = 10,
    int HarassSquadSize = 3,
    int ReinforceSquadTargetSize = 2,
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
    double SuperweaponTargetRadiusCells = 6,
    double DefendReleaseFraction = 0.7,
    double DefendMinSeconds = 10,
    double PlacementRetrySeconds = 3,
    double RejectedPlacementMemorySeconds = 120,
    bool ProductionChargedWhileBuilding = true,
    double UnknownQueueReorderSeconds = 5,
    double MinAttackForceRatio = 1.2,
    double SeenAttackForceRatio = 0.8,
    double BaseSightingWeightCap = 1.0,
    double EnemyPriorValuePerSecond = 12,
    double EnemyPriorStartSeconds = 75,
    double EnemyPriorMaxValue = 1600,
    double EnemyEvidenceSeconds = 60,
    double EnemyUncertaintyMargin = 0.5)
{
    /// <summary>The evaluator actually used: <see cref="ConditionEvaluator"/> when set, else the default.</summary>
    public Func<Condition, StrategicFeatures, bool> Evaluator => ConditionEvaluator ?? Arbitration.ConditionEvaluator.Holds;
}
