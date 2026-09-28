// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ra2Yrproto.Commands;
using Ra2Yrproto.Ra2Yr;

namespace Bindery.Ra2.Adapter.Ra2yrcpp;

/// <summary>One adapter event derived from a snapshot change, before it becomes a <see cref="RawObservation"/>.</summary>
public sealed record Ra2yrcppEvent(string EventType, JsonElement Payload);

/// <summary>
/// Turns successive <c>GameState</c> snapshots into the adapter's existing
/// <c>ra2.*</c> events.
/// </summary>
/// <remarks>
/// A snapshot is a spectator's view of every house. Owners come from
/// <c>Object.pointer_house</c> joined to <c>House.self</c>, and a house is
/// named by <c>House.name</c>. The snapshot carries no per-object visibility,
/// so no event claims any: an object, credit or power event says only that
/// its own house sees it (<c>visible_to</c> = [owner]), and the
/// <see cref="Channel.PlayerObservationFilter"/> withholds it from every other
/// seat. With <see cref="SeatHouse"/> set, other houses' object, credit and
/// power events are not emitted at all; match lifecycle, joins and defeats
/// are public either way.
///
/// Objects are keyed by address, owner, type and stable ID
/// (<c>Object.unique_id</c>, emitted as <c>unique_id</c> when the fork sends
/// one), so a change of owner (mind control) or a recycled address reads as
/// one object gone and another new.
/// Objects in limbo -- a finished building still in its factory, a unit in a
/// transport -- are off the map and count as absent. Credits are sampled on
/// change and at least every heartbeat, so a flat economy still shows.
///
/// Each house's factory items are reported as <c>ra2.production.changed</c>
/// when one appears, crosses a 10% progress step, holds or resumes,
/// finishes, or leaves the factory (<c>gone</c>), and as
/// <c>ra2.production.completed</c> once when it finishes. The item is typed
/// through its limbo object's type class; its address is this client's
/// memory, so it is never emitted.
/// </remarks>
public sealed class Ra2yrcppSnapshotDiff
{
    private readonly HashSet<string> nonPlayer;
    private readonly TimeSpan heartbeat;
    private readonly TimeSpan grace;
    private DateTimeOffset? overSince;
    private uint lastFrame;
    private readonly Dictionary<uint, string> typeNames = [];
    private readonly Dictionary<(uint Address, uint Owner, uint Type, uint UniqueId), TrackedObject> objects = [];
    private readonly Dictionary<string, (long Credits, DateTimeOffset At)> credits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Output, int Drain)> power = new(StringComparer.Ordinal);
    private readonly HashSet<string> defeated = new(StringComparer.Ordinal);
    private readonly Dictionary<(uint Owner, uint Item), Production> production = [];

    /// <summary>The fork's production steps for a finished item (<c>cfg::PRODUCTION_STEPS</c>).</summary>
    private const int ProductionSteps = 54;

    public Ra2yrcppSnapshotDiff(string? seatHouse = null, IEnumerable<string>? nonPlayerHouses = null, TimeSpan? creditsHeartbeat = null, TimeSpan? winnerGrace = null)
    {
        if (seatHouse is not null) ArgumentException.ThrowIfNullOrWhiteSpace(seatHouse);
        SeatHouse = seatHouse;
        nonPlayer = new HashSet<string>(nonPlayerHouses ?? ["Special", "Neutral"], StringComparer.Ordinal);
        heartbeat = creditsHeartbeat ?? TimeSpan.FromSeconds(10);
        grace = winnerGrace ?? TimeSpan.FromSeconds(5);
    }

    public string? SeatHouse { get; }

    public bool Started { get; private set; }

    public bool Ended { get; private set; }

    public bool HasTypes => typeNames.Count > 0;

    /// <summary>The game is over for this client but no winner is flagged yet: the diff is waiting out the grace.</summary>
    public bool EndPending => overSince is not null && !Ended;

    /// <summary>
    /// Ends a match whose end is pending because the stream stopped (the
    /// client closed) during the winner grace; nothing otherwise.
    /// </summary>
    public IReadOnlyList<Ra2yrcppEvent> Finish() => EndPending ? [End([])] : [];

    /// <summary>Type classes from <c>ReadValue</c>'s initial game state; per-frame snapshots do not repeat them.</summary>
    public void SetTypes(IEnumerable<ObjectTypeClass> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        // The rules ID ("AMCV") when the fork reports one; name is the INI
        // Name= display text ("Allied Construction Vehicle") on live builds.
        foreach (ObjectTypeClass type in types) typeNames[type.PointerSelf] = string.IsNullOrEmpty(type.Id) ? type.Name : type.Id;
    }

    public IReadOnlyList<Ra2yrcppEvent> Next(GameState state, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(state);
        List<Ra2yrcppEvent> events = [];
        if (Ended) return events;
        if (!Started)
        {
            if (state.Stage != LoadStage.StageIngame) return events;
            Started = true;
            events.Add(new(Ra2TelemetryEventTypes.MatchStarted, Payload(new JsonObject { ["frame"] = state.CurrentFrame })));
            foreach (House house in Players(state))
                events.Add(new(Ra2TelemetryEventTypes.PlayerJoined, Payload(new JsonObject { ["house"] = house.Name })));
        }

        Dictionary<uint, string> owners = state.Houses.GroupBy(static h => h.Self).ToDictionary(static g => g.Key, static g => g.First().Name);
        foreach (House house in Players(state).Where(h => h.Defeated && defeated.Add(h.Name)))
            events.Add(new(Ra2TelemetryEventTypes.PlayerDefeated, Payload(new JsonObject { ["house"] = house.Name })));

        Dictionary<(uint, uint, uint, uint), TrackedObject> current = [];
        foreach (Ra2Yrproto.Ra2Yr.Object item in state.Objects)
        {
            // An object whose owner is not a known house cannot be attributed, so it is not reported.
            if (item.InLimbo || !owners.TryGetValue(item.PointerHouse, out string? owner)) continue;
            current[(item.PointerSelf, item.PointerHouse, item.PointerTechnotypeclass, item.UniqueId)] = new TrackedObject(item, owner, typeNames.GetValueOrDefault(item.PointerTechnotypeclass));
        }
        foreach ((var key, TrackedObject gone) in objects.Where(pair => !current.ContainsKey(pair.Key)).OrderBy(static pair => pair.Key.Address))
        {
            objects.Remove(key);
            if (Scoped(gone.Owner)) events.Add(ObjectEvent(gone.IsBuilding ? Ra2TelemetryEventTypes.BuildingDestroyed : Ra2TelemetryEventTypes.UnitDestroyed, gone));
        }
        foreach ((var key, TrackedObject added) in current.Where(pair => !objects.ContainsKey(pair.Key)).OrderBy(static pair => pair.Key.Item1))
        {
            objects[key] = added;
            if (Scoped(added.Owner)) events.Add(ObjectEvent(added.IsBuilding ? Ra2TelemetryEventTypes.BuildingPlaced : Ra2TelemetryEventTypes.UnitCreated, added));
        }

        events.AddRange(ProductionEvents(state, owners));

        foreach (House house in Players(state).Where(h => Scoped(h.Name)))
        {
            if (!credits.TryGetValue(house.Name, out var last) || last.Credits != house.Money || at - last.At >= heartbeat)
            {
                credits[house.Name] = (house.Money, at);
                events.Add(new(Ra2TelemetryEventTypes.CreditsSampled, Payload(new JsonObject { ["house"] = house.Name, ["credits"] = house.Money, ["visible_to"] = new JsonArray(house.Name) })));
            }
            if (!power.TryGetValue(house.Name, out var sampled) || sampled != (house.PowerOutput, house.PowerDrain))
            {
                power[house.Name] = (house.PowerOutput, house.PowerDrain);
                events.Add(new(Ra2TelemetryEventTypes.PowerSampled, Payload(new JsonObject { ["house"] = house.Name, ["output"] = house.PowerOutput, ["drain"] = house.PowerDrain, ["visible_to"] = new JsonArray(house.Name) })));
            }
        }

        // The match is over when the game names a winner. When only this
        // client's own game is over (it lost, or is leaving), the winner may
        // not be flagged yet, so the diff keeps reading for the grace period
        // before it ends the match without one.
        lastFrame = state.CurrentFrame;
        House[] winners = Players(state).Where(static h => h.IsWinner).ToArray();
        if (winners.Length > 0)
        {
            events.Add(End(winners));
        }
        else if (state.Houses.Any(static h => h.CurrentPlayer && h.IsGameOver) || state.Stage == LoadStage.StageExitGame)
        {
            overSince ??= at;
            if (at - overSince.Value >= grace) events.Add(End([]));
        }
        return events;
    }

    private List<Ra2yrcppEvent> ProductionEvents(GameState state, Dictionary<uint, string> owners)
    {
        List<Ra2yrcppEvent> events = [];
        Dictionary<uint, uint> typeOf = state.Objects.GroupBy(static o => o.PointerSelf).ToDictionary(static g => g.Key, static g => g.First().PointerTechnotypeclass);
        Dictionary<(uint, uint), Production> current = [];
        foreach (Factory factory in state.Factories)
        {
            if (factory.Object == 0 || !owners.TryGetValue(factory.Owner, out string? owner) || nonPlayer.Contains(owner) || !Scoped(owner)) continue;
            // An item whose type cannot be read yet is picked up on a later snapshot.
            if (!typeOf.TryGetValue(factory.Object, out uint pointer) || !typeNames.TryGetValue(pointer, out string? type)) continue;
            int progress = factory.Completed ? 100 : Math.Clamp(factory.ProgressTimer * 100 / ProductionSteps / 10 * 10, 0, 90);
            // The stable ID keys the item when there is one; the address only stands in, internally.
            current[(factory.Owner, factory.ObjectUniqueId != 0 ? factory.ObjectUniqueId : factory.Object)] = new Production(owner, type, progress, factory.OnHold, factory.Completed, factory.ObjectUniqueId);
        }
        foreach ((var key, Production gone) in production.Where(pair => !current.ContainsKey(pair.Key)).OrderBy(static pair => pair.Key).ToArray())
        {
            production.Remove(key);
            events.Add(ProductionEvent(Ra2TelemetryEventTypes.ProductionChanged, gone, gone: true));
        }
        foreach ((var key, Production item) in current.OrderBy(static pair => pair.Key))
        {
            production.TryGetValue(key, out Production? last);
            production[key] = item;
            if (last != item) events.Add(ProductionEvent(Ra2TelemetryEventTypes.ProductionChanged, item));
            if (item.Completed && last is not { Completed: true }) events.Add(ProductionEvent(Ra2TelemetryEventTypes.ProductionCompleted, item));
        }
        return events;
    }

    private static Ra2yrcppEvent ProductionEvent(string eventType, Production item, bool gone = false)
    {
        JsonObject payload = new()
        {
            ["house"] = item.Owner,
            ["type"] = item.Type,
            ["progress"] = item.Progress,
            ["on_hold"] = item.OnHold,
            ["completed"] = item.Completed,
        };
        if (item.UniqueId != 0) payload["unique_id"] = item.UniqueId;
        if (gone) payload["gone"] = true;
        payload["visible_to"] = new JsonArray(item.Owner);
        return new(eventType, Payload(payload));
    }

    private Ra2yrcppEvent End(House[] winners)
    {
        Ended = true;
        JsonObject ended = new() { ["frame"] = lastFrame };
        if (winners.Length == 1) ended["winner"] = winners[0].Name;
        return new(Ra2TelemetryEventTypes.MatchEnded, Payload(ended));
    }

    private IEnumerable<House> Players(GameState state) => state.Houses.Where(h => !string.IsNullOrEmpty(h.Name) && !nonPlayer.Contains(h.Name));

    private bool Scoped(string owner) => SeatHouse is null || string.Equals(owner, SeatHouse, StringComparison.Ordinal);

    private static Ra2yrcppEvent ObjectEvent(string eventType, TrackedObject item)
    {
        JsonObject payload = new()
        {
            ["house"] = item.Owner,
            ["object"] = item.Object.PointerSelf,
            ["kind"] = item.Object.ObjectType.ToString().ToLowerInvariant(),
        };
        if (item.Type is { } type) payload["type"] = type;
        // Older fork builds send no stable ID; none is invented.
        if (item.Object.UniqueId != 0) payload["unique_id"] = item.Object.UniqueId;
        if (item.Object.Coordinates is { } at)
        {
            payload["x"] = at.X;
            payload["y"] = at.Y;
            payload["z"] = at.Z;
        }
        payload["visible_to"] = new JsonArray(item.Owner);
        return new(eventType, Payload(payload));
    }

    private static JsonElement Payload(JsonObject payload) => JsonSerializer.SerializeToElement(payload);

    private sealed record Production(string Owner, string Type, int Progress, bool OnHold, bool Completed, uint UniqueId);

    private sealed record TrackedObject(Ra2Yrproto.Ra2Yr.Object Object, string Owner, string? Type)
    {
        public bool IsBuilding => Object.ObjectType == AbstractType.Building;
    }
}

