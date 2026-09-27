// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Ra2Yrproto;

namespace Bindery.Ra2.Adapter.Tests;

/// <summary>
/// An in-process stand-in for a game client's ra2yrcpp service: one binary
/// WebSocket message per protobuf message, CLIENT_COMMAND answered with an
/// ack, results fetched with POLL_BLOCKING from the connection's own queue.
/// </summary>
internal sealed class FakeRa2yrcppServer : IAsyncDisposable
{
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task accepting;
    private readonly ConcurrentBag<Task> connections = [];
    private long nextCommandId;
    private int nextQueueId;
    private int inFlight;
    private int maximumInFlight;

    public FakeRa2yrcppServer(Func<Any, CommandResult?> handle)
    {
        Handle = handle;
        int port;
        using (TcpListener probe = new(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        Uri = new Uri($"ws://localhost:{port}/");
        accepting = AcceptAsync();
    }

    public Uri Uri { get; }

    /// <summary>Answers one queued command; null leaves it without a result forever.</summary>
    public Func<Any, CommandResult?> Handle { get; set; }

    /// <summary>Answers CLIENT_COMMAND before it is queued, as the allowlist does; null queues it.</summary>
    public Func<Any, Response?> Refuse { get; set; } = static _ => null;

    /// <summary>Replaces the answer to a message with raw bytes, e.g. a corrupt frame; null answers normally.</summary>
    public Func<Command, byte[]?> Corrupt { get; set; } = static _ => null;

    public ConcurrentQueue<Command> Received { get; } = new();

    public ConcurrentQueue<WebSocketMessageType> FrameTypes { get; } = new();

    public ConcurrentQueue<ulong> PolledQueues { get; } = new();

    public int MaximumInFlight => Volatile.Read(ref maximumInFlight);

    public int Connections => Volatile.Read(ref nextQueueId);

    public static CommandResult Ok(IMessage result) => new() { Result = Any.Pack(result), ResultCode = ResponseCode.Ok };

    public static CommandResult Error(IMessage command, string message) => new() { Result = Any.Pack(command), ResultCode = ResponseCode.Error, ErrorMessage = message };

    public static Response Text(ResponseCode code, string message) => new() { Code = code, Body = Any.Pack(new TextResponse { Message = message }) };

    private async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception) when (stop.IsCancellationRequested) { return; }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }
            connections.Add(ServeAsync(context));
        }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        if (!context.Request.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
            return;
        }
        int queue = Interlocked.Increment(ref nextQueueId);
        HttpListenerWebSocketContext ws = await context.AcceptWebSocketAsync(null);
        WebSocket socket = ws.WebSocket;
        List<CommandResult> pending = [];
        byte[] buffer = new byte[1 << 16];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using MemoryStream message = new();
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(buffer, stop.Token);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                        return;
                    }
                    message.Write(buffer, 0, received.Count);
                }
                while (!received.EndOfMessage);
                FrameTypes.Enqueue(received.MessageType);
                // The real service drops text frames without a reply.
                if (received.MessageType != WebSocketMessageType.Binary) continue;
                Command command = Command.Parser.ParseFrom(message.ToArray());
                Received.Enqueue(command);
                Response response = await AnswerAsync(command, queue, pending);
                await socket.SendAsync(Corrupt(command) ?? response.ToByteArray(), WebSocketMessageType.Binary, true, stop.Token);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // The client went away or the fake is stopping.
        }
    }

    private async Task<Response> AnswerAsync(Command command, int queue, List<CommandResult> pending)
    {
        switch (command.CommandType)
        {
            case CommandType.ClientCommand:
                if (Refuse(command.Command_) is { } refusal) return refusal;
                long id = Interlocked.Increment(ref nextCommandId);
                int now = Interlocked.Increment(ref inFlight);
                int seen;
                while ((seen = Volatile.Read(ref maximumInFlight)) < now && Interlocked.CompareExchange(ref maximumInFlight, now, seen) != seen) { }
                CommandResult? result = Handle(command.Command_);
                if (result is not null)
                {
                    result.CommandId = id;
                    pending.Add(result);
                }
                return new Response { Code = ResponseCode.Ok, Body = Any.Pack(new RunCommandAck { Id = id, QueueId = queue }) };
            case CommandType.PollBlocking:
                PollResults args = command.Command_.Unpack<PollResults>();
                PolledQueues.Enqueue(args.Args.QueueId);
                PollResults poll = new() { Result = new PollResults.Types.Result() };
                if (pending.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(args.Args.Timeout, 50UL)), stop.Token);
                    return new Response { Code = ResponseCode.Ok, Body = Any.Pack(poll) };
                }
                poll.Result.Results.AddRange(pending);
                Interlocked.Add(ref inFlight, -pending.Count);
                pending.Clear();
                return new Response { Code = ResponseCode.Ok, Body = Any.Pack(poll) };
            default:
                return Text(ResponseCode.Error, "unknown command: " + command.CommandType);
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Close();
        try { await accepting; } catch (Exception) { }
        foreach (Task connection in connections)
        {
            try { await connection; } catch (Exception) { }
        }
        stop.Dispose();
    }
}
