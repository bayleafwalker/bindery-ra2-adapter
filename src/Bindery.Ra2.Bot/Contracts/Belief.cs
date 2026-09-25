// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot;

/// <summary>An object the player owns, as currently observed.</summary>
public sealed record OwnEntity(
    EntityId Id,
    string TypeId,
    UnitRole Role,
    EntityKind Kind,
    Cell Position,
    RegionId Region,
    double HealthFraction,
    int Value,
    bool Deployed);

/// <summary>
/// The player's memory of an enemy object: where and when it was last seen
/// and how much that sighting is still worth. Confidence decays with age and
/// never exceeds 1. A contact is never updated from information the player
/// did not observe.
/// </summary>
public sealed record EnemyContact(
    EntityId Id,
    PlayerId Owner,
    string TypeId,
    UnitRole Role,
    EntityKind Kind,
    Cell LastSeenPosition,
    RegionId LastSeenRegion,
    GameTime LastSeenAt,
    double HealthFractionWhenSeen,
    int Value,
    double Confidence,
    bool ConfirmedDestroyed);

/// <summary>What the player knows about one enemy player as a whole.</summary>
public sealed record EnemyPlayerBelief(
    PlayerId Player,
    Faction? Faction,
    RegionId? SuspectedStart,
    IReadOnlySet<string> SeenTech,
    GameTime? LastSeenAnything);

/// <summary>
/// Immutable view of the belief state at one frame. <see cref="Version"/>
/// increments on every applied frame and is the freshness key for every
/// asynchronous decision.
/// </summary>
public sealed record BeliefSnapshot(
    long Version,
    GameTime Time,
    ObservationMode Mode,
    PlayerId Self,
    Faction Faction,
    int Credits,
    PowerState Power,
    IReadOnlyList<OwnEntity> Own,
    IReadOnlyList<EnemyContact> Enemies,
    IReadOnlyList<EnemyPlayerBelief> EnemyPlayers,
    IReadOnlyList<ProductionQueueState> Queues,
    IReadOnlyDictionary<RegionId, GameTime> RegionLastSeen,
    IReadOnlyList<GameEvent> RecentEvents,
    MapInfo Map)
{
    public IReadOnlySet<string> OwnBuildingTypes =>
        Own.Where(static e => e.Kind == EntityKind.Building).Select(static e => e.TypeId).ToHashSet(StringComparer.Ordinal);
}

public interface IBeliefModel
{
    BeliefSnapshot Current { get; }

    /// <summary>Folds one frame into the belief and returns the new snapshot.</summary>
    BeliefSnapshot Apply(ObservationFrame frame);
}
