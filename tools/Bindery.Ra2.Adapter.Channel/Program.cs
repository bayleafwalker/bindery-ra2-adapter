// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using Bindery.Ra2.Adapter;
using Bindery.Ra2.Adapter.Channel;
using Bindery.Ra2.Adapter.Ra2yrcpp;

if (args.Length == 2 && args[0] == "report")
{
    // Compare controllers across every match the channel recorded.
    try
    {
        Console.Write(ChannelExperimentReport.RenderTable(ChannelExperimentReport.Build(ChannelExperimentReport.Read(args[1]))));
        return 0;
    }
    catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"report failed: {exception.Message}");
        return 1;
    }
}
if (args.Length != 1)
{
    Console.Error.WriteLine("usage: Bindery.Ra2.Adapter.Channel <channel-settings.json>");
    Console.Error.WriteLine("       Bindery.Ra2.Adapter.Channel report <channel-matches.ndjson>");
    return 2;
}

using CancellationTokenSource stop = new();
ChannelRunner? runner = null;
List<Ra2yrcppCommandSink> seatSinks = [];
int interrupts = 0;
// First Ctrl+C: finish the current match, then stop. Second: stop now.
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    if (Interlocked.Increment(ref interrupts) == 1 && runner is not null)
    {
        Console.WriteLine("draining: the current match will finish; press Ctrl+C again to stop now");
        runner.RequestDrain();
        return;
    }
    stop.Cancel();
};

try
{
    ChannelToolSettings settings = JsonSerializer.Deserialize<ChannelToolSettings>(
        await File.ReadAllTextAsync(args[0]),
        new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidOperationException("settings file was empty");
    settings.Validate();
    LiveAcceptanceSettings live = settings.Live;
    Uri serviceUri = new(live.ServiceUri, UriKind.Absolute);
    // Same connection policy as the acceptance harness: matches are long and
    // quiet, so idle pooled connections are retired rather than reused stale.
    SocketsHttpHandler handler = new()
    {
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };
    using HttpClient httpClient = new(handler) { BaseAddress = serviceUri, Timeout = TimeSpan.FromSeconds(60) };
    BinderyAdapterClient controlPlane = new(httpClient);
    if (live.TransportProvider != Ra2LabProfile.TransportProviderId)
        throw new ArgumentException("the channel plays the live cncnet-private path only");
    CncNetPrivateMatchDriver driver = new(controlPlane, serviceUri);

    ISpawnerBoundary spawner = new WindowsSpawnerBoundary();
    (ILiveClientHost firstHost, HttpClient? firstAgent) = await LiveHosts.BuildAsync(live.FirstHost, spawner);
    (ILiveClientHost secondHost, HttpClient? secondAgent) = await LiveHosts.BuildAsync(live.SecondHost, spawner);
    using HttpClient? firstAgentLifetime = firstAgent;
    using HttpClient? secondAgentLifetime = secondAgent;
    (ILiveClientHost? observerHost, HttpClient? observerAgent) = live.HasObserver
        ? await LiveHosts.BuildAsync(live.ObserverHost, spawner)
        : (null, null);
    using HttpClient? observerAgentLifetime = observerAgent;
    await live.ValidateAsync(firstHost, secondHost, observerHost);
    LiveAcceptanceRunner liveRunner = new(driver, controlPlane, firstHost, secondHost, observerHost);

    await using ObsWebSocketProduction? obs = settings.Obs is null ? null : await settings.Obs.CreateAsync();
    IBroadcastProduction production = (IBroadcastProduction?)obs ?? new NoBroadcastProduction();
    if (obs is null) Console.WriteLine("broadcast: disabled (no obs settings); running the match loop only");
    // The audio watch reads OBS's meters on its own connection for as long
    // as the channel runs.
    await using ObsAudioMonitor? audio = settings.Obs is { WatchAudio: true, AudioInputs.Length: > 0 } ? await settings.Obs.CreateAudioMonitorAsync() : null;
    using CancellationTokenSource audioStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
    Task audioWatch = audio is null ? Task.CompletedTask : Task.Run(() => audio.RunAsync(audioStop.Token));

    string channelDirectory = Path.Combine(live.EvidenceDirectory, settings.ChannelId);
    LiveAcceptanceMatchLauncher launcher = new(liveRunner, context =>
    {
        // Every match is a new session: fresh keys and its own evidence folder.
        LiveAcceptanceSettings match = live.WithNewIdempotencyKeys();
        string directory = Path.Combine(channelDirectory, "match-" + context.MatchIndex.ToString("D3", CultureInfo.InvariantCulture));
        Console.WriteLine($"match {context.MatchIndex}: session_idempotency_key={match.SessionIdempotencyKey} evidence={directory}");
        return match.ToRequest(directory, settings.Seed);
    },
    new LiveChannelMatchOptions(
        Telemetry: settings.TelemetryFor,
        AgentSeat: settings.AgentSeat is null ? null : launch =>
        {
            // A fresh controller and connection per match, into the agent client's own service; the sinks close with the channel.
            (AgentSeat seat, Ra2yrcppCommandSink sink) = settings.AgentSeat.CreateSeat(launch);
            lock (seatSinks) seatSinks.Add(sink);
            return seat;
        },
        Overlay: settings.Obs?.OverlayTextInput is { Length: > 0 } ? obs : null));
    if (settings.LiveTelemetry is { } liveTelemetry)
        Console.WriteLine($"telemetry: live ra2yrcpp at {liveTelemetry.Endpoint}");
    else if (string.IsNullOrWhiteSpace(settings.TelemetryRecording))
        Console.WriteLine("telemetry: none attached; match records will carry no winner");
    if (settings.AgentSeat is { } agent)
        Console.WriteLine($"agent seat: house={agent.House} client={agent.ClientInstanceId} routine={agent.Routine}");

    ChannelRequest request = settings.ToRequest(live);
    NdjsonChannelRecordSink records = new(channelDirectory);
    runner = new ChannelRunner(launcher, production, records, health: audio);
    ChannelSessionSummary summary = await runner.RunAsync(request, stop.Token);
    audioStop.Cancel();
    try { await audioWatch; }
    catch (OperationCanceledException)
    {
        // The meters connection ends with the channel.
    }
    catch (Exception exception)
    {
        // Every match already records "sent no meters"; say why once.
        Console.Error.WriteLine($"audio watch stopped: {exception.GetType().Name}: {exception.Message}");
    }

    foreach (ChannelMatchRecord match in summary.Matches)
        Console.WriteLine($"match {match.MatchIndex}: {match.Outcome.ToString().ToLowerInvariant()} session={match.SessionId ?? "-"} seed={match.Seed?.ToString(CultureInfo.InvariantCulture) ?? "-"}{(match.Failure is null ? string.Empty : " failure=" + match.Failure)}");
    foreach (string issue in runner.BroadcastIssues) Console.WriteLine($"broadcast issue: {issue}");
    Console.WriteLine($"stopped: {summary.StopReason}");
    Console.WriteLine($"records={records.Path}");
    return summary.Matches.Any(static m => m.Outcome == ChannelMatchOutcome.Completed) ? 0 : 1;
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    Console.Error.WriteLine("channel stopped by operator");
    return 130;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"channel failed: {exception.Message}");
    return 1;
}
finally
{
    foreach (Ra2yrcppCommandSink sink in seatSinks) await sink.DisposeAsync();
}

