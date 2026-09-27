// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Adapter.Channel;

/// <summary>Runs one live match: <see cref="LiveAcceptanceRunner.RunAsync(LiveAcceptanceRequest, Func{LiveLifecycleNotice, CancellationToken, Task}?, CancellationToken)"/> or a stand-in.</summary>
public delegate Task<LiveAcceptanceEvidence> LiveMatchRun(
    LiveAcceptanceRequest request,
    Func<LiveLifecycleNotice, CancellationToken, Task> onLifecycle,
    CancellationToken cancellationToken);

/// <summary>
/// What a channel match reads from the instrumented stream, beyond the
/// launch itself. Both are optional; a match without them is the plain
/// private match path.
/// </summary>
/// <param name="Telemetry">The ra2yrcpp source for this match, if one is attached.</param>
/// <param name="AgentSeat">
/// The controller for <see cref="ChannelMatchContext.AgentSeatHouse"/>. Required
/// when the channel names an agent house, and needs <paramref name="Telemetry"/>.
/// </param>
public sealed record LiveChannelMatchOptions(
    Func<ChannelMatchContext, IRa2TelemetrySource?>? Telemetry = null,
    Func<ChannelMatchContext, AgentSeat?>? AgentSeat = null,
    TimeSpan? TelemetryDrain = null)
{
    /// <summary>How long to keep reading after the clients exit, for the final events to arrive.</summary>
    public TimeSpan EffectiveTelemetryDrain => TelemetryDrain ?? TimeSpan.FromSeconds(5);
}

/// <summary>
/// Plays each channel match through the existing <see cref="LiveAcceptanceRunner"/>.
/// </summary>
/// <remarks>
/// The cut to the match scene happens when the captured client reports
/// <c>started</c>, so viewers see the holding scene through session setup
/// and loading. With telemetry attached, the tracker names the winner and an
/// agent seat, if any, plays from the same stream. Each match needs fresh
/// idempotency keys, which is why the request comes from a factory.
/// </remarks>
public sealed class LiveAcceptanceMatchLauncher : IChannelMatchLauncher
{
    private readonly LiveMatchRun run;
    private readonly Func<ChannelMatchContext, LiveAcceptanceRequest> requestFor;
    private readonly LiveChannelMatchOptions options;

    public LiveAcceptanceMatchLauncher(
        LiveAcceptanceRunner runner,
        Func<ChannelMatchContext, LiveAcceptanceRequest> requestFor,
        LiveChannelMatchOptions? options = null)
        : this((runner ?? throw new ArgumentNullException(nameof(runner))).RunAsync, requestFor, options)
    {
    }

    public LiveAcceptanceMatchLauncher(
        LiveMatchRun run,
        Func<ChannelMatchContext, LiveAcceptanceRequest> requestFor,
        LiveChannelMatchOptions? options = null)
    {
        this.run = run ?? throw new ArgumentNullException(nameof(run));
        this.requestFor = requestFor ?? throw new ArgumentNullException(nameof(requestFor));
        this.options = options ?? new LiveChannelMatchOptions();
    }

