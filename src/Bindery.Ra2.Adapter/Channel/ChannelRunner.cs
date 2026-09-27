// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace Bindery.Ra2.Adapter.Channel;

/// <summary>What the launcher is asked to play.</summary>
public sealed record ChannelMatchContext(
    string ChannelId,
    int MatchIndex,
    string MapId,
    CaptureSource Capture,
    AgentSeatAssignment? AgentSeat);

/// <summary>
/// What the launcher brings back. <paramref name="Evidence"/> is the live
/// runner's own evidence packet; the channel derives its record from it and
/// never upgrades it.
/// </summary>
public sealed record ChannelMatchResult(
    LiveAcceptanceEvidence? Evidence,
    string? EvidencePath = null,
    string? ReplayPath = null,
    string? DecisionTracePath = null,
    int PlaybookRevisions = 0,
    string? Winner = null,
    long? TelemetryObserved = null,
    bool? TelemetryEnded = null,
    string? TelemetryIssue = null,
    string? DecisionTraceContentHash = null);

/// <summary>
/// Plays one match. The existing private RA2 path sits behind this: session,
/// placement, enrollment and the two clients are still Bindery's.
/// </summary>
public interface IChannelMatchLauncher
{
    /// <param name="onAir">
    /// Called once the match is launching, so the channel can cut to the
    /// capture scene. Its failure never stops the match.
    /// </param>
    Task<ChannelMatchResult> RunMatchAsync(ChannelMatchContext context, Func<CancellationToken, Task> onAir, CancellationToken cancellationToken);
}

public interface IChannelRecordSink
{
    Task WriteAsync(ChannelMatchRecord record, CancellationToken cancellationToken);
}

/// <summary>Appends one JSON line per match to <c>channel-matches.ndjson</c>.</summary>
public sealed class NdjsonChannelRecordSink(string directory) : IChannelRecordSink
{
    public const string FileName = "channel-matches.ndjson";

    private static readonly JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public string Path { get; } = System.IO.Path.Combine(directory ?? throw new ArgumentNullException(nameof(directory)), FileName);

