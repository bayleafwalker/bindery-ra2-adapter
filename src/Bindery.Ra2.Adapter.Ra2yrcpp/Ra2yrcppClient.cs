// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net.WebSockets;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Ra2Yrproto;

namespace Bindery.Ra2.Adapter.Ra2yrcpp;

/// <summary>The fork refused or failed a command: an ERROR result, or an ERROR response before it ran.</summary>
public sealed class Ra2yrcppCommandException(string command, string errorMessage)
    : Exception($"{command}: {errorMessage}")
{
    public string Command { get; } = command;

    /// <summary>The fork's own words, e.g. <c>order rejected: local player is an observer</c>.</summary>
    public string ErrorMessage { get; } = errorMessage;
}

public sealed record Ra2yrcppClientOptions(TimeSpan? CommandTimeout = null, TimeSpan? PollTimeout = null, int MaximumMessageBytes = 64 * 1024 * 1024, TimeSpan? ConnectTimeout = null)
{
    public TimeSpan EffectiveConnectTimeout => ConnectTimeout ?? TimeSpan.FromSeconds(5);

    public TimeSpan EffectiveCommandTimeout => CommandTimeout ?? TimeSpan.FromSeconds(10);

    public TimeSpan EffectivePollTimeout => PollTimeout ?? TimeSpan.FromSeconds(1);
}

/// <summary>
/// One connection to a game client's ra2yrcpp service: a WebSocket where
/// each binary message is one protobuf <c>Command</c> or <c>Response</c>.
/// </summary>
/// <remarks>
/// A command is sent as <c>CLIENT_COMMAND</c>, then its result is fetched
/// with <c>POLL_BLOCKING</c> on this connection's own queue (queue id 0),
/// never another connection's: polling consumes results. Game orders run at
/// the next game frame, so the poll repeats until the result arrives or the
/// command timeout passes. One command is in flight at a time; later callers
/// wait their turn. A timeout, a cancellation or a broken frame mid-exchange
/// aborts the connection, because a late reply would otherwise be paired with
/// the next command. Text frames are never sent: the service drops them.
/// </remarks>
public sealed class Ra2yrcppClient : IAsyncDisposable
{
    private const string TypeUrlPrefix = "type.googleapis.com/";

    private readonly WebSocket socket;
    private readonly Ra2yrcppClientOptions options;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool aborted;

    public Ra2yrcppClient(WebSocket socket, Ra2yrcppClientOptions? options = null)
    {
        this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
        this.options = options ?? new Ra2yrcppClientOptions();
    }

    public bool IsOpen => !aborted && socket.State == WebSocketState.Open;

    public static async Task<Ra2yrcppClient> ConnectAsync(Uri uri, Ra2yrcppClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        options ??= new Ra2yrcppClientOptions();
        ClientWebSocket socket = new();
        // A service that accepts the TCP connection but never completes the
        // upgrade must not hold the caller forever.
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.EffectiveConnectTimeout);
        try
        {
            await socket.ConnectAsync(uri, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            socket.Dispose();
            throw new TimeoutException($"no ra2yrcpp WebSocket at {uri} within {options.EffectiveConnectTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s", exception);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return new Ra2yrcppClient(socket, options);
    }

    /// <summary>The service's WebSocket address for a <c>host:port</c> endpoint.</summary>
    /// <exception cref="ArgumentException">The host is an IPv6 address: the fork's service listens on IPv4 only.</exception>
    public static Uri UriFor(Ra2YrcppEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Host.Contains(':', StringComparison.Ordinal))
            throw new ArgumentException($"ra2yrcpp endpoint {endpoint.Host} is IPv6; the fork's service listens on IPv4 only, so use its IPv4 address or host name", nameof(endpoint));
        return new Uri($"ws://{endpoint.Host}:{endpoint.Port.ToString(CultureInfo.InvariantCulture)}/");
    }

    /// <summary>Runs a command whose result is the same message filled in, as every ra2yrcpp command's is.</summary>
    public Task<T> RunAsync<T>(T command, CancellationToken cancellationToken = default)
        where T : IMessage<T>, new() =>
        RunAsync<T, T>(command, cancellationToken);

