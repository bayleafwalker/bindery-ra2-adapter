// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json.Nodes;

namespace Bindery.Ra2.Adapter.Channel;

/// <summary>
/// Something that watches the broadcast while a match is on air and says,
/// at the end, what went wrong with it. The channel records the answer; it
/// never stops a match.
/// </summary>
public interface IBroadcastHealth
{
    void BeginMatch();

    /// <summary>Null when nothing was wrong.</summary>
    string? EndMatch();
}

/// <summary>
/// Watches OBS's audio meters and reports silence during a match.
/// </summary>
/// <remarks>
/// The preflight only proves an input exists and is unmuted. This catches
/// what it cannot: a game that stopped producing sound, a capture that lost
/// its application, a guest whose audio device went away. It subscribes to
/// obs-websocket's high-volume <c>InputVolumeMeters</c> events (about 20 per
/// second) on its own connection, so the request connection is never
/// flooded. A silent stretch is counted from the last meter above the
/// threshold; an input that sent no meters at all is reported as missing.
/// </remarks>
public sealed class ObsAudioMonitor : IBroadcastHealth, IAsyncDisposable
{
    /// <summary>obs-websocket <c>EventSubscription::InputVolumeMeters</c>.</summary>
    public const int InputVolumeMetersSubscription = 1 << 16;

    private readonly Func<CancellationToken, Task<IObsConnection>> connect;
    private readonly string? password;
    private readonly IReadOnlyList<string> inputs;
    private readonly double threshold;
    private readonly TimeSpan tolerance;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly Dictionary<string, InputWatch> watches = new(StringComparer.Ordinal);
    private IObsConnection? connection;
    private bool inMatch;

    /// <param name="threshold">Linear peak below which a meter counts as silence; 0.0001 is about -80 dBFS.</param>
    /// <param name="tolerance">Silence shorter than this is a pause, not a fault.</param>
    public ObsAudioMonitor(
        Func<CancellationToken, Task<IObsConnection>> connect,
        string? password,
        IReadOnlyList<string> inputs,
        double threshold = 0.0001,
        TimeSpan? tolerance = null,
        TimeProvider? clock = null)
    {
        this.connect = connect ?? throw new ArgumentNullException(nameof(connect));
        this.password = password;
        this.inputs = inputs is { Count: > 0 } ? inputs : throw new ArgumentException("name at least one audio input to watch", nameof(inputs));
        this.threshold = threshold;
        this.tolerance = tolerance ?? TimeSpan.FromSeconds(5);
        this.clock = clock ?? TimeProvider.System;
    }

    public static ObsAudioMonitor ForUri(Uri uri, string? password, IReadOnlyList<string> inputs) =>
        new(ct => ClientWebSocketObsConnection.ConnectAsync(uri ?? throw new ArgumentNullException(nameof(uri)), ct), password, inputs);

    /// <summary>Reads meters until cancelled. Run it alongside the channel.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        connection = await ObsWebSocketProduction.IdentifyAsync(connect, password, InputVolumeMetersSubscription, cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            JsonNode message = ObsWebSocketProduction.Parse(await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false));
            if (message["op"]?.GetValue<int>() != 5) continue;
            JsonNode? data = message["d"];
            if (data?["eventType"]?.GetValue<string>() != "InputVolumeMeters") continue;
            Meter(data["eventData"]?["inputs"] as JsonArray);
        }
    }

    public void BeginMatch()
    {
        lock (gate)
        {
            DateTimeOffset now = clock.GetUtcNow();
            watches.Clear();
            foreach (string input in inputs) watches[input] = new InputWatch(now);
            inMatch = true;
        }
    }

    public string? EndMatch()
    {
        lock (gate)
        {
            if (!inMatch) return null;
            inMatch = false;
            DateTimeOffset now = clock.GetUtcNow();
            List<string> problems = [];
            foreach ((string input, InputWatch watch) in watches.OrderBy(static w => w.Key, StringComparer.Ordinal))
            {
                watch.Close(now);
                if (watch.Meters == 0)
                    problems.Add($"audio input '{input}' sent no meters");
                else if (watch.LongestSilence >= tolerance)
                    problems.Add($"audio input '{input}' was silent for {watch.LongestSilence.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}s");
            }
            return problems.Count == 0 ? null : string.Join("; ", problems);
        }
    }

    /// <summary>One <c>InputVolumeMeters</c> payload. Public for tests and for callers with their own event loop.</summary>
    public void Meter(JsonArray? meters)
    {
        if (meters is null) return;
        lock (gate)
        {
            if (!inMatch) return;
            DateTimeOffset now = clock.GetUtcNow();
            foreach (JsonNode? input in meters)
            {
                string? name = input?["inputName"]?.GetValue<string>();
                if (name is null || !watches.TryGetValue(name, out InputWatch? watch)) continue;
                // inputLevelsMul is one [magnitude, peak, inputPeak] triple per channel.
                double peak = 0;
                foreach (JsonNode? channel in input!["inputLevelsMul"] as JsonArray ?? [])
                {
                    if (channel is JsonArray { Count: >= 2 } levels) peak = Math.Max(peak, levels[1]!.GetValue<double>());
                }
                watch.Observe(now, peak >= threshold);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
        connection = null;
    }

    private sealed class InputWatch(DateTimeOffset startedAt)
    {
        private DateTimeOffset lastSound = startedAt;

        public long Meters { get; private set; }

        public TimeSpan LongestSilence { get; private set; }

        public void Observe(DateTimeOffset at, bool sound)
        {
            Meters++;
            if (!sound) return;
            Close(at);
            lastSound = at;
        }

        public void Close(DateTimeOffset at)
        {
            TimeSpan silence = at - lastSound;
            if (silence > LongestSilence) LongestSilence = silence;
        }
    }
}
