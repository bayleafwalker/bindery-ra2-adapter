// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Adapter.Bot;

/// <summary>
/// The expected telemetry payload contract this bridge reads from a
/// <see cref="NormalizedObservation"/>, versioned as
/// <c>bindery.ra2.bot-observation/v1</c>.
///
/// The native ra2yrcpp fork's payload fields are not yet frozen upstream, so
/// this contract is deliberately narrow: it names exactly the fields
/// <see cref="Ra2ObservationAssembler"/> reads for each normalized event type,
/// and nothing else. A field this contract does not name is never invented;
/// a field it does name but the payload omits is reported via
/// <see cref="MissingFieldsReport"/> and the affected entity or event is
/// excluded rather than defaulted, per the spec's fog invariant.
///
/// Per normalized event type (see <c>Ra2Normalizer</c> for the raw-to-normalized
/// mapping):
/// <list type="bullet">
/// <item><description>
/// <c>game.unit.created</c>, <c>game.building.placed</c>: <c>frame</c> (int),
/// <c>owner</c> (player id, int), <c>id</c> (entity id, uint), <c>type</c>
/// (string), <c>x</c>, <c>y</c> (cell coordinates), <c>health</c>,
/// <c>maxHealth</c> (int). When <c>owner</c> is not the controlled player,
/// <c>visible</c> (bool) is additionally required and must be <c>true</c> for
/// the entity to appear in a frame: this is the fog boundary. The first
/// upsert of an id is its <see cref="GameEventKind.EntityCreated"/>; a repeat
/// upsert of a tracked id refreshes its position, health and owner without a
/// new event, so own-entity state is only as current as the source re-sends
/// it (v1 has no separate state event). An enemy upsert
/// is a <i>sighting</i>: the enemy appears only in the frame whose window
/// (since the previous frame) holds a sighting of it, so the source re-sends
/// the upsert at least once per frame cadence for every enemy in sight, and
/// sends it with <c>visible</c> false when the enemy leaves sight. Visible
/// regions are derived from the controlled player's own entities only.
/// </description></item>
/// <item><description>
/// <c>game.unit.destroyed</c>, <c>game.building.destroyed</c>,
/// <c>game.unit.killed</c>: <c>frame</c>, <c>id</c>; optional <c>killer</c>
/// (player id); <c>visible</c> (bool), required for an enemy's removal unless
/// <c>killer</c> is the controlled player. The entity is removed from tracked
/// state. An own entity's removal is always an
/// <see cref="GameEventKind.EntityDestroyed"/> (a loss). An enemy's removal is
/// reported only when the entity was sighted and has not left sight since
/// (a bare id never seen is never reported), and the player saw it: the
/// payload says <c>visible</c> true, or <c>killer</c> is the controlled
/// player. A missing <c>visible</c> is reported missing and the removal
/// skipped. It is <see cref="GameEventKind.EntityKilledByUs"/> only when
/// <c>killer</c> is the controlled player, else
/// <see cref="GameEventKind.EntityDestroyed"/>. Owner, type and position come
/// from tracked state.
/// </description></item>
/// <item><description>
/// <c>game.economy.credits</c>: <c>frame</c>, <c>owner</c>, <c>credits</c> (int).
/// Only the sample whose <c>owner</c> is the controlled player updates
/// <see cref="ObservationFrame.Credits"/>; others are ignored (not a missing
/// field — it is simply not this player's economy).
/// </description></item>
/// <item><description>
/// <c>game.economy.power</c>: <c>frame</c>, <c>owner</c>, <c>produced</c>,
/// <c>drained</c> (int). Same owner gating as credits, mapped to
/// <see cref="PowerState"/>.
/// </description></item>
/// <item><description>
/// <c>game.participant.defeated</c>: <c>frame</c>, <c>owner</c>. Mapped to a
/// <see cref="GameEventKind.PlayerDefeated"/> event.
/// </description></item>
/// <item><description>
/// <c>game.lifecycle.ended</c>: <c>frame</c>. Mapped to
/// <see cref="GameEventKind.MatchEnded"/>.
/// </description></item>
/// </list>
///
/// Event types outside this list (<c>game.unit.queued</c>,
/// <c>game.order.issued</c>, <c>game.selection.changed</c>,
/// <c>game.lifecycle.started</c>, <c>game.participant.joined</c>,
/// <c>game.observation.unknown</c>) are not yet part of this contract version
/// and are ignored by the assembler without being reported as missing —
/// widening the contract to cover them is a version bump, not a silent guess.
/// </summary>
public static class Ra2BotTelemetryContract
{
    /// <summary>Identifies this payload contract version in logs and reports.</summary>
    public const string SchemaVersion = "bindery.ra2.bot-observation/v1";

    public const string FieldFrame = "frame";
    public const string FieldOwner = "owner";
    public const string FieldId = "id";
    public const string FieldType = "type";
    public const string FieldX = "x";
    public const string FieldY = "y";
    public const string FieldHealth = "health";
    public const string FieldMaxHealth = "maxHealth";
    public const string FieldCredits = "credits";
    public const string FieldProduced = "produced";
    public const string FieldDrained = "drained";
    public const string FieldVisible = "visible";
    public const string FieldKiller = "killer";
}