    public async Task<TResult> RunAsync<TCommand, TResult>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : IMessage
        where TResult : IMessage<TResult>, new()
    {
        ArgumentNullException.ThrowIfNull(command);
        string name = command.Descriptor.FullName;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsOpen) throw new InvalidOperationException("the ra2yrcpp connection is closed");
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.EffectiveCommandTimeout);
            try
            {
                Response ack = await ExchangeAsync(new Command { CommandType = CommandType.ClientCommand, Command_ = Any.Pack(command) }, deadline.Token).ConfigureAwait(false);
                if (ack.Code != ResponseCode.Ok) throw new Ra2yrcppCommandException(name, Describe(ack));
                long id = Unpack<RunCommandAck>(ack).Id;
                PollResults poll = new() { Args = new PollResults.Types.Args { QueueId = 0, Timeout = (ulong)options.EffectivePollTimeout.TotalMilliseconds } };
                Command request = new() { CommandType = CommandType.PollBlocking, Command_ = Any.Pack(poll) };
                while (true)
                {
                    Response response = await ExchangeAsync(request, deadline.Token).ConfigureAwait(false);
                    if (response.Code != ResponseCode.Ok) throw new Ra2yrcppCommandException(name, Describe(response));
                    // Only this connection's commands are in the queue, and
                    // only one at a time; anything else is a stale leftover.
                    CommandResult? result = Unpack<PollResults>(response).Result?.Results.FirstOrDefault(r => r.CommandId == id);
                    if (result is null) continue;
                    if (result.ResultCode != ResponseCode.Ok)
                        throw new Ra2yrcppCommandException(name, string.IsNullOrEmpty(result.ErrorMessage) ? "command failed without a message" : result.ErrorMessage);
                    if (result.Result is null || !result.Result.Is(new TResult().Descriptor))
                        throw new InvalidDataException($"{name}: result is {result.Result?.TypeUrl ?? "empty"}, not {new TResult().Descriptor.FullName}");
                    return result.Result.Unpack<TResult>();
                }
            }
            // A cancelled socket operation may surface as a WebSocketException
            // rather than a cancellation; once the deadline or the caller has
            // fired, it is reported as what it is.
            catch (Exception exception) when (deadline.IsCancellationRequested && (exception is OperationCanceledException or WebSocketException or ObjectDisposedException))
            {
                Abort();
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException($"{name} was cancelled", exception, cancellationToken);
                throw new TimeoutException($"{name}: no result within {options.EffectiveCommandTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s", exception);
            }
            catch (Exception exception) when (exception is WebSocketException or InvalidDataException or InvalidProtocolBufferException)
            {
                Abort();
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Response> ExchangeAsync(Command command, CancellationToken cancellationToken)
    {
        await socket.SendAsync(command.ToByteArray(), WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
        using MemoryStream message = new();
        byte[] buffer = new byte[1 << 16];
        while (true)
        {
            WebSocketReceiveResult received = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (received.MessageType == WebSocketMessageType.Close) throw new WebSocketException("ra2yrcpp closed the connection");
            if (message.Length + received.Count > options.MaximumMessageBytes)
                throw new InvalidDataException($"ra2yrcpp message exceeds {options.MaximumMessageBytes} bytes");
            message.Write(buffer, 0, received.Count);
            if (!received.EndOfMessage) continue;
            if (received.MessageType != WebSocketMessageType.Binary) throw new InvalidDataException("ra2yrcpp answered with a text frame");
            message.Position = 0;
            return Response.Parser.ParseFrom(message);
        }
    }

    private static T Unpack<T>(Response response)
        where T : IMessage<T>, new() =>
        response.Body is { } body && body.Is(new T().Descriptor)
            ? body.Unpack<T>()
            : throw new InvalidDataException($"expected {new T().Descriptor.FullName}, got {response.Body?.TypeUrl ?? "no body"}");

    private static string Describe(Response response) =>
        response.Body is { } body && body.Is(TextResponse.Descriptor)
            ? body.Unpack<TextResponse>().Message
            : $"{response.Code} response ({response.Body?.TypeUrl.Replace(TypeUrlPrefix, string.Empty, StringComparison.Ordinal) ?? "no body"})";

    private void Abort()
    {
        aborted = true;
        socket.Abort();
    }

    public async ValueTask DisposeAsync()
    {
        if (!aborted && socket.State == WebSocketState.Open)
        {
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
            {
                // Closing is a courtesy; the service cleans up the queue either way.
            }
        }
        socket.Dispose();
        gate.Dispose();
    }
}