    public async Task WriteAsync(ChannelMatchRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        await File.AppendAllTextAsync(Path, JsonSerializer.Serialize(record, options) + "\n", cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The on-demand channel: holding scene, match, record, next match -- until
/// the match budget is spent, a drain is requested, or matches keep failing.
/// </summary>
/// <remarks>
/// Broadcasting is a side effect of the loop, not a dependency of it. A scene
/// switch that fails is noted and the match plays on; with
/// <see cref="NoBroadcastProduction"/> the loop is the existing private match
/// path run back to back.
/// </remarks>
public sealed class ChannelRunner
{
    private readonly IChannelMatchLauncher launcher;
    private readonly IBroadcastProduction production;
    private readonly IChannelRecordSink records;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly IBroadcastHealth? health;
    private readonly List<ChannelPhase> phases = [];
    private readonly List<string> broadcastIssues = [];
    private int drainRequested;

    public ChannelRunner(
        IChannelMatchLauncher launcher,
        IBroadcastProduction production,
        IChannelRecordSink records,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        IBroadcastHealth? health = null)
    {
        this.health = health;
        this.launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        this.production = production ?? throw new ArgumentNullException(nameof(production));
        this.records = records ?? throw new ArgumentNullException(nameof(records));
        this.delay = delay ?? Task.Delay;
    }

    public ChannelPhase Phase { get; private set; } = ChannelPhase.Idle;

    /// <summary>Scene switches that failed; the matches still ran.</summary>
    public IReadOnlyList<string> BroadcastIssues => broadcastIssues;

    /// <summary>Let the current match finish, then stop. Safe from any thread.</summary>
    public void RequestDrain() => Interlocked.Exchange(ref drainRequested, 1);

    public async Task<ChannelSessionSummary> RunAsync(ChannelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (Phase != ChannelPhase.Idle) throw new InvalidOperationException("a channel runner plays one session");

        List<ChannelMatchRecord> matches = [];
        string stopReason = "match budget reached";
        try
        {
            await production.StartAsync(request.Broadcast, cancellationToken).ConfigureAwait(false);
            Enter(ChannelPhase.Holding);
            await TryBroadcastAsync(ct => production.ShowHoldingAsync("channel starting", ct), "holding before first match", cancellationToken).ConfigureAwait(false);

            int consecutiveFailures = 0;
            for (int index = 1; index <= request.MaximumMatches; index++)
            {
                if (Volatile.Read(ref drainRequested) == 1)
                {
                    stopReason = "drain requested";
                    break;
                }

                Enter(ChannelPhase.Starting);
                ChannelMatchContext context = new(request.ChannelId, index, request.MapId, request.Capture, request.AgentSeat);
                DateTimeOffset startedAt = DateTimeOffset.UtcNow;
                ChannelMatchRecord record;
                try
                {
                    ChannelMatchResult result = await launcher.RunMatchAsync(
                        context,
                        async ct =>
                        {
                            Enter(ChannelPhase.OnAir);
                            health?.BeginMatch();
                            await TryBroadcastAsync(c => production.ShowMatchAsync(request.Capture, c), $"match {index} on air", ct).ConfigureAwait(false);
                        },
                        cancellationToken).ConfigureAwait(false);
                    record = FromResult(request, context, startedAt, result);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    record = Failed(request, context, startedAt, exception);
                }

                Enter(ChannelPhase.Recording);
                if (health?.EndMatch() is { } broadcastIssue) record = record with { BroadcastIssue = broadcastIssue };
                await TryBroadcastAsync(ct => production.ShowHoldingAsync("match ended", ct), $"holding after match {index}", cancellationToken).ConfigureAwait(false);
                await records.WriteAsync(record, cancellationToken).ConfigureAwait(false);
                matches.Add(record);

                consecutiveFailures = record.Outcome == ChannelMatchOutcome.Failed ? consecutiveFailures + 1 : 0;
                if (consecutiveFailures >= ChannelRequest.MaximumConsecutiveFailures)
                {
                    stopReason = $"{consecutiveFailures} consecutive matches failed";
                    break;
                }
                if (index == request.MaximumMatches) break;
                if (Volatile.Read(ref drainRequested) == 1)
                {
                    stopReason = "drain requested";
                    break;
                }
                Enter(ChannelPhase.Holding);
                await delay(request.EffectiveHoldingDuration, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Enter(ChannelPhase.Draining);
            // Release the output even when the session is being cancelled.
            await TryBroadcastAsync(production.StopAsync, "stopping output", CancellationToken.None).ConfigureAwait(false);
            Enter(ChannelPhase.Stopped);
        }
        return new ChannelSessionSummary(request.ChannelId, matches, phases.ToArray(), stopReason);
    }

    private void Enter(ChannelPhase phase)
    {
        Phase = phase;
        phases.Add(phase);
    }

    private async Task TryBroadcastAsync(Func<CancellationToken, Task> action, string what, CancellationToken cancellationToken)
    {
        try
        {
            await action(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            broadcastIssues.Add($"{what}: {exception.Message}");
        }
    }

    internal static ChannelMatchRecord FromResult(ChannelRequest request, ChannelMatchContext context, DateTimeOffset startedAt, ChannelMatchResult result)
    {
        LiveAcceptanceEvidence? evidence = result.Evidence;
        // The match is the players'. An observer's failure, desync included,
        // degrades the view and is recorded apart from the outcome.
        LiveClientEvidence[] players = evidence?.Clients.Where(static c => c.ClientClass != "observer").ToArray() ?? [];
        LiveClientEvidence? observer = evidence?.Clients.FirstOrDefault(static c => c.ClientClass == "observer");
        string? failure = players.Select(static c => c.Failure).FirstOrDefault(static f => f is not null);
        bool desync = players.Any(static c => c.Observations?.Any(static o => o.Kind == RunObservation.Desync) == true);
        string? observerIssue = evidence?.ObserverDegraded == true
            ? observer?.Failure ?? "the observer did not depart cleanly"
            : null;
        ChannelMatchOutcome outcome = evidence is null
            ? ChannelMatchOutcome.Incomplete
            : evidence.Qualification.ControlPlaneLifecycleComplete && !desync && failure is null
                ? ChannelMatchOutcome.Completed
                : ChannelMatchOutcome.Incomplete;
        if (desync) failure ??= "the clients desynchronised";
        if (evidence is null) failure ??= "the launcher returned no evidence";
        return new ChannelMatchRecord(
            request.ChannelId,
            context.MatchIndex,
            evidence?.RunId,
            evidence?.SessionId,
            evidence?.MapId ?? context.MapId,
            evidence?.Seed,
            evidence?.GoldenApplianceId,
            evidence?.Clients.Select(static c => c.GameExecutableSha256).Distinct(StringComparer.Ordinal).ToArray() ?? [],
            Ra2LabProfile.AdapterId,
            Ra2LabProfile.AdapterVersion,
            evidence?.StartedAt ?? startedAt,
            evidence?.CompletedAt ?? DateTimeOffset.UtcNow,
            outcome,
            result.Winner,
            result.EvidencePath,
            result.ReplayPath,
            result.DecisionTracePath,
            result.PlaybookRevisions,
            request.Capture,
            request.Broadcast.Public is not null,
            failure,
            result.TelemetryObserved,
            result.TelemetryEnded,
            result.TelemetryIssue,
            observerIssue,
            result.DecisionTraceContentHash,
            context.AgentSeat?.Controller,
            context.AgentSeat?.House);
    }

    private static ChannelMatchRecord Failed(ChannelRequest request, ChannelMatchContext context, DateTimeOffset startedAt, Exception exception) => new(
        request.ChannelId,
        context.MatchIndex,
        null,
        null,
        context.MapId,
        null,
        null,
        [],
        Ra2LabProfile.AdapterId,
        Ra2LabProfile.AdapterVersion,
        startedAt,
        DateTimeOffset.UtcNow,
        ChannelMatchOutcome.Failed,
        null,
        null,
        null,
        null,
        0,
        request.Capture,
        request.Broadcast.Public is not null,
        $"{exception.GetType().Name}: {exception.Message}");
}