    public async Task<ChannelMatchResult> RunMatchAsync(ChannelMatchContext context, Func<CancellationToken, Task> onAir, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(onAir);
        LiveAcceptanceRequest request = requestFor(context) ?? throw new InvalidOperationException("the request factory returned nothing");
        Validate(context, request);

        IRa2TelemetrySource? telemetry = options.Telemetry?.Invoke(context);
        AgentSeat? seat = context.AgentSeatHouse is null ? null : options.AgentSeat?.Invoke(context);
        if (context.AgentSeatHouse is not null)
        {
            if (seat is null) throw new InvalidOperationException($"the channel names agent house {context.AgentSeatHouse} but no agent seat was provided");
            if (!string.Equals(seat.House, context.AgentSeatHouse, StringComparison.Ordinal)) throw new InvalidOperationException("the agent seat is bound to a different house than the channel names");
            if (telemetry is null) throw new InvalidOperationException("an agent seat needs a telemetry source to observe");
        }

        MatchTelemetryTracker tracker = new();
        List<string> telemetryIssues = [];
        using CancellationTokenSource telemetryStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task pump = Task.CompletedTask;
        Task tracking = Task.CompletedTask;
        Task<AgentSeatSummary?> seatRun = Task.FromResult<AgentSeatSummary?>(null);
        if (telemetry is not null)
        {
            TelemetryFanOut fanOut = new(telemetry);
            IRa2TelemetrySource trackerBranch = fanOut.Branch();
            IRa2TelemetrySource? seatBranch = seat is null ? null : fanOut.Branch();
            tracking = TrackAsync(trackerBranch, tracker);
            if (seat is not null) seatRun = RunSeatAsync(seat, seatBranch!, request.EvidenceDirectory, telemetryIssues, cancellationToken);
            pump = fanOut.RunAsync(telemetryStop.Token);
        }

        int cued = 0;
        LiveAcceptanceEvidence evidence;
        try
        {
            evidence = await run(
                request,
                async (notice, ct) =>
                {
                    if (notice.Kind == LifecycleKind.Started
                        && string.Equals(notice.ClientInstanceId, context.Capture.ClientInstanceId, StringComparison.Ordinal)
                        && Interlocked.Exchange(ref cued, 1) == 0)
                        await onAir(ct).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Give the last events -- defeat, match end -- time to arrive,
            // then close the stream so the tracker and seat finish cleanly.
            if (telemetry is not null && !tracker.Ended && !cancellationToken.IsCancellationRequested)
            {
                try { await Task.WhenAny(tracking, Task.Delay(options.EffectiveTelemetryDrain, cancellationToken)).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            telemetryStop.Cancel();
            await QuietlyAsync(pump, "telemetry source", telemetryIssues).ConfigureAwait(false);
            await QuietlyAsync(tracking, "match tracker", telemetryIssues).ConfigureAwait(false);
        }

        AgentSeatSummary? summary = await seatRun.ConfigureAwait(false);
        return new ChannelMatchResult(
            evidence,
            request.EvidenceDirectory,
            DecisionTracePath: summary?.TracePath,
            PlaybookRevisions: summary?.Revisions.Count ?? 0,
            Winner: tracker.Winner,
            TelemetryObserved: telemetry is null ? null : tracker.Observed,
            TelemetryEnded: telemetry is null ? null : tracker.Ended,
            TelemetryIssue: telemetryIssues.Count == 0 ? null : string.Join("; ", telemetryIssues));
    }

    internal static void Validate(ChannelMatchContext context, LiveAcceptanceRequest request)
    {
        if (!string.Equals(request.FirstLaunch.MapId, context.MapId, StringComparison.Ordinal)
            || !string.Equals(request.SecondLaunch.MapId, context.MapId, StringComparison.Ordinal))
            throw new InvalidOperationException("the live request must play the channel's map on both clients");
        // A capture of a client that is not in the match would never go on air.
        (string Instance, ClientClass Class)[] clients =
        [
            (request.First.ClientInstanceId, request.First.ClientClass),
            (request.Second.ClientInstanceId, request.Second.ClientClass),
            .. request.Observer is null ? [] : new[] { (request.Observer.Definition.ClientInstanceId, request.Observer.Definition.ClientClass) },
        ];
        if (!clients.Any(c => string.Equals(c.Instance, context.Capture.ClientInstanceId, StringComparison.Ordinal) && c.Class == context.Capture.ClientClass))
            throw new InvalidOperationException($"capture client {context.Capture.ClientInstanceId} ({context.Capture.ClientClass}) is not a client of this match");
    }

    private static async Task TrackAsync(IRa2TelemetrySource source, MatchTelemetryTracker tracker)
    {
        await foreach (RawObservation observation in source.ReadAsync().ConfigureAwait(false))
        {
            tracker.Observe(observation);
            if (tracker.Ended) return;
        }
    }

    // The instrumented stream is evidence about the match, not the match: a
    // source, tracker or controller that fails is recorded against the match
    // and never turns a played match into a failed one.
    private static async Task<AgentSeatSummary?> RunSeatAsync(AgentSeat seat, IRa2TelemetrySource source, string directory, List<string> issues, CancellationToken cancellationToken)
    {
        try
        {
            return await seat.RunAsync(source, directory, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            lock (issues) issues.Add($"agent seat: {exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    private static async Task QuietlyAsync(Task task, string what, List<string> issues)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Closing the stream is expected.
        }
        catch (Exception exception)
        {
            lock (issues) issues.Add($"{what}: {exception.GetType().Name}: {exception.Message}");
        }
    }
}
