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
/// <see cref="ObservationMode.Belief"/> frame through a telemetry gap.
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
    private readonly Dictionary<EntityId, EntityState> entities = [];
    private readonly List<GameEvent> pendingEvents = [];
    private readonly List<MissingFieldEntry> missingFields = [];

    private int credits;
    private PowerState power = new(0, 0);
    private long latestFrame = -1;
    private long nextBoundary;

    /// <param name="self">The controlled player this assembler builds frames for.</param>
    /// <param name="faction">
    /// The controlled player's faction. Not derivable from the telemetry
    /// fields this contract version carries, so it is supplied by the caller.
    /// </param>
    /// <param name="map">Static map knowledge, supplied by the caller (see <see cref="MapInfoLoader"/>).</param>
    /// <param name="frameCadence">
    /// Frames between emitted <see cref="ObservationFrame"/>s. Must be positive.
    /// </param>
    public Ra2ObservationAssembler(PlayerId self, Faction faction, MapInfo map, int frameCadence = GameTime.FramesPerSecond)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (frameCadence <= 0) throw new ArgumentOutOfRangeException(nameof(frameCadence), frameCadence, "frame cadence must be positive");
        this.self = self;
        this.faction = faction;
        this.map = map;
        this.frameCadence = frameCadence;
        nextBoundary = frameCadence;
    }

    /// <summary>Every telemetry field expected but not received, since construction.</summary>
    public MissingFieldsReport MissingFields => new([.. missingFields]);

    /// <summary>
    /// Folds one normalized event into tracked state. Returns zero or more
    /// frames: normally zero (no cadence boundary crossed) or one, but more
    /// than one when a gap in telemetry frame numbers jumps past several
    /// boundaries at once — each such filler frame carries no new events.
    /// </summary>
    public IReadOnlyList<ObservationFrame> Ingest(NormalizedObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        Apply(observation);

        if (latestFrame < nextBoundary) return [];
        List<ObservationFrame> emitted = [];
        while (latestFrame >= nextBoundary)
        {
            emitted.Add(BuildFrame(new GameTime(nextBoundary)));
            pendingEvents.Clear();
            nextBoundary += frameCadence;
        }
        return emitted;
    }

    private void Apply(NormalizedObservation observation)
    {
        if (!TryGetLong(observation.Payload, Ra2BotTelemetryContract.FieldFrame, observation, "event_skipped", out long frame)) return;
        latestFrame = Math.Max(latestFrame, frame);

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

        if (owner != self)
        {
            // Fog boundary: an enemy entity is included only when the payload
            // itself asserts visibility. Missing or false both exclude it;
            // only "missing" is a contract violation worth reporting.
            bool present = TryGetBool(payload, Ra2BotTelemetryContract.FieldVisible, observation, "entity_excluded", out bool visible);
            if (!present || !visible) return;
        }

        entities[id] = new EntityState(id, owner, typeId, position, health, maxHealth);
        pendingEvents.Add(new GameEvent(GameEventKind.EntityCreated, new GameTime(frame), id, owner, typeId, position));
    }

    private void ApplyEntityRemoval(NormalizedObservation observation, long frame)
    {
        if (!TryGetEntityId(observation.Payload, Ra2BotTelemetryContract.FieldId, observation, "removal_skipped", out EntityId id)) return;

        entities.Remove(id, out EntityState? removed);
        GameEventKind kind = observation.EventType == "game.unit.killed" ? GameEventKind.EntityKilledByUs : GameEventKind.EntityDestroyed;
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
        List<ObservedEntity> observedEntities = entities.Values
            .OrderBy(static e => e.Id.Value)
            .Select(static e => new ObservedEntity(e.Id, e.Owner, e.TypeId, e.Position, e.Health, e.MaxHealth))
            .ToList();

        HashSet<RegionId> visibleRegions = [];
        foreach (ObservedEntity entity in observedEntities)
        {
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
            if (element.TryGetDouble(out double d)) { value = (long)Math.Round(d); return true; }
        }
        value = 0;
        RecordMissing(observation, field, effect);
        return false;
    }

    private bool TryGetInt(JsonElement payload, string field, NormalizedObservation observation, string effect, out int value)
    {
        if (TryGetLong(payload, field, observation, effect, out long raw)) { value = (int)raw; return true; }
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
        if (TryGetLong(payload, field, observation, effect, out long raw) && raw >= 0) { id = new EntityId((uint)raw); return true; }
        id = default;
        return false;
    }

    private bool TryGetPlayerId(JsonElement payload, string field, NormalizedObservation observation, string effect, out PlayerId playerId)
    {
        if (TryGetLong(payload, field, observation, effect, out long raw)) { playerId = new PlayerId((int)raw); return true; }
        playerId = default;
        return false;
    }

    private bool TryGetCell(JsonElement payload, NormalizedObservation observation, string effect, out Cell cell)
    {
        bool hasX = TryGetDouble(payload, Ra2BotTelemetryContract.FieldX, observation, effect, out double x);
        bool hasY = TryGetDouble(payload, Ra2BotTelemetryContract.FieldY, observation, effect, out double y);
        if (!hasX || !hasY) { cell = default; return false; }
        cell = new Cell((int)Math.Round(x), (int)Math.Round(y));
        return true;
    }

    private sealed record EntityState(EntityId Id, PlayerId Owner, string TypeId, Cell Position, int Health, int MaxHealth);
}
