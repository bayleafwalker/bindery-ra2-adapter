// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot;

namespace Bindery.Ra2.Adapter.Bot;

/// <summary>
/// Folds <see cref="NormalizedObservation"/> telemetry events for one
/// controlled player into <see cref="ObservationFrame"/>s at a configurable
/// frame cadence, under the <see cref="Ra2BotTelemetryContract"/> payload
/// contract.
///
/// Two invariants the spec requires are enforced here, not downstream:
/// <list type="bullet">
/// <item><description>
/// <b>No invented values.</b> A field the contract names but the payload
/// omits is recorded in <see cref="MissingFields"/> and the affected entity
/// or event is excluded — never defaulted to a guessed value.
/// </description></item>
/// <item><description>
/// <b>Fog.</b> An entity owned by a player other than the controlled one is
/// included only when its payload says <c>visible == true</c>. When the
/// payload lacks a visibility flag entirely, the entity is excluded and the
/// omission is recorded, so engine-omniscient state can never leak into a
/// <see cref="ObservationMode.Belief"/> frame through a telemetry gap. A
/// sighting counts only for the frame whose window holds it, visible regions
/// come from own entities alone, and an enemy's removal is reported only when
/// the player saw it (see <see cref="Ra2BotTelemetryContract"/>).
/// </description></item>
/// </list>
///
/// Frames are emitted every time the highest frame number seen across
/// ingested events crosses a multiple of <c>frameCadence</c>. The default of
/// <see cref="GameTime.FramesPerSecond"/> (one second of game time) is a
/// conventional choice for a bridge whose telemetry is event-driven rather
/// than per-engine-frame; callers running faster tactical loops pass a
/// smaller cadence.
/// </summary>
public sealed class Ra2ObservationAssembler
{
    private readonly PlayerId self;
    private readonly Faction faction;
    private readonly MapInfo map;
    private readonly long frameCadence;
    private readonly HashSet<PlayerId> nonHostileOwners;
    private readonly Dictionary<EntityId, EntityState> entities = [];
    private readonly List<GameEvent> pendingEvents = [];
    private readonly List<MissingFieldEntry> missingFields = [];

    private int credits;
    private PowerState power = new(0, 0);
    private long latestFrame = -1;
    private long nextBoundary;
    private long lastFrameTime = -1;
    private bool matchEnded;

    /// <param name="self">The controlled player this assembler builds frames for.</param>
    /// <param name="faction">
    /// The controlled player's faction. Not derivable from the telemetry
    /// fields this contract version carries, so it is supplied by the caller.
    /// </param>
    /// <param name="map">Static map knowledge, supplied by the caller (see <see cref="MapInfoLoader"/>).</param>
    /// <param name="frameCadence">
    /// Frames between emitted <see cref="ObservationFrame"/>s. Must be positive.
    /// </param>
    /// <param name="nonHostileOwners">
    /// Players that are not enemies of the controlled one: allies in a team
    /// game and the neutral/civilian/special houses. The bot's frame contract
    /// treats every other owner as an enemy and v1 telemetry carries no
    /// alliance field, so the caller supplies them from the match setup
    /// (like <paramref name="faction"/>). Their entities never enter a frame.
    /// </param>
    public Ra2ObservationAssembler(PlayerId self, Faction faction, MapInfo map, int frameCadence = GameTime.FramesPerSecond, IEnumerable<PlayerId>? nonHostileOwners = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (frameCadence <= 0) throw new ArgumentOutOfRangeException(nameof(frameCadence), frameCadence, "frame cadence must be positive");
        this.self = self;
        this.faction = faction;
        this.map = map;
        this.frameCadence = frameCadence;
        this.nonHostileOwners = [.. nonHostileOwners ?? []];
        if (this.nonHostileOwners.Contains(self)) throw new ArgumentException("the controlled player cannot be listed as a non-hostile owner", nameof(nonHostileOwners));
        nextBoundary = frameCadence;
    }

    /// <summary>Every telemetry field expected but not received, since construction.</summary>
    public MissingFieldsReport MissingFields => new([.. missingFields]);

