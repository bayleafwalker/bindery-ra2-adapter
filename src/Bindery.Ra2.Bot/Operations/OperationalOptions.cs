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
/// <param name="ConditionEvaluator">
/// Evaluates one <see cref="Condition"/> against <see cref="StrategicFeatures"/>. Left null,
/// the planner uses its own <see cref="DefaultConditionEvaluator"/>; the Arbitration package
/// writes the shared evaluator concurrently, and the integrator may pass that one in instead
/// so both packages agree on attack-condition semantics.
/// </param>
public sealed record OperationalOptions(
    string ControllerId = "ops",
    double DefaultHarvesterTargetPerRefinery = 2.0,
    double DefaultRetreatBelowForceRatio = 0.6,
    double SquadLeaseMinHoldSeconds = 10.0,
    double SquadLeaseTtlSeconds = 5.0,
    int SquadLeasePriority = 10,
    int HarassSquadSize = 3,
    int ReinforceSquadTargetSize = 4,
    int BuildSearchRings = 16,
    int BuildGridStep = 3,
    Func<Condition, StrategicFeatures, bool>? ConditionEvaluator = null)
{
    /// <summary>The evaluator actually used: <see cref="ConditionEvaluator"/> when set, else the default.</summary>
    public Func<Condition, StrategicFeatures, bool> Evaluator => ConditionEvaluator ?? DefaultConditionEvaluator.Evaluate;
}