/// <param name="PollInterval">How often to read <c>GetGameState</c>; default 500 ms.</param>
/// <param name="ConnectRetry">How long to wait between attempts while the service is not up; default 2 s.</param>
/// <param name="SeatHouse">Emit only this house's object, credit and power events; null for the spectator view.</param>
/// <param name="MaximumConsecutiveFailures">Failed reads in a row, once the match started, before the stream fails.</param>
/// <param name="WinnerGrace">
/// How long to keep reading for a winner once this client's own game is
/// over; default 5 s. The observer client's service is the preferred
/// endpoint: its game is not over when a player's is.
/// </param>
public sealed record Ra2yrcppTelemetryOptions(
    TimeSpan? PollInterval = null,
    TimeSpan? ConnectRetry = null,
    string? SeatHouse = null,
    TimeSpan? CreditsHeartbeat = null,
    int MaximumConsecutiveFailures = 5,
    Ra2yrcppClientOptions? Client = null,
    string? CaptureId = null,
    IReadOnlyList<string>? NonPlayerHouses = null,
    TimeSpan? WinnerGrace = null);

/// <summary>
/// Live telemetry from a game client's ra2yrcpp service: polls
/// <c>GetGameState</c> and yields what changed as raw observations.
/// </summary>
/// <remarks>
/// The service comes up with the game, so until the match starts the source
/// keeps retrying the connection. No event is emitted until the type classes
/// (<c>ReadValue</c>'s initial game state) have loaded; if they keep coming
/// back empty, the stream fails rather than report objects without types. Once it has started, a connection that
/// keeps failing ends the stream with an error rather than silently opening
/// a gap. The stream ends after <c>ra2.match.ended</c>.
/// </remarks>
public sealed class Ra2yrcppTelemetrySource : IRa2TelemetrySource
{
    private readonly Ra2YrcppEndpoint endpoint;
    private readonly Ra2yrcppTelemetryOptions options;
    private readonly TimeProvider clock;
    private readonly string captureId;
    private long read;

