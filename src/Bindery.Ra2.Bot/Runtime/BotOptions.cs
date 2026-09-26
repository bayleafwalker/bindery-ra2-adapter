// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arbitration;

namespace Bindery.Ra2.Bot.Runtime;

/// <summary>
/// Runtime cadences and policies (spec "Layers and cadence"). All times are game
/// seconds, converted to frames at <see cref="GameTime.FramesPerSecond"/>, so a
/// paused or slowed game never makes decisions go stale on the wall clock.
/// </summary>
/// <param name="StrategicCadenceSeconds">Primary strategist cadence (spec 20 s; sensible 10–30 s).</param>
/// <param name="MajorEventSeverity">Event severity that triggers an immediate strategic request.</param>
/// <param name="OperationalCadenceSeconds">Operational planning cadence (spec 1 s); also the ledger's planning period.</param>
/// <param name="OperationalEventSeverity">Event severity that triggers an immediate operational pass (spec 0.5).</param>
/// <param name="TacticalHz">Tactical controller rate, 5–15 Hz; 15 runs them every frame.</param>
/// <param name="RunDeterministicStrategistsInline">See <see cref="SchedulerOptions.RunDeterministicStrategistsInline"/>; true in the simulator and tests.</param>
/// <param name="MaxProposalAgeSeconds">Late-discard age (invariant 2).</param>
/// <param name="StaleEventSeverity">Late-discard event severity (invariant 2).</param>
/// <param name="StrategistTimeoutSeconds">Game seconds before an unanswered strategist request is cancelled.</param>
/// <param name="OperationsControllerId">
/// Controller id budget reservations are made under when the plan's production commands do not name a single
/// controller themselves. The operational planner should issue production with this id.
/// </param>
/// <param name="Personality">Passed to strategists in <see cref="StrategistContext.Personality"/>.</param>
/// <param name="EmergencyPlaybookId">Playbook used when no strategist produced any valid intent.</param>
/// <param name="Arbiter">Commitment and hysteresis settings; null for the spec defaults.</param>
/// <param name="LogPlans">Write an <c>operations.plan</c> record for every operational pass.</param>
/// <param name="AccrueBudgets">Run the budget ledger in accrual mode (<see cref="LedgerOptions.AccrueByShare"/>).</param>
/// <param name="ProductionChargedWhileBuilding">
/// True where the game debits a production item's cost gradually as it builds (retail RA2): the ledger's capacity
/// then excludes what queued items still owe, since the credits on hand do not show it yet. False where the full
/// cost is debited at order time (the simulator), where subtracting it again would count it twice.
/// </param>
public sealed record BotOptions(
    double StrategicCadenceSeconds = 20,
    double MajorEventSeverity = 0.6,
    double OperationalCadenceSeconds = 1,
    double OperationalEventSeverity = 0.5,
    double TacticalHz = 15,
    bool RunDeterministicStrategistsInline = false,
    double MaxProposalAgeSeconds = 15,
    double StaleEventSeverity = 0.7,
    double StrategistTimeoutSeconds = 60,
    string OperationsControllerId = "operations",
    string? Personality = null,
    string? EmergencyPlaybookId = "generic-defend",
    ArbiterOptions? Arbiter = null,
    bool LogPlans = true,
    bool AccrueBudgets = true,
    bool ProductionChargedWhileBuilding = true)
{
    public static BotOptions Default { get; } = new();

    /// <summary>Frames between tactical passes: 1 at 15 Hz, 3 at 5 Hz.</summary>
    public int TacticalPeriodFrames => Math.Max(1, (int)Math.Round(GameTime.FramesPerSecond / TacticalHz));

    public SchedulerOptions ToSchedulerOptions() => new(
        StrategicCadenceSeconds,
        MajorEventSeverity,
        MaxProposalAgeSeconds,
        StaleEventSeverity,
        StrategistTimeoutSeconds,
        RunDeterministicStrategistsInline: RunDeterministicStrategistsInline,
        Personality: Personality,
        EmergencyPlaybookId: EmergencyPlaybookId);

    /// <summary>Throws when a value would make the runtime misbehave (non-positive cadence, tactical rate outside (0, 15]).</summary>
    public void Validate()
    {
        if (!(StrategicCadenceSeconds > 0)) throw new ArgumentOutOfRangeException(nameof(StrategicCadenceSeconds));
        if (!(OperationalCadenceSeconds > 0)) throw new ArgumentOutOfRangeException(nameof(OperationalCadenceSeconds));
        if (!(TacticalHz > 0) || TacticalHz > GameTime.FramesPerSecond) throw new ArgumentOutOfRangeException(nameof(TacticalHz));
        if (!(StrategistTimeoutSeconds > 0)) throw new ArgumentOutOfRangeException(nameof(StrategistTimeoutSeconds));
        if (!(MaxProposalAgeSeconds > 0)) throw new ArgumentOutOfRangeException(nameof(MaxProposalAgeSeconds));
        ArgumentException.ThrowIfNullOrEmpty(OperationsControllerId);
    }
}

/// <summary>
/// Everything a <see cref="BotRuntime"/> is assembled from. Each layer is a
/// contract, so the same runtime drives the simulator, retail RA2 and replays,
/// and every arena arm is just a different set of components.
/// </summary>
/// <param name="Primary">The configured strategist (selector, bandit, LLM, replay, ...).</param>
/// <param name="Fallback">Deterministic strategist used whenever no usable intent exists.</param>
/// <param name="Shadow">Optional strategist whose proposals are recorded and never applied.</param>
public sealed record BotComponents(
    IBeliefModel Belief,
    IFeatureCompiler Features,
    IRulesDatabase Rules,
    IPlaybookLibrary Playbooks,
    IIntentValidator Validator,
    IOperationalPlanner Planner,
    IReadOnlyList<ITacticalController> Tactics,
    IStrategist Primary,
    IStrategist Fallback,
    IStrategist? Shadow,
    IDecisionLog Log,
    BotOptions Options);
