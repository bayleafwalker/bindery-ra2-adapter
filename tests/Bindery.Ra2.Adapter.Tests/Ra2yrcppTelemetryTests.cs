// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Adapter.Channel;
using Bindery.Ra2.Adapter.Ra2yrcpp;
using Ra2Yrproto.Commands;
using Ra2Yrproto.Ra2Yr;
using Xunit;

namespace Bindery.Ra2.Adapter.Tests;

/// <summary>Snapshots as the fork sends them, for the telemetry and command tests.</summary>
internal static class Snapshots
{
    public const uint Americans = 0x100, Soviets = 0x200, Special = 0x10, Neutral = 0x20;
    public const uint Amcv = 0x1000, Smcv = 0x1001, Gapowr = 0x1100, Napowr = 0x1101, Htnk = 0x1002;

    public static ObjectTypeClass[] Types { get; } =
    [
        new() { Name = "AMCV", PointerSelf = Amcv, Type = AbstractType.Unittype },
        new() { Name = "SMCV", PointerSelf = Smcv, Type = AbstractType.Unittype },
        new() { Name = "HTNK", PointerSelf = Htnk, Type = AbstractType.Unittype },
        new() { Name = "GAPOWR", PointerSelf = Gapowr, Type = AbstractType.Buildingtype },
        new() { Name = "NAPOWR", PointerSelf = Napowr, Type = AbstractType.Buildingtype },
    ];

    public static House House(string name, uint self, int money = 10000, bool current = false, bool defeated = false, bool winner = false, bool gameOver = false) =>
        new() { Name = name, Self = self, Money = money, CurrentPlayer = current, Defeated = defeated, IsWinner = winner, IsGameOver = gameOver, PowerOutput = 0, PowerDrain = 0 };

    public static Ra2Yrproto.Ra2Yr.Object Unit(uint address, uint owner, uint type, AbstractType kind = AbstractType.Unit, bool limbo = false, uint uniqueId = 0) =>
        new() { PointerSelf = address, PointerHouse = owner, PointerTechnotypeclass = type, ObjectType = kind, InLimbo = limbo, UniqueId = uniqueId, Coordinates = new Coordinates { X = 100, Y = 200, Z = 0 } };

    public static GameState State(uint frame, IEnumerable<House> houses, IEnumerable<Ra2Yrproto.Ra2Yr.Object> objects, LoadStage stage = LoadStage.StageIngame)
    {
        GameState state = new() { CurrentFrame = frame, Stage = stage };
        state.Houses.AddRange(houses);
        state.Objects.AddRange(objects);
        return state;
    }

    public static House[] Opening(int americanMoney = 10000, int sovietMoney = 10000) =>
    [
        House("Special", Special),
        House("Neutral", Neutral),
        House("Americans", Americans, americanMoney, current: true),
        House("Soviets", Soviets, sovietMoney),
    ];
}

public sealed class Ra2yrcppTelemetryTests
{
    private static readonly DateTimeOffset start = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static Ra2yrcppSnapshotDiff Diff(string? seat = null)
    {
        Ra2yrcppSnapshotDiff diff = new(seat, creditsHeartbeat: TimeSpan.FromSeconds(10));
        diff.SetTypes(Snapshots.Types);
        return diff;
    }

