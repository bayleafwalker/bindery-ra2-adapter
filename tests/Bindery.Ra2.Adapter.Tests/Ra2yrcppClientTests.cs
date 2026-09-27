// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.WebSockets;
using Bindery.Ra2.Adapter.Ra2yrcpp;
using Google.Protobuf.WellKnownTypes;
using Ra2Yrproto;
using Ra2Yrproto.Commands;
using Ra2Yrproto.Ra2Yr;
using Xunit;

namespace Bindery.Ra2.Adapter.Tests;

public sealed class Ra2yrcppClientTests
{
    private static GetGameState Snapshot(uint frame) => new() { State = new GameState { CurrentFrame = frame } };

    [Fact]
    public async Task ACommandIsQueuedThenPolledFromTheConnectionsOwnQueueInBinaryFrames()
    {
        await using FakeRa2yrcppServer server = new(static command => FakeRa2yrcppServer.Ok(Snapshot(42)));
        await using Ra2yrcppClient client = await Ra2yrcppClient.ConnectAsync(server.Uri);

        GetGameState result = await client.RunAsync(new GetGameState());

        Assert.Equal(42u, result.State.CurrentFrame);
        Command[] sent = server.Received.ToArray();
        Assert.Equal(CommandType.ClientCommand, sent[0].CommandType);
        Assert.Equal("type.googleapis.com/ra2yrproto.commands.GetGameState", sent[0].Command_.TypeUrl);
        Assert.All(sent.Skip(1), static poll => Assert.Equal(CommandType.PollBlocking, poll.CommandType));
        // Queue 0 is "my own queue": never read, and so consume, another connection's results.
        Assert.All(server.PolledQueues, static queue => Assert.Equal(0UL, queue));
        Assert.All(server.FrameTypes, static type => Assert.Equal(WebSocketMessageType.Binary, type));
    }

    [Fact]
    public async Task AnErrorResultBecomesATypedExceptionWithTheForksMessage()
    {
        await using FakeRa2yrcppServer server = new(static command => FakeRa2yrcppServer.Error(command.Unpack<UnitOrder>(), "order rejected: local player is an observer"));
        await using Ra2yrcppClient client = await Ra2yrcppClient.ConnectAsync(server.Uri);

        Ra2yrcppCommandException error = await Assert.ThrowsAsync<Ra2yrcppCommandException>(() => client.RunAsync(new UnitOrder { Action = UnitAction.Deploy }));

        Assert.Equal("order rejected: local player is an observer", error.ErrorMessage);
        Assert.Equal("ra2yrproto.commands.UnitOrder", error.Command);
    }

    [Fact]
    public async Task ARefusalBeforeTheCommandRunsIsAlsoACommandException()
    {
        await using FakeRa2yrcppServer server = new(static command => FakeRa2yrcppServer.Ok(command.Unpack<AddMessage>()))
        {
            Refuse = static command => FakeRa2yrcppServer.Text(ResponseCode.Error, "command not permitted by allowedCommands: ra2yrproto.commands.AddMessage"),
        };
        await using Ra2yrcppClient client = await Ra2yrcppClient.ConnectAsync(server.Uri);

        Ra2yrcppCommandException error = await Assert.ThrowsAsync<Ra2yrcppCommandException>(() => client.RunAsync(new AddMessage { Message = "hi" }));

        Assert.Contains("allowedCommands", error.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommandWithoutAResultTimesOutAndClosesTheConnection()
    {
        await using FakeRa2yrcppServer server = new(static _ => null);
        await using Ra2yrcppClient client = await Ra2yrcppClient.ConnectAsync(server.Uri, new Ra2yrcppClientOptions(CommandTimeout: TimeSpan.FromMilliseconds(300), PollTimeout: TimeSpan.FromMilliseconds(50)));

        await Assert.ThrowsAsync<TimeoutException>(() => client.RunAsync(new GetGameState()));

        // A reply may still be on its way; the connection cannot be trusted to pair the next one.
        Assert.False(client.IsOpen);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RunAsync(new GetGameState()));
    }

    [Fact]
    public async Task CancellationStopsAWaitingCommand()
    {
        await using FakeRa2yrcppServer server = new(static _ => null);
        await using Ra2yrcppClient client = await Ra2yrcppClient.ConnectAsync(server.Uri, new Ra2yrcppClientOptions(CommandTimeout: TimeSpan.FromSeconds(30), PollTimeout: TimeSpan.FromMilliseconds(50)));
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RunAsync(new GetGameState(), cancel.Token));
    }

    [Fact]
    public async Task ConcurrentCallersAreServedOneCommandAtATime()
    {
        uint frame = 0;
        await using FakeRa2yrcppServer server = new(_ => FakeRa2yrcppServer.Ok(Snapshot(Interlocked.Increment(ref frame))));
        await using Ra2yrcppClient client = await Ra2yrcppClient.ConnectAsync(server.Uri);

        GetGameState[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.RunAsync(new GetGameState())));

        Assert.Equal(Enumerable.Range(1, 8).Select(static f => (uint)f), results.Select(static r => r.State.CurrentFrame).Order());
        Assert.Equal(1, server.MaximumInFlight);
    }
}