    public Ra2yrcppTelemetrySource(Ra2YrcppEndpoint endpoint, Ra2yrcppTelemetryOptions? options = null, TimeProvider? clock = null)
    {
        this.endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        this.options = options ?? new Ra2yrcppTelemetryOptions();
        if (this.options.MaximumConsecutiveFailures < 1) throw new ArgumentOutOfRangeException(nameof(options), "at least one failure is allowed");
        this.clock = clock ?? TimeProvider.System;
        captureId = this.options.CaptureId ?? "ra2yrcpp-" + Guid.NewGuid().ToString("N");
    }

    public Uri Uri => Ra2yrcppClient.UriFor(endpoint);

    public Ra2TelemetryCapture Capture => new(
        "ra2yrcpp-websocket",
        endpoint,
        Interlocked.Read(ref read) > 0,
        Interlocked.Read(ref read),
        null);

    public async IAsyncEnumerable<RawObservation> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        TimeSpan poll = options.PollInterval ?? TimeSpan.FromMilliseconds(500);
        TimeSpan retry = options.ConnectRetry ?? TimeSpan.FromSeconds(2);
        Ra2yrcppSnapshotDiff diff = new(options.SeatHouse, options.NonPlayerHouses, options.CreditsHeartbeat, options.WinnerGrace);
        Ra2yrcppClient? client = null;
        int failures = 0, typeless = 0;
        ulong sequence = 0;
        try
        {
            while (true)
            {
                IReadOnlyList<Ra2yrcppEvent> events = [];
                bool typesMissing = false;
                try
                {
                    if (client is not { IsOpen: true })
                    {
                        if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
                        client = null;
                        client = await Ra2yrcppClient.ConnectAsync(Uri, options.Client, cancellationToken).ConfigureAwait(false);
                    }
                    GameState state = (await client.RunAsync(new GetGameState(), cancellationToken).ConfigureAwait(false)).State ?? new GameState();
                    if (!diff.HasTypes && state.Stage == LoadStage.StageIngame)
                    {
                        ReadValue types = await client.RunAsync(new ReadValue { Data = new StorageValue { InitialGameState = new GameState() } }, cancellationToken).ConfigureAwait(false);
                        diff.SetTypes(types.Data?.InitialGameState?.ObjectTypes ?? []);
                        // Without type names, object events would carry no
                        // type and routines keyed on it (the MCV deploy)
                        // would never act. Hold every event until they load.
                        typesMissing = !diff.HasTypes;
                    }
                    if (!typesMissing) events = diff.Next(state, clock.GetUtcNow());
                    failures = 0;
                }
                catch (Exception exception) when (exception is WebSocketException or TimeoutException or IOException or Ra2yrcppCommandException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
                {
                    // Before the match the service may simply not be up yet.
                    // IOException covers a malformed frame
                    // (InvalidProtocolBufferException) as well as
                    // InvalidDataException; the client has already dropped
                    // that connection, and the next read reconnects.
                    // A client that closes while the end is pending (the
                    // loser's game exiting) ends the match with what is known.
                    if (diff.EndPending)
                    {
                        events = diff.Finish();
                    }
                    else if (diff.Started && ++failures >= options.MaximumConsecutiveFailures)
                    {
                        throw new IOException($"ra2yrcpp telemetry at {Uri} failed {failures} times in a row mid-match: {exception.Message}", exception);
                    }
                    else
                    {
                        await Task.Delay(diff.Started ? poll : retry, clock, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }

                if (typesMissing)
                {
                    if (++typeless >= options.MaximumConsecutiveFailures)
                        throw new IOException($"ra2yrcpp at {Uri} returned no object type classes {typeless} times; refusing to stream objects without types");
                    await Task.Delay(poll, clock, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                foreach (Ra2yrcppEvent e in events)
                {
                    Interlocked.Increment(ref read);
                    sequence++;
                    yield return new RawObservation(
                        $"{captureId}-{sequence}",
                        captureId,
                        sequence,
                        e.EventType,
                        Ra2LabProfile.AdapterId,
                        Ra2LabProfile.AdapterVersion,
                        clock.GetUtcNow(),
                        e.Payload,
                        "sha256:" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(e.Payload))).ToLowerInvariant());
                }
                if (diff.Ended) yield break;
                await Task.Delay(poll, clock, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
