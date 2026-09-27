// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Pipe = System.Threading.Channels.Channel;

namespace Bindery.Ra2.Adapter.Channel;

/// <summary>
/// Follows one match through the instrumented stream: whether it started,
/// which houses joined and were defeated, whether it ended, and who won.
/// </summary>
/// <remarks>
/// The winner is only ever what the telemetry says. It is the
/// <c>winner</c> field of <c>ra2.match.ended</c> when the bridge sends one;
/// otherwise, once the match has ended, the single joined house that was
/// never defeated. Anything else -- a draw, a disconnect, a stream that
/// stopped early -- leaves it null.
/// </remarks>
public sealed class MatchTelemetryTracker
{
    private readonly HashSet<string> joined = new(StringComparer.Ordinal);
    private readonly HashSet<string> defeated = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private string? declaredWinner;

    public MatchTelemetryTracker(string houseField = "house", string winnerField = "winner")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(houseField);
        ArgumentException.ThrowIfNullOrWhiteSpace(winnerField);
        HouseField = houseField;
        WinnerField = winnerField;
    }

    public string HouseField { get; }

    public string WinnerField { get; }

    public bool Started { get; private set; }

    public bool Ended { get; private set; }

    public long Observed { get; private set; }

    public IReadOnlyCollection<string> Joined
    {
        get { lock (gate) return joined.ToArray(); }
    }

    public IReadOnlyCollection<string> Defeated
    {
        get { lock (gate) return defeated.ToArray(); }
    }

    public string? Winner
    {
        get
        {
            lock (gate)
            {
                if (declaredWinner is not null) return declaredWinner;
                if (!Ended) return null;
                string[] standing = joined.Where(house => !defeated.Contains(house)).ToArray();
                return standing.Length == 1 ? standing[0] : null;
            }
        }
    }

    public void Observe(RawObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (gate)
        {
            Observed++;
            switch (observation.EventType)
            {
                case Ra2TelemetryEventTypes.MatchStarted:
                    Started = true;
                    break;
                case Ra2TelemetryEventTypes.PlayerJoined when Read(observation.Payload, HouseField) is { } house:
                    joined.Add(house);
                    break;
                case Ra2TelemetryEventTypes.PlayerDefeated when Read(observation.Payload, HouseField) is { } house:
                    joined.Add(house);
                    defeated.Add(house);
                    break;
                case Ra2TelemetryEventTypes.MatchEnded:
                    Ended = true;
                    declaredWinner ??= Read(observation.Payload, WinnerField);
                    break;
            }
        }
    }

    private static string? Read(JsonElement payload, string field) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(field, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}

/// <summary>
/// Reads one telemetry source once and hands every observation to several
/// readers -- the match tracker and an agent seat -- each at its own pace.
/// </summary>
public sealed class TelemetryFanOut
{
    private readonly IRa2TelemetrySource source;
    private readonly List<Channel<RawObservation>> branches = [];

    public TelemetryFanOut(IRa2TelemetrySource source) =>
        this.source = source ?? throw new ArgumentNullException(nameof(source));

    /// <summary>A reader that sees every observation from the point it was created. Create them before <see cref="RunAsync"/>.</summary>
    public IRa2TelemetrySource Branch()
    {
        Channel<RawObservation> branch = Pipe.CreateUnbounded<RawObservation>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        branches.Add(branch);
        return new BranchSource(source.Capture, branch.Reader);
    }

    /// <summary>
    /// Pumps until the source ends or is cancelled, then completes every
    /// branch so its readers finish normally rather than by cancellation.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await foreach (RawObservation observation in source.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (Channel<RawObservation> branch in branches) branch.Writer.TryWrite(observation);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping the pump is how a match's telemetry is closed.
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            foreach (Channel<RawObservation> branch in branches) branch.Writer.TryComplete(failure);
        }
    }

    private sealed class BranchSource(Ra2TelemetryCapture capture, ChannelReader<RawObservation> reader) : IRa2TelemetrySource
    {
        public Ra2TelemetryCapture Capture { get; } = capture;

        public async IAsyncEnumerable<RawObservation> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (RawObservation observation in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return observation;
        }
    }
}