    private static string? Text(Ra2yrcppEvent e, string field) =>
        e.Payload.TryGetProperty(field, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [Fact]
    public void TheFirstInGameSnapshotStartsTheMatchWithItsPlayersAndStartingUnits()
    {
        Ra2yrcppSnapshotDiff diff = Diff();
        Assert.Empty(diff.Next(Snapshots.State(0, [], [], LoadStage.StageLoading), start));

        IReadOnlyList<Ra2yrcppEvent> events = diff.Next(Snapshots.State(1, Snapshots.Opening(), [Snapshots.Unit(0xA1, Snapshots.Americans, Snapshots.Amcv), Snapshots.Unit(0xB1, Snapshots.Soviets, Snapshots.Smcv)]), start);

        Assert.NotEmpty(events);
        Assert.Equal(Ra2TelemetryEventTypes.MatchStarted, events[0].EventType);
        // Special and Neutral are the engine's houses, not players.
        Assert.Equal(["Americans", "Soviets"], events.Where(static e => e.EventType == Ra2TelemetryEventTypes.PlayerJoined).Select(e => Text(e, "house")));
        Ra2yrcppEvent mcv = Assert.Single(events, e => e.EventType == Ra2TelemetryEventTypes.UnitCreated && Text(e, "house") == "Americans");
        Assert.Equal("AMCV", Text(mcv, "type"));
        Assert.Equal(0xA1u, mcv.Payload.GetProperty("object").GetUInt32());
        // Each house sees its own objects; nothing claims more than that.
        Assert.Equal(["Americans"], mcv.Payload.GetProperty("visible_to").EnumerateArray().Select(static v => v.GetString()));
        Assert.True(diff.Started);
    }

    [Fact]
    public void LaterSnapshotsBecomeBuildDestroyEconomyDefeatAndEndEvents()
    {
        Ra2yrcppSnapshotDiff diff = Diff();
        diff.Next(Snapshots.State(1, Snapshots.Opening(), [Snapshots.Unit(0xA1, Snapshots.Americans, Snapshots.Amcv), Snapshots.Unit(0xB1, Snapshots.Soviets, Snapshots.Smcv)]), start);

        // The MCV deployed: it is gone and a construction yard... here a power plant... stands; money was spent.
        IReadOnlyList<Ra2yrcppEvent> built = diff.Next(Snapshots.State(2,
            Snapshots.Opening(americanMoney: 9200),
            [Snapshots.Unit(0xA2, Snapshots.Americans, Snapshots.Gapowr, AbstractType.Building), Snapshots.Unit(0xB1, Snapshots.Soviets, Snapshots.Smcv), Snapshots.Unit(0xB2, Snapshots.Soviets, Snapshots.Napowr, AbstractType.Building, limbo: true)]), start.AddSeconds(1));
        Assert.Contains(built, e => e.EventType == Ra2TelemetryEventTypes.UnitDestroyed && Text(e, "type") == "AMCV");
        Assert.Contains(built, e => e.EventType == Ra2TelemetryEventTypes.BuildingPlaced && Text(e, "type") == "GAPOWR" && Text(e, "house") == "Americans");
        // A finished building waiting in its factory is not on the map yet.
        Assert.DoesNotContain(built, e => Text(e, "type") == "NAPOWR");
        Ra2yrcppEvent credits = Assert.Single(built, static e => e.EventType == Ra2TelemetryEventTypes.CreditsSampled);
        Assert.Equal(9200, credits.Payload.GetProperty("credits").GetInt64());

        // Unchanged credits are still sampled at the heartbeat, so a flat economy is visible.
        Assert.DoesNotContain(diff.Next(Snapshots.State(3, Snapshots.Opening(americanMoney: 9200), [Snapshots.Unit(0xA2, Snapshots.Americans, Snapshots.Gapowr, AbstractType.Building)]), start.AddSeconds(5)), static e => e.EventType == Ra2TelemetryEventTypes.CreditsSampled && e.Payload.GetProperty("house").GetString() == "Americans");
        Assert.Contains(diff.Next(Snapshots.State(4, Snapshots.Opening(americanMoney: 9200), [Snapshots.Unit(0xA2, Snapshots.Americans, Snapshots.Gapowr, AbstractType.Building)]), start.AddSeconds(12)), static e => e.EventType == Ra2TelemetryEventTypes.CreditsSampled && e.Payload.GetProperty("house").GetString() == "Americans");

        House[] over =
        [
            Snapshots.House("Special", Snapshots.Special),
            Snapshots.House("Neutral", Snapshots.Neutral),
            Snapshots.House("Americans", Snapshots.Americans, 9200, current: true, winner: true, gameOver: true),
            Snapshots.House("Soviets", Snapshots.Soviets, 0, defeated: true),
        ];
        IReadOnlyList<Ra2yrcppEvent> end = diff.Next(Snapshots.State(5, over, [Snapshots.Unit(0xA2, Snapshots.Americans, Snapshots.Gapowr, AbstractType.Building)]), start.AddSeconds(13));
        Assert.Contains(end, static e => e.EventType == Ra2TelemetryEventTypes.PlayerDefeated && e.Payload.GetProperty("house").GetString() == "Soviets");
        Ra2yrcppEvent ended = end[^1];
        Assert.Equal(Ra2TelemetryEventTypes.MatchEnded, ended.EventType);
        Assert.Equal("Americans", Text(ended, "winner"));
        Assert.True(diff.Ended);
        Assert.Empty(diff.Next(Snapshots.State(6, over, []), start.AddSeconds(14)));
    }

    [Fact]
    public void StableIdsTravelWithObjectEventsAndARecycledAddressIsANewObject()
    {
        Ra2yrcppSnapshotDiff diff = Diff();
        IReadOnlyList<Ra2yrcppEvent> first = diff.Next(Snapshots.State(1, Snapshots.Opening(), [Snapshots.Unit(0xA1, Snapshots.Americans, Snapshots.Amcv, uniqueId: 7001), Snapshots.Unit(0xA3, Snapshots.Americans, Snapshots.Htnk)]), start);
        Assert.Equal((uint?)7001u, Assert.Single(first, e => e.EventType == Ra2TelemetryEventTypes.UnitCreated && Text(e, "type") == "AMCV").Payload.TryGetProperty("unique_id", out JsonElement id0) ? id0.GetUInt32() : (uint?)null);
        // An older fork build sends no ID, and none is invented.
        Assert.False(Assert.Single(first, e => e.EventType == Ra2TelemetryEventTypes.UnitCreated && Text(e, "type") == "HTNK").Payload.TryGetProperty("unique_id", out _));

        // Same address, same owner and type, new ID: the old MCV died and the address was reused.
        IReadOnlyList<Ra2yrcppEvent> next = diff.Next(Snapshots.State(2, Snapshots.Opening(), [Snapshots.Unit(0xA1, Snapshots.Americans, Snapshots.Amcv, uniqueId: 7050), Snapshots.Unit(0xA3, Snapshots.Americans, Snapshots.Htnk)]), start.AddSeconds(1));
        Assert.Equal((uint?)7001u, Assert.Single(next, static e => e.EventType == Ra2TelemetryEventTypes.UnitDestroyed).Payload.TryGetProperty("unique_id", out JsonElement id1) ? id1.GetUInt32() : (uint?)null);
        Assert.Equal((uint?)7050u, Assert.Single(next, static e => e.EventType == Ra2TelemetryEventTypes.UnitCreated).Payload.TryGetProperty("unique_id", out JsonElement id2) ? id2.GetUInt32() : (uint?)null);
    }

    [Fact]
    public void ALosingLocalPlayerWaitsBrieflyForTheWinnerBeforeTheMatchEnds()
    {
        Ra2yrcppSnapshotDiff diff = new(creditsHeartbeat: TimeSpan.FromSeconds(10), winnerGrace: TimeSpan.FromSeconds(5));
        diff.SetTypes(Snapshots.Types);
        diff.Next(Snapshots.State(1, Snapshots.Opening(), []), start);

        // This client's house lost; the game has not flagged the winner yet.
        House[] lost = [Snapshots.House("Americans", Snapshots.Americans, current: true, defeated: true, gameOver: true), Snapshots.House("Soviets", Snapshots.Soviets)];
        Assert.DoesNotContain(diff.Next(Snapshots.State(2, lost, []), start.AddSeconds(10)), static e => e.EventType == Ra2TelemetryEventTypes.MatchEnded);
        Assert.True(diff.EndPending);
        House[] decided = [lost[0], Snapshots.House("Soviets", Snapshots.Soviets, winner: true)];
        Ra2yrcppEvent ended = Assert.Single(diff.Next(Snapshots.State(3, decided, []), start.AddSeconds(12)), static e => e.EventType == Ra2TelemetryEventTypes.MatchEnded);
        Assert.Equal("Soviets", Text(ended, "winner"));
    }

    [Fact]
    public void WithoutAWinnerTheMatchEndsWhenTheGraceRunsOutOrTheStreamStops()
    {
        House[] lost = [Snapshots.House("Americans", Snapshots.Americans, current: true, defeated: true, gameOver: true), Snapshots.House("Soviets", Snapshots.Soviets)];
        Ra2yrcppSnapshotDiff timed = new(winnerGrace: TimeSpan.FromSeconds(5));
        timed.Next(Snapshots.State(1, Snapshots.Opening(), []), start);
        Assert.DoesNotContain(timed.Next(Snapshots.State(2, lost, []), start.AddSeconds(10)), static e => e.EventType == Ra2TelemetryEventTypes.MatchEnded);
        Ra2yrcppEvent ended = Assert.Single(timed.Next(Snapshots.State(3, lost, []), start.AddSeconds(16)), static e => e.EventType == Ra2TelemetryEventTypes.MatchEnded);
        Assert.Null(Text(ended, "winner"));

        // The client closed during the grace: end with what is known.
        Ra2yrcppSnapshotDiff closed = new(winnerGrace: TimeSpan.FromSeconds(5));
        closed.Next(Snapshots.State(1, Snapshots.Opening(), []), start);
        closed.Next(Snapshots.State(2, lost, []), start.AddSeconds(10));
        Assert.Equal(Ra2TelemetryEventTypes.MatchEnded, Assert.Single(closed.Finish()).EventType);
        Assert.True(closed.Ended);
    }

    [Fact]
    public void ASeatScopedDiffLeavesOtherHousesOutAndEverythingItEmitsPassesTheSeatFilter()
    {
        Ra2yrcppSnapshotDiff diff = Diff("Americans");
        List<Ra2yrcppEvent> events =
        [
            .. diff.Next(Snapshots.State(1, Snapshots.Opening(), [Snapshots.Unit(0xA1, Snapshots.Americans, Snapshots.Amcv), Snapshots.Unit(0xB1, Snapshots.Soviets, Snapshots.Smcv)]), start),
            .. diff.Next(Snapshots.State(2, Snapshots.Opening(americanMoney: 9000, sovietMoney: 8000), [Snapshots.Unit(0xA1, Snapshots.Americans, Snapshots.Amcv), Snapshots.Unit(0xB3, Snapshots.Soviets, Snapshots.Htnk)]), start.AddSeconds(1)),
        ];

        Assert.DoesNotContain(events, e => e.EventType is not (Ra2TelemetryEventTypes.PlayerJoined or Ra2TelemetryEventTypes.PlayerDefeated) && Text(e, "house") == "Soviets");
        Assert.Contains(events, e => e.EventType == Ra2TelemetryEventTypes.PlayerJoined && Text(e, "house") == "Soviets");
        Assert.Contains(events, e => e.EventType == Ra2TelemetryEventTypes.CreditsSampled && Text(e, "house") == "Americans");
        PlayerObservationFilter filter = new("Americans");
        Assert.All(events, e => Assert.True(filter.Admits(Observation(e))));
    }

    [Fact]
    public void ASpectatorDiffKeepsEveryHouseButTheSeatFilterStillWithholdsTheEnemy()
    {
        Ra2yrcppSnapshotDiff diff = Diff();
        IReadOnlyList<Ra2yrcppEvent> events = diff.Next(Snapshots.State(1, Snapshots.Opening(), [Snapshots.Unit(0xA1, Snapshots.Americans, Snapshots.Amcv), Snapshots.Unit(0xB1, Snapshots.Soviets, Snapshots.Smcv)]), start);

        PlayerObservationFilter filter = new("Americans");
        Ra2yrcppEvent enemy = Assert.Single(events, e => e.EventType == Ra2TelemetryEventTypes.UnitCreated && Text(e, "house") == "Soviets");
        Assert.False(filter.Admits(Observation(enemy)));
        Assert.True(filter.Admits(Observation(Assert.Single(events, e => e.EventType == Ra2TelemetryEventTypes.UnitCreated && Text(e, "house") == "Americans"))));
    }

    [Fact]
    public async Task TheSourcePollsGameStateUntilTheMatchEndsWithTypeNamesFromTheInitialState()
    {
        Queue<GameState> frames = new(
        [
            Snapshots.State(0, [], [], LoadStage.StageLoading),
            Snapshots.State(1, Snapshots.Opening(), [Snapshots.Unit(0xA1, Snapshots.Americans, Snapshots.Amcv)]),
            Snapshots.State(2,
            [
                Snapshots.House("Special", Snapshots.Special),
                Snapshots.House("Neutral", Snapshots.Neutral),
                Snapshots.House("Americans", Snapshots.Americans, current: true, defeated: true, gameOver: true),
                Snapshots.House("Soviets", Snapshots.Soviets, winner: true),
            ], []),
        ]);
        GameState last = frames.Last();
        await using FakeRa2yrcppServer server = new(command =>
        {
            if (command.Is(GetGameState.Descriptor)) return FakeRa2yrcppServer.Ok(new GetGameState { State = frames.Count > 0 ? frames.Dequeue() : last });
            if (command.Is(ReadValue.Descriptor))
            {
                GameState initial = new();
                initial.ObjectTypes.AddRange(Snapshots.Types);
                return FakeRa2yrcppServer.Ok(new ReadValue { Data = new StorageValue { InitialGameState = initial } });
            }
            return FakeRa2yrcppServer.Error(command, "unexpected");
        });
        Ra2yrcppTelemetrySource source = new(new Ra2YrcppEndpoint(server.Uri.Host, server.Uri.Port), new Ra2yrcppTelemetryOptions(PollInterval: TimeSpan.FromMilliseconds(10)));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));

