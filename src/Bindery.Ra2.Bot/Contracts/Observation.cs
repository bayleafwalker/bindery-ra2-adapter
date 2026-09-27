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
/// <see cref="Position"/> is null when the location is not visible.
/// </summary>
/// <param name="Owner">
/// The player the event belongs to: the owner of the created, destroyed, attacked or produced object, the launching
/// player of a superweapon, and for <see cref="GameEventKind.EntityKilledByUs"/> the <em>killer</em> (the receiving
/// player, since the event is only delivered to its killer); the victim is <see cref="Entity"/>. Every source
/// (simulator and RA2 assembler) uses this meaning, so a consumer can tell its own kills from other players' kills
/// in an oracle stream.
/// </param>
public sealed record GameEvent(
    GameEventKind Kind,
    GameTime Time,
    EntityId? Entity,
    PlayerId? Owner,
    string? TypeId,
    Cell? Position,
    string? Detail = null);

/// <summary>
/// One superweapon's countdown as the game shows it. RA2 displays every player's superweapon timer to all
/// players, so an enemy timer is legitimate belief-mode information; the building behind an enemy timer is not
/// (its <see cref="Building"/> is null unless the player owns it).
/// </summary>
/// <param name="ChargeSeconds">Full recharge time of this superweapon.</param>
/// <param name="SecondsToReady">Seconds of charge still missing; 0 when ready.</param>
public sealed record SuperweaponStatus(PlayerId Owner, string TypeId, EntityId? Building, double ChargeSeconds, double SecondsToReady, bool Ready);

/// <summary>
/// Everything the bot receives for one decision frame. Frames are
/// append-only facts: the belief model derives memory (last seen, age,
/// confidence) from their sequence.
/// </summary>
/// <param name="OreRemaining">
/// Ore value left in each ore region the player can currently see, as the game shows it; null when the
/// source does not report ore (the RA2 telemetry contract does not yet). Regions out of sight are absent,
/// never estimated.
/// </param>
/// <param name="Superweapons">
/// Every superweapon timer on the map (own and enemy, see <see cref="SuperweaponStatus"/>); null when the
/// source does not report superweapons (the RA2 telemetry contract does not yet).
/// </param>
/// <param name="QueuesKnown">
/// False when the source does not report production queues (the RA2 telemetry contract does not yet): then
/// <paramref name="Queues"/> is empty because nothing is known, not because every queue is idle, and consumers must
/// not treat an absent queue as free.
/// </param>
/// <param name="CreditsKnown">False when credits have not been sampled yet: <paramref name="Credits"/> is then 0 by default, not a fact.</param>
/// <param name="PowerKnown">False when power has not been sampled yet: <paramref name="Power"/> is then a placeholder, not a fact.</param>
/// <param name="Enemies">
/// The players this player is at war with. Null keeps the default that every owner other than
/// <paramref name="Self"/> is an enemy; a source that knows alliances, neutral or civilian houses sets it so their
/// objects never become enemy contacts, scouting knowledge or enemy player beliefs.
/// </param>
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
    MapInfo Map,
    IReadOnlyDictionary<RegionId, int>? OreRemaining = null,
    IReadOnlyList<SuperweaponStatus>? Superweapons = null,
    bool QueuesKnown = true,
    bool CreditsKnown = true,
    bool PowerKnown = true,
    IReadOnlySet<PlayerId>? Enemies = null)
{
    /// <summary>Whether <paramref name="owner"/> is an enemy of <see cref="Self"/> in this frame (see <see cref="Enemies"/>).</summary>
    public bool IsEnemy(PlayerId owner) => owner != Self && (Enemies is null || Enemies.Contains(owner));
}

/// <summary>Where frames come from: the RA2 bridge, the simulator, or a replay.</summary>
public interface IObservationSource
{
    ObservationMode Mode { get; }

    /// <summary>Returns the next frame, or null when the match has ended.</summary>
    ValueTask<ObservationFrame?> NextAsync(CancellationToken cancellationToken = default);
}
