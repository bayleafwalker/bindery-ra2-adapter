// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Text.Json;
using Bindery.Ra2.Adapter.Channel;
using Bindery.Ra2.Adapter.Ra2yrcpp;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Ra2Yrproto;
using Ra2Yrproto.Commands;
using Ra2Yrproto.Ra2Yr;
using Xunit;

namespace Bindery.Ra2.Adapter.Tests;

/// <summary>A game behind the fake service: its current snapshot, and the orders it was given.</summary>
internal sealed class FakeGame
{
    public GameState State { get; set; } = Snapshots.State(1, Snapshots.Opening(), [Snapshots.Unit(0xA1, Snapshots.Americans, Snapshots.Amcv), Snapshots.Unit(0xB1, Snapshots.Soviets, Snapshots.Smcv)]);

    /// <summary>The fork's error for an order, as for an observer; null accepts it.</summary>
    public string? RejectOrders { get; set; }

    public ConcurrentQueue<IMessage> Orders { get; } = new();

    /// <summary>The type classes ReadValue reports.</summary>
    public ObjectTypeClass[] Types { get; set; } = Snapshots.Types;

    public CommandResult? Handle(Any command)
    {
        if (command.Is(GetGameState.Descriptor)) return FakeRa2yrcppServer.Ok(new GetGameState { State = State });
        if (command.Is(ReadValue.Descriptor))
        {
            GameState initial = new();
            initial.ObjectTypes.AddRange(Types);
            return FakeRa2yrcppServer.Ok(new ReadValue { Data = new StorageValue { InitialGameState = initial } });
        }
        IMessage order = command.Is(UnitOrder.Descriptor) ? command.Unpack<UnitOrder>()
            : command.Is(ProduceOrder.Descriptor) ? command.Unpack<ProduceOrder>()
            : command.Is(PlaceBuilding.Descriptor) ? command.Unpack<PlaceBuilding>()
            : throw new InvalidOperationException("unexpected command " + command.TypeUrl);
        Orders.Enqueue(order);
        return RejectOrders is { } reason ? FakeRa2yrcppServer.Error(order, reason) : FakeRa2yrcppServer.Ok(order);
    }
}

public sealed class Ra2yrcppCommandSinkTests
{
    private static PlayerCommand Command(string kind, string arguments) => new(kind, JsonDocument.Parse(arguments).RootElement.Clone());

    private static Ra2yrcppCommandSink Sink(FakeRa2yrcppServer server, string house = "Americans") =>
        new(house, new Ra2YrcppEndpoint(server.Uri.Host, server.Uri.Port));

    [Fact]
    public async Task ADeployGoesOutAsAUnitOrderForTheHousesOwnObjectsOnly()
    {
        FakeGame game = new();
        await using FakeRa2yrcppServer server = new(game.Handle);
        await using Ra2yrcppCommandSink sink = Sink(server);

        // 0xB1 is the enemy's MCV and 0xC1 is not in the snapshot at all.
        await sink.SendAsync(Command(Ra2yrcppCommandSink.Deploy, "{\"objects\":[161,177,193]}"), CancellationToken.None);

        UnitOrder order = Assert.IsType<UnitOrder>(Assert.Single(game.Orders));
        Assert.Equal(UnitAction.Deploy, order.Action);
        Assert.Equal([0xA1u], order.ObjectAddresses);
        // This snapshot, like an older fork build's, has no stable IDs to guard with.
        Assert.Empty(order.ObjectUniqueIds);
    }

