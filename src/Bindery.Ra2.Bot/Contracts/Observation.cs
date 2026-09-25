// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot;

/// <summary>
/// Whether a frame carries only what the controlled player can see
/// (<see cref="Belief"/>) or the engine's full state (<see cref="Oracle"/>).
/// Oracle frames exist solely to diagnose whether perception is the bottleneck;
/// the arena must label every result produced from them.
/// </summary>
public enum ObservationMode { Belief, Oracle }

/// <summary>
/// One observed object. For objects the player does not own, only fields the
/// player could see in game are populated; the adapter is responsible for
/// never populating hidden fields in <see cref="ObservationMode.Belief"/> mode.
/// </summary>
public sealed record ObservedEntity(
    EntityId Id,
    PlayerId Owner,
    string TypeId,
    Cell Position,
    int Health,
    int MaxHealth,
    bool Deployed = false,
    EntityId? Target = null)
{
    public double HealthFraction => MaxHealth <= 0 ? 0 : Math.Clamp(Health / (double)MaxHealth, 0, 1);
}

public sealed record QueueItem(string TypeId, double Progress, bool Ready, bool OnHold);

public sealed record ProductionQueueState(QueueKind Kind, IReadOnlyList<QueueItem> Items, int Factories);

public enum GameEventKind
{
    EntityCreated,
    EntityDestroyed,
    EntityKilledByUs,
    BuildingCaptured,
    UnderAttack,
    ProductionCompleted,
    SuperweaponLaunched,
    PlayerDefeated,
    MatchEnded,
}

/// <summary>
/// A discrete event the player's client would see or be notified about.
/// <see cref="Region"/> is null when the location is not visible.
/// </summary>
public sealed record GameEvent(
    GameEventKind Kind,
    GameTime Time,
    EntityId? Entity,
    PlayerId? Owner,
    string? TypeId,
    Cell? Position,
    string? Detail = null);

/// <summary>
/// Everything the bot receives for one decision frame. Frames are
/// append-only facts: the belief model derives memory (last seen, age,
/// confidence) from their sequence.
/// </summary>
public sealed record ObservationFrame(
    GameTime Time,
    ObservationMode Mode,
    PlayerId Self,
    Faction Faction,
    int Credits,
    PowerState Power,
    IReadOnlyList<ObservedEntity> Entities,
    IReadOnlyList<ProductionQueueState> Queues,
    IReadOnlyList<GameEvent> Events,
    IReadOnlySet<RegionId> VisibleRegions,
    MapInfo Map);

/// <summary>Where frames come from: the RA2 bridge, the simulator, or a replay.</summary>
public interface IObservationSource
{
    ObservationMode Mode { get; }

    /// <summary>Returns the next frame, or null when the match has ended.</summary>
    ValueTask<ObservationFrame?> NextAsync(CancellationToken cancellationToken = default);
}