        List<RawObservation> observed = [];
        await foreach (RawObservation observation in source.ReadAsync(timeout.Token)) observed.Add(observation);

        Assert.NotEmpty(observed);
        Assert.Equal(Ra2TelemetryEventTypes.MatchStarted, observed[0].EventType);
        Assert.Contains(observed, static o => o.EventType == Ra2TelemetryEventTypes.UnitCreated && o.Payload.GetProperty("type").GetString() == "AMCV");
        Assert.Equal(Ra2TelemetryEventTypes.MatchEnded, observed[^1].EventType);
        MatchTelemetryTracker tracker = new();
        foreach (RawObservation observation in observed) tracker.Observe(observation);
        Assert.Equal("Soviets", tracker.Winner);
        Assert.Equal(observed.Select(static (_, i) => (ulong)(i + 1)), observed.Select(static o => o.Sequence));
        Assert.All(observed, static o => Assert.StartsWith("sha256:", o.RawObjectHash, StringComparison.Ordinal));
        Assert.Equal(observed.Count, source.Capture.RawEventCount);
        Assert.True(source.Capture.RawEventsObserved);
    }

    [Fact]
    public async Task OneCorruptFrameMidMatchIsRetriedNotTheEndOfTheStream()
    {
        Queue<GameState> frames = new(
        [
            Snapshots.State(1, Snapshots.Opening(), [Snapshots.Unit(0xA1, Snapshots.Americans, Snapshots.Amcv)]),
            Snapshots.State(2, Snapshots.Opening(), [Snapshots.Unit(0xA1, Snapshots.Americans, Snapshots.Amcv)]),
            Snapshots.State(3,
            [
                Snapshots.House("Americans", Snapshots.Americans, current: true, winner: true),
                Snapshots.House("Soviets", Snapshots.Soviets, defeated: true),
            ], []),
        ]);
        GameState last = frames.Last();
        int polls = 0;
        await using FakeRa2yrcppServer server = new(command =>
        {
            if (command.Is(GetGameState.Descriptor)) return FakeRa2yrcppServer.Ok(new GetGameState { State = frames.Count > 0 ? frames.Dequeue() : last });
            GameState initial = new();
            initial.ObjectTypes.AddRange(Snapshots.Types);
            return FakeRa2yrcppServer.Ok(new ReadValue { Data = new StorageValue { InitialGameState = initial } });
        })
        {
            // The fourth poll's reply is a truncated protobuf message.
            Corrupt = command => command.CommandType == Ra2Yrproto.CommandType.PollBlocking && Interlocked.Increment(ref polls) == 4 ? [0x0A, 0xFF] : null,
        };
        Ra2yrcppTelemetrySource source = new(new Ra2YrcppEndpoint(server.Uri.Host, server.Uri.Port), new Ra2yrcppTelemetryOptions(PollInterval: TimeSpan.FromMilliseconds(10)));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));

        List<RawObservation> observed = [];
        Exception? failure = null;
        try
        {
            await foreach (RawObservation observation in source.ReadAsync(timeout.Token)) observed.Add(observation);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Assert.Null(failure);
        Assert.True(Volatile.Read(ref polls) >= 4);
        Assert.Equal(Ra2TelemetryEventTypes.MatchEnded, Assert.IsType<RawObservation>(observed.LastOrDefault()).EventType);
    }

    [Fact]
    public void AnIpv6EndpointIsRefusedBecauseTheForkListensOnIpv4Only()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => Ra2yrcppClient.UriFor(Ra2YrcppEndpoint.Parse("[::1]:14521")));
        Assert.Contains("IPv4", error.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(new Ra2yrcppLiveTelemetrySettings { Endpoint = "[fe80::1]:14521" }.Validate);
    }

    [Fact]
    public async Task EventsWaitForTypeNamesAndTheStreamFailsIfTheyNeverCome()
    {
        GameState running = Snapshots.State(1, Snapshots.Opening(), [Snapshots.Unit(0xA1, Snapshots.Americans, Snapshots.Amcv)]);
        GameState won = Snapshots.State(2, [Snapshots.House("Americans", Snapshots.Americans, current: true, winner: true)], []);
        int reads = 0, typeReads = 0;
        int typesAfter = 2;
        FakeRa2yrcppServer Serve() => new(command =>
        {
            if (command.Is(GetGameState.Descriptor)) return FakeRa2yrcppServer.Ok(new GetGameState { State = Interlocked.Increment(ref reads) < 6 ? running : won });
            GameState initial = new();
            if (Interlocked.Increment(ref typeReads) > typesAfter) initial.ObjectTypes.AddRange(Snapshots.Types);
            return FakeRa2yrcppServer.Ok(new ReadValue { Data = new StorageValue { InitialGameState = initial } });
        });

        // Types arrive on the third read: the MCV is reported with its type, never without.
        await using (FakeRa2yrcppServer server = Serve())
        {
            Ra2yrcppTelemetrySource source = new(new Ra2YrcppEndpoint(server.Uri.Host, server.Uri.Port), new Ra2yrcppTelemetryOptions(PollInterval: TimeSpan.FromMilliseconds(10)));
            List<RawObservation> observed = [];
            await foreach (RawObservation observation in source.ReadAsync(new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token)) observed.Add(observation);
            RawObservation mcv = Assert.Single(observed, static o => o.EventType == Ra2TelemetryEventTypes.UnitCreated);
            Assert.True(mcv.Payload.TryGetProperty("type", out JsonElement type));
            Assert.Equal("AMCV", type.GetString());
        }

        // Types never arrive: fail loudly rather than stream typeless objects.
        reads = 0;
        typeReads = 0;
        typesAfter = int.MaxValue;
        await using (FakeRa2yrcppServer server = Serve())
        {
            Ra2yrcppTelemetrySource source = new(new Ra2YrcppEndpoint(server.Uri.Host, server.Uri.Port), new Ra2yrcppTelemetryOptions(PollInterval: TimeSpan.FromMilliseconds(10), MaximumConsecutiveFailures: 3));
            List<RawObservation> observed = [];
            Exception? failure = await Record.ExceptionAsync(async () =>
            {
                await foreach (RawObservation observation in source.ReadAsync(new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token)) observed.Add(observation);
            });
            Assert.IsType<IOException>(failure);
            Assert.Contains("type", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(observed, static o => o.EventType == Ra2TelemetryEventTypes.UnitCreated);
        }
    }

    [Fact]
    public async Task TheSourceWaitsForTheServiceToComeUp()
    {
        // Nothing listens yet: the game has not loaded the DLL.
        int port;
        using (System.Net.Sockets.TcpListener probe = new(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        }
        Ra2yrcppTelemetrySource source = new(new Ra2YrcppEndpoint("localhost", port), new Ra2yrcppTelemetryOptions(PollInterval: TimeSpan.FromMilliseconds(10), ConnectRetry: TimeSpan.FromMilliseconds(20)));
        using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(400));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (RawObservation _ in source.ReadAsync(timeout.Token)) { }
        });
        Assert.False(source.Capture.RawEventsObserved);
    }

    private static RawObservation Observation(Ra2yrcppEvent e) =>
        new("e-1", "capture-1", 1, e.EventType, Ra2LabProfile.AdapterId, Ra2LabProfile.AdapterVersion, start, e.Payload, "sha256:raw");
}