    [Fact]
    public async Task OrdersCarryTheSnapshotsStableIdsAndDropObjectsWhoseIdChanged()
    {
        FakeGame game = new()
        {
            State = Snapshots.State(1, Snapshots.Opening(),
            [
                Snapshots.Unit(0xA1, Snapshots.Americans, Snapshots.Htnk, uniqueId: 7001),
                Snapshots.Unit(0xA2, Snapshots.Americans, Snapshots.Htnk, uniqueId: 7002),
                Snapshots.Unit(0xB1, Snapshots.Soviets, Snapshots.Htnk, uniqueId: 8001),
            ]),
        };
        await using FakeRa2yrcppServer server = new(game.Handle);
        await using Ra2yrcppCommandSink sink = Sink(server);

        // The controller saw 0xA2 as 6999: that unit is gone and the address now holds another.
        await sink.SendAsync(Command(Ra2yrcppCommandSink.Attack, "{\"objects\":[161,162],\"unique_ids\":[7001,6999],\"target\":177}"), CancellationToken.None);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => sink.SendAsync(Command(Ra2yrcppCommandSink.Stop, "{\"objects\":[162],\"unique_ids\":[6999]}"), CancellationToken.None));

        UnitOrder order = Assert.IsType<UnitOrder>(Assert.Single(game.Orders));
        Assert.Equal([0xA1u], order.ObjectAddresses);
        Assert.Equal([7001u], order.ObjectUniqueIds);
        Assert.Equal(0xB1u, order.TargetObject);
        Assert.Equal(8001u, order.TargetUniqueId);
    }

    [Fact]
    public async Task OrdersNameTheSeatClientsOwnObjectByItsStableId()
    {
        // The controller reads telemetry from another client, whose objects
        // live at other addresses: measured 2026-09-28, player-b's MCV was
        // 0x18fb8700 on client-a and 0x18d87758 on client-b, unique_id
        // 1070755 on both. Only the stable ID names the same object.
        FakeGame game = new()
        {
            State = Snapshots.State(1, Snapshots.Opening(),
            [
                Snapshots.Unit(0xB7, Snapshots.Americans, Snapshots.Amcv, uniqueId: 6),
                Snapshots.Unit(0xC9, Snapshots.Soviets, Snapshots.Htnk, uniqueId: 9),
            ]),
        };
        await using FakeRa2yrcppServer server = new(game.Handle);
        await using Ra2yrcppCommandSink sink = Sink(server);

        await sink.SendAsync(Command(Ra2yrcppCommandSink.Deploy, "{\"objects\":[161],\"unique_ids\":[6]}"), CancellationToken.None);
        await sink.SendAsync(Command(Ra2yrcppCommandSink.Attack, "{\"objects\":[161],\"unique_ids\":[6],\"target\":153,\"target_unique_id\":9}"), CancellationToken.None);

        UnitOrder[] orders = game.Orders.Cast<UnitOrder>().ToArray();
        Assert.Equal(2, orders.Length);
        Assert.Equal([0xB7u], orders[0].ObjectAddresses);
        Assert.Equal([6u], orders[0].ObjectUniqueIds);
        Assert.Equal([0xB7u], orders[1].ObjectAddresses);
        Assert.Equal(0xC9u, orders[1].TargetObject);
        Assert.Equal(9u, orders[1].TargetUniqueId);
    }

    [Fact]
    public async Task AStableIdThatNoLongerResolvesIsRefusedNotTriedAtTheForeignAddress()
    {
        // The controller's addresses are the capture client's. On this client
        // 0x99 holds an unrelated enemy (id 42), and the target it meant (id 9)
        // is gone; this client's snapshot of 0xA1 predates stable IDs for it.
        FakeGame game = new()
        {
            State = Snapshots.State(1, Snapshots.Opening(),
            [
                Snapshots.Unit(0xB7, Snapshots.Americans, Snapshots.Htnk, uniqueId: 6),
                Snapshots.Unit(0xA1, Snapshots.Americans, Snapshots.Htnk),
                Snapshots.Unit(0x99, Snapshots.Soviets, Snapshots.Smcv, uniqueId: 42),
            ]),
        };
        await using FakeRa2yrcppServer server = new(game.Handle);
        await using Ra2yrcppCommandSink sink = Sink(server);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => sink.SendAsync(Command(Ra2yrcppCommandSink.Attack, "{\"objects\":[183],\"unique_ids\":[6],\"target\":153,\"target_unique_id\":9}"), CancellationToken.None));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => sink.SendAsync(Command(Ra2yrcppCommandSink.Deploy, "{\"objects\":[161],\"unique_ids\":[5]}"), CancellationToken.None));

        Assert.Empty(game.Orders);
    }

    [Fact]
    public async Task ARulesIdWinsOverAnotherTypesDisplayNameInProduction()
    {
        FakeGame game = new()
        {
            Types =
            [
                new ObjectTypeClass { Name = "HTNK", Id = "XTNK", PointerSelf = 0x2001, Type = AbstractType.Unittype },
                new ObjectTypeClass { Name = "Rhino Heavy Tank", Id = "HTNK", PointerSelf = Snapshots.Htnk, Type = AbstractType.Unittype },
            ],
        };
        await using FakeRa2yrcppServer server = new(game.Handle);
        await using Ra2yrcppCommandSink sink = Sink(server);

        await sink.SendAsync(Command(Ra2yrcppCommandSink.Produce, "{\"type\":\"HTNK\"}"), CancellationToken.None);

        Assert.Equal(Snapshots.Htnk, Assert.IsType<ProduceOrder>(Assert.Single(game.Orders)).ObjectType.PointerSelf);
    }

    [Fact]
    public async Task ProductionFindsATypeByItsRulesIdWhenItsNameIsDisplayText()
    {
        FakeGame game = new() { Types = Snapshots.ForkTypes };
        await using FakeRa2yrcppServer server = new(game.Handle);
        await using Ra2yrcppCommandSink sink = Sink(server);

        await sink.SendAsync(Command(Ra2yrcppCommandSink.Produce, "{\"type\":\"GAPOWR\"}"), CancellationToken.None);

        ProduceOrder produce = Assert.IsType<ProduceOrder>(Assert.Single(game.Orders));
        Assert.Equal(Snapshots.Gapowr, produce.ObjectType.PointerSelf);
    }

    [Fact]
    public async Task AnOrderWithNoOwnObjectLeftIsRefusedWithoutReachingTheGame()
    {
        FakeGame game = new();
        await using FakeRa2yrcppServer server = new(game.Handle);
        await using Ra2yrcppCommandSink sink = Sink(server);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => sink.SendAsync(Command(Ra2yrcppCommandSink.Move, "{\"objects\":[177],\"x\":1,\"y\":2}"), CancellationToken.None));

        Assert.Empty(game.Orders);
    }

    [Fact]
    public async Task AClientPlayingAnotherHouseIsRefusedForGood()
    {
        FakeGame game = new();
        game.State.Houses[2].CurrentPlayer = false;
        game.State.Houses[3].CurrentPlayer = true;
        await using FakeRa2yrcppServer server = new(game.Handle);
        await using Ra2yrcppCommandSink sink = Sink(server);

        Ra2yrcppSeatException refused = await Assert.ThrowsAsync<Ra2yrcppSeatException>(() => sink.OpenAsync());
        Assert.Contains("Soviets", refused.Message, StringComparison.Ordinal);

        // Even if the snapshot later looked right, the seat stays closed.
        game.State.Houses[3].CurrentPlayer = false;
        game.State.Houses[2].CurrentPlayer = true;
        await Assert.ThrowsAsync<Ra2yrcppSeatException>(() => sink.SendAsync(Command(Ra2yrcppCommandSink.Deploy, "{\"objects\":[161]}"), CancellationToken.None));
        Assert.Empty(game.Orders);
    }

    [Fact]
    public async Task ASeatWaitsForTheMatchWhenNoHouseIsLocalYet()
    {
        FakeGame game = new() { State = Snapshots.State(0, [], [], LoadStage.StageLoading) };
        await using FakeRa2yrcppServer server = new(game.Handle);
        await using Ra2yrcppCommandSink sink = Sink(server);

        InvalidOperationException early = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => sink.OpenAsync());
        Assert.IsNotType<Ra2yrcppSeatException>(early);

        game.State = new FakeGame().State;
        await sink.SendAsync(Command(Ra2yrcppCommandSink.Deploy, "{\"objects\":[161]}"), CancellationToken.None);
        Assert.Single(game.Orders);
    }

    [Fact]
    public async Task AnObserverClientsRejectionIsAFailedCommandInTheSeatsTrace()
    {
        FakeGame game = new() { RejectOrders = "order rejected: local player is an observer" };
        await using FakeRa2yrcppServer server = new(game.Handle);
        await using Ra2yrcppCommandSink sink = Sink(server);

        Ra2yrcppCommandException error = await Assert.ThrowsAsync<Ra2yrcppCommandException>(() => sink.SendAsync(Command(Ra2yrcppCommandSink.Deploy, "{\"objects\":[161]}"), CancellationToken.None));
        Assert.Equal("order rejected: local player is an observer", error.ErrorMessage);

        string directory = Path.Combine(Path.GetTempPath(), "bindery-sink-" + Guid.NewGuid().ToString("N"));
        string recording = Path.Combine(directory, "telemetry.ndjson");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(recording, NdjsonTelemetryFormat.Serialize(new RawObservation("e1", "c1", 1, Ra2TelemetryEventTypes.MatchStarted, Ra2LabProfile.AdapterId, Ra2LabProfile.AdapterVersion, DateTimeOffset.UnixEpoch, JsonDocument.Parse("{}").RootElement.Clone(), "sha256:raw")) + "\n");
        AgentSeat seat = new(new OneCommandController(Command(Ra2yrcppCommandSink.Deploy, "{\"objects\":[161]}")), sink, new PlayerObservationFilter("Americans"));

        AgentSeatSummary summary = await seat.RunAsync(new NdjsonTelemetrySource(recording), directory);

        Assert.Equal(1, summary.CommandsFailed);
        Assert.Contains("local player is an observer", await File.ReadAllTextAsync(summary.TracePath), StringComparison.Ordinal);
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task ProductionNamesATypeAndPlacementNeedsAFinishedBuildingInTheHousesFactory()
    {
        FakeGame game = new();
        game.State.Objects.Add(Snapshots.Unit(0xA5, Snapshots.Americans, Snapshots.Gapowr, AbstractType.Building, limbo: true));
        game.State.Factories.Add(new Factory { Owner = Snapshots.Americans, Object = 0xA5, Completed = true });
        game.State.Factories.Add(new Factory { Owner = Snapshots.Soviets, Object = 0xB5, Completed = true });
        await using FakeRa2yrcppServer server = new(game.Handle);
        await using Ra2yrcppCommandSink sink = Sink(server);

        await sink.SendAsync(Command(Ra2yrcppCommandSink.Produce, "{\"type\":\"GAPOWR\"}"), CancellationToken.None);
        await sink.SendAsync(Command(Ra2yrcppCommandSink.PlaceBuilding, "{\"object\":165,\"x\":1024,\"y\":2048}"), CancellationToken.None);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => sink.SendAsync(Command(Ra2yrcppCommandSink.PlaceBuilding, "{\"object\":181,\"x\":1,\"y\":1}"), CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => sink.SendAsync(Command(Ra2yrcppCommandSink.Produce, "{\"type\":\"NOSUCH\"}"), CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => sink.SendAsync(Command("self_destruct", "{}"), CancellationToken.None));

        IMessage[] orders = game.Orders.ToArray();
        Assert.Equal(2, orders.Length);
        ProduceOrder produce = Assert.IsType<ProduceOrder>(orders[0]);
        Assert.Equal(Snapshots.Gapowr, produce.ObjectType.PointerSelf);
        Assert.Equal(ProduceAction.Begin, produce.Action);
        PlaceBuilding place = Assert.IsType<PlaceBuilding>(orders[1]);
        Assert.Equal(0xA5u, place.Building.PointerSelf);
        Assert.Equal(1024, place.Coordinates.X);
        Assert.Equal(2048, place.Coordinates.Y);
    }

    [Fact]
    public async Task AServiceThatAcceptsButNeverAnswersTimesTheConnectOut()
    {
        // TCP accepts, the WebSocket upgrade never comes back.
        using System.Net.Sockets.TcpListener silent = new(System.Net.IPAddress.Loopback, 0);
        silent.Start();
        int port = ((System.Net.IPEndPoint)silent.LocalEndpoint).Port;
        await using Ra2yrcppCommandSink sink = new("Americans", new Ra2YrcppEndpoint("127.0.0.1", port), new Ra2yrcppClientOptions(ConnectTimeout: TimeSpan.FromMilliseconds(300)));
        using CancellationTokenSource backstop = new(TimeSpan.FromSeconds(10));
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => sink.SendAsync(Command(Ra2yrcppCommandSink.Deploy, "{\"objects\":[161]}"), backstop.Token));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AnOrderWhoseResultNeverComesBackHasAnUnknownOutcomeNotAFailure()
    {
        FakeGame game = new();
        await using FakeRa2yrcppServer server = new(command => command.Is(UnitOrder.Descriptor) ? QueueOnly(game, command) : game.Handle(command));
        await using Ra2yrcppCommandSink sink = new("Americans", new Ra2YrcppEndpoint(server.Uri.Host, server.Uri.Port), new Ra2yrcppClientOptions(CommandTimeout: TimeSpan.FromMilliseconds(300), PollTimeout: TimeSpan.FromMilliseconds(50)));
        string directory = Path.Combine(Path.GetTempPath(), "bindery-sink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string recording = Path.Combine(directory, "telemetry.ndjson");
            await File.WriteAllTextAsync(recording, NdjsonTelemetryFormat.Serialize(new RawObservation("e1", "c1", 1, Ra2TelemetryEventTypes.MatchStarted, Ra2LabProfile.AdapterId, Ra2LabProfile.AdapterVersion, DateTimeOffset.UnixEpoch, JsonDocument.Parse("{}").RootElement.Clone(), "sha256:raw")) + "\n");
            AgentSeat seat = new(new OneCommandController(Command(Ra2yrcppCommandSink.Deploy, "{\"objects\":[161]}")), sink, new PlayerObservationFilter("Americans"));

            AgentSeatSummary summary = await seat.RunAsync(new NdjsonTelemetrySource(recording), directory);

            // The fork queued it; it may still run on the next frame.
            Assert.Single(game.Orders);
            Assert.Equal(0, summary.CommandsFailed);
            Assert.Equal(0, summary.CommandsSent);
            Assert.Equal(1, summary.CommandsOutcomeUnknown);
            Assert.Contains("\"kind\":\"command_outcome_unknown\"", await File.ReadAllTextAsync(summary.TracePath), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Ra2Yrproto.CommandResult? QueueOnly(FakeGame game, Google.Protobuf.WellKnownTypes.Any command)
    {
        game.Orders.Enqueue(command.Unpack<UnitOrder>());
        return null;
    }

    [Fact]
    public async Task DisposingDuringAnOrderWaitsForItAndThenClosesTheSeat()
    {
        FakeGame game = new();
        await using FakeRa2yrcppServer server = new(command =>
        {
            CommandResult? result = game.Handle(command);
            // The game takes a while to run the order.
            if (command.Is(UnitOrder.Descriptor)) Thread.Sleep(300);
            return result;
        });
        Ra2yrcppCommandSink sink = Sink(server);

        Task sending = sink.SendAsync(Command(Ra2yrcppCommandSink.Deploy, "{\"objects\":[161]}"), CancellationToken.None);
        while (game.Orders.IsEmpty) await Task.Delay(10);
        await sink.DisposeAsync();

        Exception? failure = await Record.ExceptionAsync(() => sending);
        Assert.Null(failure);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => sink.SendAsync(Command(Ra2yrcppCommandSink.Deploy, "{\"objects\":[161]}"), CancellationToken.None));
    }

    private sealed class OneCommandController(PlayerCommand command) : IPlayerController
    {
        public string ControllerId => "test/one-command";

        public string ControllerVersion => "1";

        public Task<ControllerStep> ObserveAsync(RawObservation observation, CancellationToken cancellationToken) =>
            Task.FromResult(new ControllerStep([command]));
    }
}