    /// <summary>
    /// Folds one normalized event into tracked state. Returns zero or more
    /// frames: normally zero (no cadence boundary crossed) or one, but more
    /// than one when a gap in telemetry frame numbers jumps past several
    /// boundaries at once.
    /// </summary>
    /// <remarks>
    /// Boundaries strictly before the event's frame are emitted <i>before</i>
    /// the event is applied, so a frame stamped with time T never carries
    /// state or events from after T (belief would otherwise stamp a sighting
    /// earlier than it happened, and event windows would see the future).
    /// The frames emitted for a gap therefore hold the state as of the last
    /// event before it and no new events; the event itself lands in the
    /// first frame at or after its own time. <c>game.lifecycle.ended</c> is
    /// the exception that ends the stream: it emits a final frame at once, and
    /// every later event is ignored.
    /// </remarks>
    public IReadOnlyList<ObservationFrame> Ingest(NormalizedObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (matchEnded) return []; // nothing after the end of the match is part of it
        if (!TryGetLong(observation.Payload, Ra2BotTelemetryContract.FieldFrame, observation, "event_skipped", out long frame)) return [];

        List<ObservationFrame> emitted = [];
        EmitBoundaries(frame - 1, emitted);
        latestFrame = Math.Max(latestFrame, frame);
        Apply(observation, frame);
        EmitBoundaries(latestFrame, emitted);
        if (matchEnded && pendingEvents.Count > 0)
        {
            // The match end is the last telemetry there will be, so waiting for the next boundary would hold it
            // (and any defeat or destruction since the last boundary) back forever: flush a final frame now.
            emitted.Add(BuildFrame(new GameTime(latestFrame)));
            pendingEvents.Clear();
        }
        return emitted;
    }

    /// <summary>Emits one frame per cadence boundary at or before <paramref name="upTo"/>.</summary>
    private void EmitBoundaries(long upTo, List<ObservationFrame> emitted)
    {
        while (upTo >= nextBoundary)
        {
            emitted.Add(BuildFrame(new GameTime(nextBoundary)));
            pendingEvents.Clear();
            nextBoundary += frameCadence;
        }
    }

    private void Apply(NormalizedObservation observation, long frame)
    {
        switch (observation.EventType)
        {
            case "game.unit.created":
            case "game.building.placed":
                ApplyEntityUpsert(observation, frame);
                break;
            case "game.unit.destroyed":
            case "game.building.destroyed":
            case "game.unit.killed":
                ApplyEntityRemoval(observation, frame);
                break;
            case "game.economy.credits":
                ApplyCredits(observation);
                break;
            case "game.economy.power":
                ApplyPower(observation);
                break;
            case "game.participant.defeated":
                ApplyPlayerDefeated(observation, frame);
                break;
            case "game.lifecycle.ended":
                pendingEvents.Add(new GameEvent(GameEventKind.MatchEnded, new GameTime(frame), null, null, null, null));
                matchEnded = true;
                break;
            default:
                // Not yet part of bindery.ra2.bot-observation/v1; ignored, not reported missing.
                break;
        }
    }

    private void ApplyEntityUpsert(NormalizedObservation observation, long frame)
    {
        JsonElement payload = observation.Payload;
        bool ok = true;
        ok &= TryGetPlayerId(payload, Ra2BotTelemetryContract.FieldOwner, observation, "entity_excluded", out PlayerId owner);
        ok &= TryGetEntityId(payload, Ra2BotTelemetryContract.FieldId, observation, "entity_excluded", out EntityId id);
        ok &= TryGetString(payload, Ra2BotTelemetryContract.FieldType, observation, "entity_excluded", out string typeId);
        ok &= TryGetCell(payload, observation, "entity_excluded", out Cell position);
        ok &= TryGetInt(payload, Ra2BotTelemetryContract.FieldHealth, observation, "entity_excluded", out int health);
        ok &= TryGetInt(payload, Ra2BotTelemetryContract.FieldMaxHealth, observation, "entity_excluded", out int maxHealth);
        if (!ok) return;

        if (nonHostileOwners.Contains(owner))
        {
            // An ally or a neutral house is not an enemy contact; the frame contract has no place for it. An entity
            // that changed hands to such an owner leaves tracked state too.
            entities.Remove(id);
            return;
        }

        if (owner != self)
        {
            // Fog boundary: an enemy entity is included only when the payload
            // itself asserts visibility. Missing or false both exclude it;
            // only "missing" is a contract violation worth reporting.
            bool present = TryGetBool(payload, Ra2BotTelemetryContract.FieldVisible, observation, "entity_excluded", out bool visible);
            if (!present || !visible)
            {
                // No longer observed: it leaves the frame (belief keeps its memory), and a later removal of it is
                // a death in fog, which the player does not see.
                entities.Remove(id);
                return;
            }
        }

        // A repeated upsert of a tracked entity is a state refresh (position, health, owner): the source re-sends it
        // to keep that state current, so it is not a second creation.
        bool tracked = entities.ContainsKey(id);
        entities[id] = new EntityState(id, owner, typeId, position, health, maxHealth, frame);
        if (!tracked) pendingEvents.Add(new GameEvent(GameEventKind.EntityCreated, new GameTime(frame), id, owner, typeId, position));
    }

