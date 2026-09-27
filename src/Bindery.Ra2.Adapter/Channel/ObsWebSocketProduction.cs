// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bindery.Ra2.Adapter.Channel;

/// <summary>One text-message connection. The seam lets the protocol be tested without OBS.</summary>
public interface IObsConnection : IAsyncDisposable
{
    Task SendAsync(string message, CancellationToken cancellationToken);

    Task<string> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Drives OBS through its built-in obs-websocket v5 server: scene switching
/// and starting or stopping the stream output.
/// </summary>
/// <remarks>
/// OBS publishes one stream, to the local MediaMTX room path. The public
/// Twitch copy is relayed from MediaMTX (see deploy/ra2-channel), so turning
/// public output on or off never touches the capture or the room stream.
/// </remarks>
public sealed class ObsWebSocketProduction : IBroadcastProduction, IAsyncDisposable
{
    public const int RpcVersion = 1;

    private const int OpHello = 0;
    private const int OpIdentify = 1;
    private const int OpIdentified = 2;
    private const int OpRequest = 6;
    private const int OpRequestResponse = 7;

    private readonly Func<CancellationToken, Task<IObsConnection>> connect;
    private readonly string? password;
    private readonly IReadOnlyDictionary<string, string> matchSceneByClient;
    private readonly IReadOnlyList<string> requiredAudioInputs;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IObsConnection? connection;
    private BroadcastScenes scenes = new();
    private long nextRequest;

    /// <param name="matchSceneByClient">
    /// Optional scene per capture client instance, for a collection with one
    /// window-capture scene per client. Without it the plan's match scene is used.
    /// </param>
    /// <param name="requiredAudioInputs">
    /// OBS inputs that carry the game's sound. Preflight refuses to go on air
    /// if any of them is missing or muted: a silent channel is the failure
    /// nobody notices from the picture.
    /// </param>
    public ObsWebSocketProduction(
        Func<CancellationToken, Task<IObsConnection>> connect,
        string? password,
        IReadOnlyDictionary<string, string>? matchSceneByClient = null,
        IReadOnlyList<string>? requiredAudioInputs = null)
    {
        this.connect = connect ?? throw new ArgumentNullException(nameof(connect));
        this.password = password;
        this.matchSceneByClient = matchSceneByClient ?? new Dictionary<string, string>();
        this.requiredAudioInputs = requiredAudioInputs ?? [];
    }

    public static ObsWebSocketProduction ForUri(
        Uri uri,
        string? password,
        IReadOnlyDictionary<string, string>? matchSceneByClient = null,
        IReadOnlyList<string>? requiredAudioInputs = null)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return new ObsWebSocketProduction(ct => ClientWebSocketObsConnection.ConnectAsync(uri, ct), password, matchSceneByClient, requiredAudioInputs);
    }

    /// <summary>
    /// What would stop this production from working: missing scenes and
    /// missing or muted audio inputs. Empty means ready.
    /// </summary>
    public async Task<IReadOnlyList<string>> PreflightAsync(BroadcastPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        BroadcastScenes planned = plan.EffectiveScenes;
        List<string> problems = [];
        JsonNode? list = await RequestAsync("GetSceneList", null, cancellationToken).ConfigureAwait(false);
        HashSet<string> present = new(
            (list?["scenes"] as JsonArray ?? []).Select(static scene => scene?["sceneName"]?.GetValue<string>()).OfType<string>(),
            StringComparer.Ordinal);
        foreach (string scene in new[] { planned.Holding, planned.Match }.Concat(matchSceneByClient.Values).Distinct(StringComparer.Ordinal))
        {
            if (!present.Contains(scene)) problems.Add($"scene '{scene}' does not exist");
        }
        foreach (string input in requiredAudioInputs)
        {
            try
            {
                JsonNode? mute = await RequestAsync("GetInputMute", new JsonObject { ["inputName"] = input }, cancellationToken).ConfigureAwait(false);
                if (mute?["inputMuted"]?.GetValue<bool>() == true) problems.Add($"audio input '{input}' is muted");
            }
            catch (InvalidOperationException exception)
            {
                problems.Add($"audio input '{input}' is unavailable: {exception.Message}");
            }
        }
        return problems;
    }

    public async Task StartAsync(BroadcastPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        IReadOnlyList<string> problems = await PreflightAsync(plan, cancellationToken).ConfigureAwait(false);
        if (problems.Count > 0) throw new InvalidOperationException("OBS is not ready: " + string.Join("; ", problems));
        scenes = plan.EffectiveScenes;
        await SetSceneAsync(scenes.Holding, cancellationToken).ConfigureAwait(false);
        // StartStream fails with OutputRunning if a previous session left the
        // output up; that is the state we want, so check rather than fail.
        JsonNode? status = await RequestAsync("GetStreamStatus", null, cancellationToken).ConfigureAwait(false);
        if (status?["outputActive"]?.GetValue<bool>() != true)
            await RequestAsync("StartStream", null, cancellationToken).ConfigureAwait(false);
    }