internal sealed class ChannelToolSettings
{
    /// <summary>The match itself, exactly as the live-acceptance harness plays it.</summary>
    public LiveAcceptanceSettings Live { get; init; } = new();

    public string ChannelId { get; init; } = "ra2-channel";
    public int MaximumMatches { get; init; } = 1;
    public int HoldingSeconds { get; init; } = 20;

    /// <summary>Pin one seed for every match; omit for a fresh seed each time.</summary>
    public int? Seed { get; init; }

    /// <summary>The rendered client on air; defaults to the first player.</summary>
    public string CaptureClientInstanceId { get; init; } = string.Empty;

    /// <summary>Omit to run the loop without OBS.</summary>
    public ObsSettings? Obs { get; init; }

    public string LocalRoomUri { get; init; } = "rtmp://127.0.0.1:1935/ra2";
    public string TwitchUri { get; init; } = string.Empty;
    public string TwitchStreamKeyEnvironmentVariable { get; init; } = "TWITCH_STREAM_KEY";

    /// <summary>
    /// Records that matches went out publicly. The relay itself is switched by
    /// the MediaMTX flag file; keep the two in agreement.
    /// </summary>
    public bool PublishPublicly { get; init; }

    /// <summary>
    /// A telemetry recording to follow for each match, as NDJSON raw
    /// observations. <c>{channel}</c> and <c>{match}</c> (three digits) are
    /// replaced per match. Omit to run without telemetry.
    /// </summary>
    public string TelemetryRecording { get; init; } = string.Empty;

    /// <summary>
    /// Live telemetry from a client's ra2yrcpp service, instead of a
    /// recording. Omit to keep the recording (or no telemetry).
    /// </summary>
    public Ra2yrcppLiveTelemetrySettings? LiveTelemetry { get; init; }

    /// <summary>
    /// Put the rules playbook controller in one player seat, with live
    /// commands into that player's client. Needs telemetry. Omit for two
    /// players as launched, today's proven path.
    /// </summary>
    public Ra2yrcppAgentSeatSettings? AgentSeat { get; init; }