    private void ApplyEntityRemoval(NormalizedObservation observation, long frame)
    {
        if (!TryGetEntityId(observation.Payload, Ra2BotTelemetryContract.FieldId, observation, "removal_skipped", out EntityId id)) return;

        JsonElement payload = observation.Payload;
        entities.Remove(id, out EntityState? removed);
        bool own = removed is { } r && r.Owner == self;
        bool killedByUs = !own && payload.TryGetProperty(Ra2BotTelemetryContract.FieldKiller, out JsonElement k)
            && k.ValueKind == JsonValueKind.Number && k.TryGetInt32(out int killer) && killer == self.Value;
        if (!own)
        {
            // Fog boundary: an enemy's removal is reported only for an entity we have observed (never a bare id we
            // never saw, and never one that went out of sight since), and only when the player saw it happen: the
            // payload says visible, or we are the killer. A missing flag is a contract gap, recorded and not guessed.
            if (removed is null) return;
            if (!killedByUs)
            {
                bool present = TryGetBool(payload, Ra2BotTelemetryContract.FieldVisible, observation, "removal_skipped", out bool visible);
                if (!present || !visible) return;
            }
        }

        // A kill is ours only when the payload names us as the killer; the event name alone says nothing about who
        // killed what, and an own unit's death is always a loss.
        GameEventKind kind = killedByUs ? GameEventKind.EntityKilledByUs : GameEventKind.EntityDestroyed;
        pendingEvents.Add(new GameEvent(kind, new GameTime(frame), id, removed?.Owner, removed?.TypeId, removed?.Position));
    }

    private void ApplyCredits(NormalizedObservation observation)
    {
        if (!TryGetPlayerId(observation.Payload, Ra2BotTelemetryContract.FieldOwner, observation, "credits_skipped", out PlayerId owner)) return;
        if (owner != self) return; // another player's economy sample; not our field to report missing
        if (!TryGetInt(observation.Payload, Ra2BotTelemetryContract.FieldCredits, observation, "credits_skipped", out int value)) return;
        credits = value;
    }

    private void ApplyPower(NormalizedObservation observation)
    {
        if (!TryGetPlayerId(observation.Payload, Ra2BotTelemetryContract.FieldOwner, observation, "power_skipped", out PlayerId owner)) return;
        if (owner != self) return;
        bool ok = true;
        ok &= TryGetInt(observation.Payload, Ra2BotTelemetryContract.FieldProduced, observation, "power_skipped", out int produced);
        ok &= TryGetInt(observation.Payload, Ra2BotTelemetryContract.FieldDrained, observation, "power_skipped", out int drained);
        if (!ok) return;
        power = new PowerState(produced, drained);
    }

    private void ApplyPlayerDefeated(NormalizedObservation observation, long frame)
    {
        if (!TryGetPlayerId(observation.Payload, Ra2BotTelemetryContract.FieldOwner, observation, "event_skipped", out PlayerId owner)) return;
        pendingEvents.Add(new GameEvent(GameEventKind.PlayerDefeated, new GameTime(frame), null, owner, null, null));
    }

    private ObservationFrame BuildFrame(GameTime time)
    {
        // Fog: an own entity is always observed; an enemy only when telemetry sighted it in this frame's window.
        // A sighting from an earlier window is memory, which belief keeps and decays; the bridge must not present
        // it as seen now.
        List<ObservedEntity> observedEntities = entities.Values
            .Where(e => e.Owner == self || e.LastSightedFrame > lastFrameTime)
            .OrderBy(static e => e.Id.Value)
            .Select(static e => new ObservedEntity(e.Id, e.Owner, e.TypeId, e.Position, e.Health, e.MaxHealth))
            .ToList();
        lastFrameTime = time.Frame;

        // What the player sees is around its own units and buildings; where an enemy stands says nothing about
        // whether that region is in sight (the enemy may be seen at the edge of our vision).
        HashSet<RegionId> visibleRegions = [];
        foreach (ObservedEntity entity in observedEntities)
        {
            if (entity.Owner != self) continue;
            Region? region = map.RegionOf(entity.Position);
            if (region is not null) visibleRegions.Add(region.Id);
        }

        return new ObservationFrame(
            time,
            ObservationMode.Belief,
            self,
            faction,
            credits,
            power,
            observedEntities,
            [],
            [.. pendingEvents],
            visibleRegions,
            map);
    }