    public Task ShowHoldingAsync(string reason, CancellationToken cancellationToken) => SetSceneAsync(scenes.Holding, cancellationToken);

    public Task ShowMatchAsync(CaptureSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        string scene = matchSceneByClient.TryGetValue(source.ClientInstanceId, out string? mapped) ? mapped : scenes.Match;
        return SetSceneAsync(scene, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (connection is null) return;
        JsonNode? status = await RequestAsync("GetStreamStatus", null, cancellationToken).ConfigureAwait(false);
        if (status?["outputActive"]?.GetValue<bool>() == true)
            await RequestAsync("StopStream", null, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
        connection = null;
        gate.Dispose();
    }

    /// <summary>The obs-websocket v5 authentication string.</summary>
    public static string Authentication(string password, string salt, string challenge)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(challenge);
        string secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    private Task SetSceneAsync(string scene, CancellationToken cancellationToken) =>
        RequestAsync("SetCurrentProgramScene", new JsonObject { ["sceneName"] = scene }, cancellationToken);

    private async Task<JsonNode?> RequestAsync(string requestType, JsonObject? data, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IObsConnection open = connection ??= await IdentifyAsync(cancellationToken).ConfigureAwait(false);
            string requestId = Interlocked.Increment(ref nextRequest).ToString(System.Globalization.CultureInfo.InvariantCulture);
            JsonObject d = new() { ["requestType"] = requestType, ["requestId"] = requestId };
            if (data is not null) d["requestData"] = data;
            await open.SendAsync(new JsonObject { ["op"] = OpRequest, ["d"] = d }.ToJsonString(), cancellationToken).ConfigureAwait(false);
            while (true)
            {
                JsonNode message = Parse(await open.ReceiveAsync(cancellationToken).ConfigureAwait(false));
                // Events (op 5) and other traffic may interleave; skip them.
                if (message["op"]?.GetValue<int>() != OpRequestResponse) continue;
                JsonNode? response = message["d"];
                if (response?["requestId"]?.GetValue<string>() != requestId) continue;
                JsonNode? status = response["requestStatus"];
                if (status?["result"]?.GetValue<bool>() != true)
                {
                    int code = status?["code"]?.GetValue<int>() ?? 0;
                    string comment = status?["comment"]?.GetValue<string>() ?? "no comment";
                    throw new InvalidOperationException($"OBS rejected {requestType} ({code}): {comment}");
                }
                return response["responseData"];
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IObsConnection> IdentifyAsync(CancellationToken cancellationToken)
    {
        IObsConnection open = await connect(cancellationToken).ConfigureAwait(false);
        try
        {
            JsonNode hello = Parse(await open.ReceiveAsync(cancellationToken).ConfigureAwait(false));
            if (hello["op"]?.GetValue<int>() != OpHello) throw new InvalidOperationException("OBS did not open with Hello");
            JsonObject identify = new() { ["rpcVersion"] = RpcVersion, ["eventSubscriptions"] = 0 };
            JsonNode? auth = hello["d"]?["authentication"];
            if (auth is not null)
            {
                if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("OBS requires a websocket password");
                identify["authentication"] = Authentication(
                    password,
                    auth["salt"]?.GetValue<string>() ?? throw new InvalidOperationException("OBS Hello has no salt"),
                    auth["challenge"]?.GetValue<string>() ?? throw new InvalidOperationException("OBS Hello has no challenge"));
            }
            await open.SendAsync(new JsonObject { ["op"] = OpIdentify, ["d"] = identify }.ToJsonString(), cancellationToken).ConfigureAwait(false);
            JsonNode identified = Parse(await open.ReceiveAsync(cancellationToken).ConfigureAwait(false));
            if (identified["op"]?.GetValue<int>() != OpIdentified) throw new InvalidOperationException("OBS did not identify the session");
            return open;
        }
        catch
        {
            await open.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static JsonNode Parse(string message) =>
        JsonNode.Parse(message) ?? throw new JsonException("OBS sent an empty message");
}

/// <summary>The real transport: a text WebSocket to OBS.</summary>
public sealed class ClientWebSocketObsConnection : IObsConnection
{
    private readonly ClientWebSocket socket;

    private ClientWebSocketObsConnection(ClientWebSocket socket) => this.socket = socket;

    public static async Task<IObsConnection> ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        ClientWebSocket socket = new();
        try
        {
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            return new ClientWebSocketObsConnection(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public Task SendAsync(string message, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cancellationToken);

    public async Task<string> ReceiveAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        using MemoryStream message = new();
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("OBS closed the websocket");
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(message.ToArray());
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (socket.State == WebSocketState.Open)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "channel stopped", CancellationToken.None).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // Already gone; nothing to release beyond the socket itself.
            }
        }
        socket.Dispose();
    }
}
