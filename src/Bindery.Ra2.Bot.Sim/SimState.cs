// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Sim;

/// <summary>Harvester economic cycle state; the bindery region sim models a trip as four timed phases.</summary>
internal enum HarvesterPhase { Idle, ToOre, Harvesting, ToRefinery, Unloading }

/// <summary>
/// One live object in the sim. Mutable by design (the engine owns the only
/// copy); everything external sees an immutable <see cref="ObservedEntity"/>
/// snapshot built from this each frame.
/// </summary>
internal sealed class SimEntity
{
    public required EntityId Id { get; init; }
    public required PlayerId Owner { get; init; }
    public required string TypeId { get; set; }
    public required Cell Position { get; set; }
    public required RegionId Region { get; set; }

    // Sub-cell movement precision: Position is the public, whole-cell snapshot; slow units (well under
    // one cell per frame) would otherwise never accumulate visible movement if every frame rounded and
    // discarded the fractional remainder. Kept in sync with Position by SnapTo everywhere except the
    // fractional-step branch of movement, which is the only place allowed to diverge between the two.
    public double ExactX { get; set; }
    public double ExactY { get; set; }

    public void SnapTo(Cell cell)
    {
        Position = cell;
        ExactX = cell.X;
        ExactY = cell.Y;
    }
    public required int Health { get; set; }
    public required int MaxHealth { get; set; }
    public bool Deployed { get; set; }
    public EntityId? ExplicitTarget { get; set; }
    public bool Alive => Health > 0;

    // Movement.
    public List<RegionId> RemainingPath { get; } = [];
    public Cell? FinalDestination { get; set; }
    public bool HoldForCombat { get; set; }

    // Harvester economy.
    public HarvesterPhase Phase { get; set; } = HarvesterPhase.Idle;
    public RegionId? AssignedOreRegion { get; set; }
    public RegionId? TargetRefineryRegion { get; set; }
    public int CarriedValue { get; set; }
    public double PhaseSecondsRemaining { get; set; }

    // Repair.
    public bool RepairRequested { get; set; }

    // Superweapon charge in seconds (superweapon buildings only).
    public double SuperweaponCharge { get; set; }
}

internal sealed class QueueItemRuntime
{
    public required string TypeId { get; init; }
    public double Progress { get; set; }
    public bool Ready => Progress >= 1.0;

    /// <summary>True once a finished building/defense item has been moved to <see cref="SimPlayerState.PendingPlacements"/>; it still blocks its queue until placed.</summary>
    public bool AwaitingPlacement { get; set; }
}

internal sealed class QueueRuntime
{
    public required QueueKind Kind { get; init; }
    public List<QueueItemRuntime> Items { get; } = [];
}

internal sealed class PendingPlacement
{
    public required string TypeId { get; init; }
    public required QueueKind Queue { get; init; }
}

/// <summary>Per-player mutable state the engine advances each step.</summary>
internal sealed class SimPlayerState
{
    public required PlayerId Id { get; init; }
    public required Faction Faction { get; init; }
    public int Credits { get; set; }
    public bool Defeated { get; set; }
    public int RejectedCommands { get; set; }
    public Dictionary<QueueKind, QueueRuntime> Queues { get; } = [];
    public List<PendingPlacement> PendingPlacements { get; } = [];
    public List<GameEvent> PendingEvents { get; } = [];

    public Queue<GameCommand> RecentRejections { get; } = new();
}