    public void Validate()
    {
        Ra2yrcppChannelSettings.Validate(LiveTelemetry, TelemetryRecording, AgentSeat);
        if (AgentSeat is null) return;
        LiveClientLaunch? launch = string.Equals(AgentSeat.ClientInstanceId, Live.FirstClientInstanceId, StringComparison.Ordinal) ? Live.FirstLaunch
            : string.Equals(AgentSeat.ClientInstanceId, Live.SecondClientInstanceId, StringComparison.Ordinal) ? Live.SecondLaunch
            : null;
        if (launch is null) throw new ArgumentException($"agentSeat.clientInstanceId {AgentSeat.ClientInstanceId} is not one of the two players");
        if (!string.Equals(launch.PlayerName, AgentSeat.House, StringComparison.Ordinal))
            throw new ArgumentException($"agentSeat.house must be that client's playerName, {launch.PlayerName}");
        if (string.IsNullOrWhiteSpace(launch.CommandEndpoint))
            throw new ArgumentException("the agent client's launch needs commandEndpoint, its own ra2yrcpp service");
        Ra2yrcppClient.UriFor(Ra2YrcppEndpoint.Parse(launch.CommandEndpoint));
    }

    public IRa2TelemetrySource? TelemetryFor(ChannelMatchContext context) =>
        LiveTelemetry is not null
            ? LiveTelemetry.Create()
            : string.IsNullOrWhiteSpace(TelemetryRecording)
            ? null
            : new NdjsonTelemetrySource(
                TelemetryRecording
                    .Replace("{channel}", context.ChannelId, StringComparison.Ordinal)
                    .Replace("{match}", context.MatchIndex.ToString("D3", CultureInfo.InvariantCulture), StringComparison.Ordinal),
                follow: true);

    public string MatchScene { get; init; } = "ra2-match";
    public string HoldingScene { get; init; } = "ra2-holding";

    public ChannelRequest ToRequest(LiveAcceptanceSettings live)
    {
        string captureId = string.IsNullOrWhiteSpace(CaptureClientInstanceId) ? live.FirstClientInstanceId : CaptureClientInstanceId;
        ClientClass captureClass = live.HasObserver && string.Equals(captureId, live.ObserverClientInstanceId, StringComparison.Ordinal)
            ? ClientClass.Observer
            : ClientClass.Player;
        List<BroadcastDestination> destinations = [new("room", BroadcastDestinationKind.LocalRoom, new Uri(LocalRoomUri, UriKind.Absolute))];
        if (!string.IsNullOrWhiteSpace(TwitchUri))
            destinations.Add(new("twitch", BroadcastDestinationKind.Twitch, new Uri(TwitchUri, UriKind.Absolute), TwitchStreamKeyEnvironmentVariable));
        return new ChannelRequest(
            ChannelId,
            live.MapId,
            new CaptureSource(captureId, captureClass),
            new BroadcastPlan(destinations, new BroadcastScenes(MatchScene, HoldingScene), PublishPublicly),
            MaximumMatches,
            AgentSeat?.ToAssignment(),
            HoldingDuration: TimeSpan.FromSeconds(HoldingSeconds));
    }
}

internal sealed class ObsSettings
{
    public string Uri { get; init; } = "ws://127.0.0.1:4455";

    /// <summary>Read the websocket password from a file or an environment variable; never inline it.</summary>
    public string PasswordFile { get; init; } = string.Empty;
    public string PasswordEnvironmentVariable { get; init; } = "OBS_WEBSOCKET_PASSWORD";

    public string[] AudioInputs { get; init; } = [];
    public Dictionary<string, string> MatchSceneByClient { get; init; } = [];

    /// <summary>An OBS text source to write the scoreboard into; omit for no overlay.</summary>
    public string OverlayTextInput { get; init; } = string.Empty;

    /// <summary>Watch <see cref="AudioInputs"/> for silence during each match.</summary>
    public bool WatchAudio { get; init; } = true;

    public async Task<ObsWebSocketProduction> CreateAsync() =>
        ObsWebSocketProduction.ForUri(
            new Uri(Uri, UriKind.Absolute),
            await PasswordAsync(),
            MatchSceneByClient,
            AudioInputs,
            string.IsNullOrWhiteSpace(OverlayTextInput) ? null : OverlayTextInput);

    public async Task<ObsAudioMonitor> CreateAudioMonitorAsync() =>
        ObsAudioMonitor.ForUri(new Uri(Uri, UriKind.Absolute), await PasswordAsync(), AudioInputs);

    private async Task<string?> PasswordAsync() =>
        !string.IsNullOrWhiteSpace(PasswordFile)
            ? (await File.ReadAllTextAsync(PasswordFile)).Trim()
            : Environment.GetEnvironmentVariable(PasswordEnvironmentVariable);
}