    private void RecordMissing(NormalizedObservation observation, string field, string effect) =>
        missingFields.Add(new MissingFieldEntry(observation.EventType, field, effect, observation.DerivedEventId));

    private bool TryGetLong(JsonElement payload, string field, NormalizedObservation observation, string effect, out long value)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(field, out JsonElement element) && element.ValueKind == JsonValueKind.Number)
        {
            if (element.TryGetInt64(out value)) return true;
            if (element.TryGetDouble(out double d) && Math.Abs(d) < 9.2e18) { value = (long)Math.Round(d); return true; }
        }
        value = 0;
        RecordMissing(observation, field, effect);
        return false;
    }

    // Narrowing casts are unchecked in C#, so an out-of-range payload value would wrap into a valid-looking one (an
    // id aliasing another tracked entity, a health turning negative). It is a contract violation: recorded with an
    // "_out_of_range" effect and the entity or event excluded, like a missing field.
    private bool TryGetInt(JsonElement payload, string field, NormalizedObservation observation, string effect, out int value)
    {
        if (TryGetLong(payload, field, observation, effect, out long raw))
        {
            if (raw is >= int.MinValue and <= int.MaxValue) { value = (int)raw; return true; }
            RecordMissing(observation, field, effect + " (out_of_range)");
        }
        value = 0;
        return false;
    }

    private bool TryGetDouble(JsonElement payload, string field, NormalizedObservation observation, string effect, out double value)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(field, out JsonElement element) && element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value))
            return true;
        value = 0;
        RecordMissing(observation, field, effect);
        return false;
    }

    private bool TryGetString(JsonElement payload, string field, NormalizedObservation observation, string effect, out string value)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(field, out JsonElement element) && element.ValueKind == JsonValueKind.String)
        {
            string? s = element.GetString();
            if (!string.IsNullOrEmpty(s)) { value = s; return true; }
        }
        value = string.Empty;
        RecordMissing(observation, field, effect);
        return false;
    }

    private bool TryGetBool(JsonElement payload, string field, NormalizedObservation observation, string effect, out bool value)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(field, out JsonElement element) &&
            element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetBoolean();
            return true;
        }
        value = false;
        RecordMissing(observation, field, effect);
        return false;
    }

    private bool TryGetEntityId(JsonElement payload, string field, NormalizedObservation observation, string effect, out EntityId id)
    {
        if (TryGetLong(payload, field, observation, effect, out long raw))
        {
            if (raw is >= 0 and <= uint.MaxValue) { id = new EntityId((uint)raw); return true; }
            RecordMissing(observation, field, effect + " (out_of_range)");
        }
        id = default;
        return false;
    }

    private bool TryGetPlayerId(JsonElement payload, string field, NormalizedObservation observation, string effect, out PlayerId playerId)
    {
        if (TryGetInt(payload, field, observation, effect, out int raw)) { playerId = new PlayerId(raw); return true; }
        playerId = default;
        return false;
    }

    private bool TryGetCell(JsonElement payload, NormalizedObservation observation, string effect, out Cell cell)
    {
        bool hasX = TryGetDouble(payload, Ra2BotTelemetryContract.FieldX, observation, effect, out double x);
        bool hasY = TryGetDouble(payload, Ra2BotTelemetryContract.FieldY, observation, effect, out double y);
        if (!hasX || !hasY) { cell = default; return false; }
        if (Math.Abs(x) > int.MaxValue || Math.Abs(y) > int.MaxValue)
        {
            RecordMissing(observation, Math.Abs(x) > int.MaxValue ? Ra2BotTelemetryContract.FieldX : Ra2BotTelemetryContract.FieldY, effect + " (out_of_range)");
            cell = default;
            return false;
        }
        cell = new Cell((int)Math.Round(x), (int)Math.Round(y));
        return true;
    }

    /// <param name="LastSightedFrame">Frame of the latest upsert: for an enemy, the latest sighting.</param>
    private sealed record EntityState(EntityId Id, PlayerId Owner, string TypeId, Cell Position, int Health, int MaxHealth, long LastSightedFrame);
}
