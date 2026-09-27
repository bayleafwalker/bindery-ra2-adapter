// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Bindery.Ra2.Bot;

namespace Bindery.Ra2.Adapter.Bot;

/// <summary>
/// An <see cref="IRa2TelemetrySource"/> that replays recorded raw observations in their recorded order, so
/// <see cref="Ra2BotHost"/> can be driven offline from a capture (or a hand-written fixture) exactly as it would
/// be from the native ra2yrcpp stream. The file form is NDJSON, one <see cref="RawObservation"/> per line in
/// <see cref="BotJson.Options"/> (camelCase) form; <see cref="WriteNdjson"/> produces it.
/// </summary>
public sealed class RecordedRa2TelemetrySource : IRa2TelemetrySource
{
    private readonly IReadOnlyList<RawObservation> observations;

    public RecordedRa2TelemetrySource(IReadOnlyList<RawObservation> observations, string? recordingPath = null)
    {
        ArgumentNullException.ThrowIfNull(observations);
        this.observations = observations;
        Capture = new Ra2TelemetryCapture("recording", new Ra2YrcppEndpoint("recording", 1), observations.Count > 0, observations.Count, recordingPath);
    }

    public Ra2TelemetryCapture Capture { get; }

    /// <summary>Loads an NDJSON recording.</summary>
    public static RecordedRa2TelemetrySource FromNdjson(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        List<RawObservation> observations = [];
        foreach (string line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            observations.Add(JsonSerializer.Deserialize<RawObservation>(line, BotJson.Options)
                ?? throw new InvalidDataException($"null observation in {path}"));
        }
        return new RecordedRa2TelemetrySource(observations, path);
    }

    /// <summary>Writes observations as an NDJSON recording (UTF-8, <c>\n</c>-separated).</summary>
    public static void WriteNdjson(string path, IEnumerable<RawObservation> observations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(observations);
        using StreamWriter writer = new(path, false, new UTF8Encoding(false)) { NewLine = "\n" };
        foreach (RawObservation observation in observations) writer.WriteLine(JsonSerializer.Serialize(observation, BotJson.Options));
    }

    public async IAsyncEnumerable<RawObservation> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (RawObservation observation in observations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return observation;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
