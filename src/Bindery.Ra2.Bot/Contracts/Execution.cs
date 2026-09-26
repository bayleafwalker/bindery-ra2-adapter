// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot;

public enum ValidationSeverity { Warning, Reject }

/// <param name="Code">Stable machine code, e.g. <c>budget.sum</c>, <c>stale.snapshot</c>, <c>fog.unknown_type</c>.</param>
public sealed record ValidationIssue(string Code, ValidationSeverity Severity, string Message);

/// <summary>
/// Validator verdict. When accepted, <see cref="Intent"/> is the sanitised
/// intent actually applied (clamped parameters, normalised budget, playbook
/// defaults filled in); warnings explain every change.
/// </summary>
public sealed record ValidationResult(bool Accepted, StrategicIntent? Intent, IReadOnlyList<ValidationIssue> Issues);

public sealed record ValidationContext(
    StrategicFeatures Current,
    BeliefSnapshot Belief,
    IRulesDatabase Rules,
    IPlaybookLibrary Playbooks,
    StrategicIntent? ActiveIntent,
    GameTime ActiveSince);

public interface IIntentValidator
{
    ValidationResult Validate(StrategicIntent proposal, ValidationContext context);
}

/// <summary>What can be leased: one unit, one squad, or one budget pool.</summary>
public readonly record struct LeaseKey(string Value)
{
    public static LeaseKey Unit(EntityId id) => new($"unit:{id.Value}");
    public static LeaseKey Squad(string squadId) => new($"squad:{squadId}");
    public static LeaseKey Budget(string pool) => new($"budget:{pool}");
    public override string ToString() => Value;
}

public sealed record Lease(LeaseKey Key, string Owner, int Priority, GameTime Acquired, GameTime MinHoldUntil, GameTime ExpiresAt);

/// <summary>
/// Single-owner arbitration. A key has at most one live lease. A higher
/// priority request preempts only after the holder's minimum hold time;
/// equal or lower priority never preempts.
/// </summary>
public interface ILeaseManager
{
    Lease? TryAcquire(LeaseKey key, string owner, int priority, GameTime now, double minHoldSeconds, double ttlSeconds);

    bool Renew(LeaseKey key, string owner, GameTime now, double ttlSeconds);

    void Release(LeaseKey key, string owner);

    string? OwnerOf(LeaseKey key, GameTime now);

    IReadOnlyList<Lease> HeldBy(string owner, GameTime now);
}

/// <summary>
/// A game command. <see cref="Controller"/> names the issuing controller; the
/// command gate drops any command on a unit whose lease that controller does not hold.
/// </summary>
public abstract record GameCommand(string Controller);

public sealed record ProduceCommand(string Controller, string TypeId, QueueKind Queue) : GameCommand(Controller);
public sealed record CancelProductionCommand(string Controller, string TypeId, QueueKind Queue) : GameCommand(Controller);
public sealed record PlaceBuildingCommand(string Controller, string TypeId, Cell Cell) : GameCommand(Controller);
public sealed record SellCommand(string Controller, EntityId Building) : GameCommand(Controller);
public sealed record MoveCommand(string Controller, IReadOnlyList<EntityId> Units, Cell Destination) : GameCommand(Controller);
public sealed record AttackMoveCommand(string Controller, IReadOnlyList<EntityId> Units, Cell Destination) : GameCommand(Controller);
public sealed record AttackCommand(string Controller, IReadOnlyList<EntityId> Units, EntityId Target) : GameCommand(Controller);
public sealed record StopCommand(string Controller, IReadOnlyList<EntityId> Units) : GameCommand(Controller);
public sealed record DeployCommand(string Controller, EntityId Unit) : GameCommand(Controller);
public sealed record RepairCommand(string Controller, EntityId Unit, EntityId? Depot) : GameCommand(Controller);
public sealed record HarvestCommand(string Controller, EntityId Harvester, Cell Ore) : GameCommand(Controller);
public sealed record SetRallyPointCommand(string Controller, EntityId Factory, Cell Cell) : GameCommand(Controller);

public interface ICommandSink
{
    void Submit(GameCommand command);
}

/// <summary>A squad as the operational layer defines it and tactics executes it.</summary>
/// <param name="StandoffBufferCells">
/// Siege stand-off (the playbook's <c>siegeRangeBufferCells</c>): when positive and the squad has artillery that
/// out-ranges a known enemy defense by at least this much, the squad holds this many cells beyond the defense's
/// range, outside the defense's region, and the artillery bombards it from there. Zero assaults as usual.
/// </param>
public sealed record SquadOrder(string SquadId, ObjectiveKind Objective, RegionId TargetRegion, IReadOnlyList<EntityId> Units, bool Engage, double RetreatBelowForceRatio, double StandoffBufferCells = 0);

/// <summary>Output of one operational planning pass.</summary>
/// <param name="BudgetReservations">
/// Credits to reserve this pass, keyed by budget pool name (<c>economy</c>, <c>army</c>, <c>tech</c>,
/// <c>defense</c>; case-insensitive). The runtime reserves them in the ledger under the controller that
/// issued <see cref="ProductionCommands"/>, and the command gate spends them; an empty map grants every
/// pool its full share.
/// </param>
public sealed record OperationalPlan(
    GameTime Time,
    string IntentId,
    IReadOnlyList<GameCommand> ProductionCommands,
    IReadOnlyList<SquadOrder> Squads,
    IReadOnlyDictionary<string, int> BudgetReservations,
    IReadOnlyList<string> Notes);

public interface IOperationalPlanner
{
    OperationalPlan Plan(BeliefSnapshot belief, StrategicFeatures features, StrategicIntent intent, ILeaseManager leases);
}

/// <summary>Fast controllers: squads, harvesters, repair, deployment.</summary>
/// <remarks>
/// A controller commands only units whose lease it holds when it issues the command: it acquires (or
/// renews) its own leases, except squad units, which the operational planner leases as
/// <c>squad:&lt;SquadId&gt;</c> for the squad controller. The command gate drops any command naming a unit
/// the issuing controller does not hold.
/// </remarks>
public interface ITacticalController
{
    string Id { get; }

    IReadOnlyList<GameCommand> Tick(BeliefSnapshot belief, IReadOnlyList<SquadOrder> squads, ILeaseManager leases, IRulesDatabase rules);
}
