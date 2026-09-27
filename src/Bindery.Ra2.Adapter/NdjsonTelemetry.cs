// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bindery.Ra2.Adapter;

/// <summary>
/// The on-disk form of a telemetry recording: one <see cref="RawObservation"/>
/// per line, in snake_case, with the payload kept verbatim.
/// </summary>
/// <remarks>
/// This is a recording format, not the ra2yrcpp wire protocol. The native
/// fork owns protobuf/TCP framing; anything that has already decoded an
/// observation -- the fork's own reader, a bridge-side recorder -- can write
/// this, and a recorded match can be replayed through the channel exactly as
/// it was seen.
/// </remarks>
public static class NdjsonTelemetryFormat
{
    private static readonly JsonSerializerOptions options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Serialize(RawObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return JsonSerializer.Serialize(observation, options);
    }

    public static RawObservation Parse(string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        RawObservation observation = JsonSerializer.Deserialize<RawObservation>(line, options)
            ?? throw new JsonException("empty observation");
        if (string.IsNullOrWhiteSpace(observation.EventId) || string.IsNullOrWhiteSpace(observation.EventType))
            throw new JsonException("an observation needs event_id and event_type");
        return observation with { Payload = observation.Payload.Clone() };
    }
}

/// <summary>
/// Reads a telemetry recording, optionally following it while it is written.
/// </summary>
/// <remarks>
/// With <c>follow</c>, the source waits at the end of the file for more lines
/// until it is cancelled -- which is how the channel closes a match's
/// telemetry -- or until it reads <c>ra2.match.ended</c>. A partial last line
/// is left for the next read rather than parsed early. A malformed line is an
/// error: skipping it would silently open a gap in the evidence.
/// </remarks>
public sealed class NdjsonTelemetrySource(string path, bool follow = false, TimeSpan? pollInterval = null) : IRa2TelemetrySource
{
    private readonly TimeSpan poll = pollInterval ?? TimeSpan.FromMilliseconds(250);
    private long read;

    public string Path { get; } = string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("a recording path is required", nameof(path)) : path;

    public Ra2TelemetryCapture Capture => new(
        "ndjson-recording",
        new Ra2YrcppEndpoint("recording", 1),
        Interlocked.Read(ref read) > 0,
        Interlocked.Read(ref read),
        Path);

    public async IAsyncEnumerable<RawObservation> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Following a recording that does not exist yet is normal: the
        // producer creates it when the match starts.
        while (!File.Exists(Path))
        {
            if (!follow) throw new FileNotFoundException("telemetry recording was not found", Path);
            await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
        }
        await using FileStream stream = new(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream, Encoding.UTF8);
        StringBuilder pending = new();
        long lineNumber = 0;
        char[] buffer = new char[8192];
        while (true)
        {
            int count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (!follow)
                {
                    if (pending.Length > 0 && !string.IsNullOrWhiteSpace(pending.ToString()))
                        yield return ParseLine(pending.ToString(), ++lineNumber);
                    yield break;
                }
                await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
                continue;
            }
            for (int index = 0; index < count; index++)
            {
                char c = buffer[index];
                if (c != '\n')
                {
                    pending.Append(c);
                    continue;
                }
                string line = pending.ToString().TrimEnd('\r');
                pending.Clear();
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                RawObservation observation = ParseLine(line, lineNumber);
                yield return observation;
                if (follow && observation.EventType == Ra2TelemetryEventTypes.MatchEnded) yield break;
            }
        }
    }

    private RawObservation ParseLine(string line, long lineNumber)
    {
        try
        {
            RawObservation observation = NdjsonTelemetryFormat.Parse(line);
            Interlocked.Increment(ref read);
            return observation;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{Path}:{lineNumber}: {exception.Message}", exception);
        }
    }
}
