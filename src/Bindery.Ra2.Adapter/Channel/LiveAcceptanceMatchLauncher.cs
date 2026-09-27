// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Adapter.Channel;

/// <summary>Runs one live match: <see cref="LiveAcceptanceRunner.RunAsync(LiveAcceptanceRequest, LiveRunHooks?, CancellationToken)"/> or a stand-in.</summary>
public delegate Task<LiveAcceptanceEvidence> LiveMatchRun(
    LiveAcceptanceRequest request,
    LiveRunHooks hooks,
    CancellationToken cancellationToken);

/// <summary>
/// What a channel match reads from the instrumented stream, beyond the
/// launch itself. Both are optional; a match without them is the plain
/// private match path.
/// </summary>
/// <param name="Telemetry">The ra2yrcpp source for this match, if one is attached.</param>
/// <param name="AgentSeat">
/// The controller for <see cref="ChannelMatchContext.AgentSeat"/>. Required
/// when the channel assigns an agent seat, and needs <paramref name="Telemetry"/>.
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
/// agent seat, if any, plays from the same stream. The agent's player client
/// declares its controller at enrollment, and its decision trace is uploaded
/// into that client's capture once the match is over. Each match needs fresh
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
        AgentSeatAssignment? assignment = context.AgentSeat;
        if (assignment is not null) request = DeclareController(request, assignment);

        IRa2TelemetrySource? telemetry = options.Telemetry?.Invoke(context);
        AgentSeat? seat = assignment is null ? null : options.AgentSeat?.Invoke(context);
        if (assignment is not null)
        {
            if (seat is null) throw new InvalidOperationException($"the channel assigns agent house {assignment.House} but no agent seat was provided");
            if (!string.Equals(seat.House, assignment.House, StringComparison.Ordinal)) throw new InvalidOperationException("the agent seat is bound to a different house than the channel assigns");
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

        // Closing the stream happens once, whichever comes first: the runner
        // asking for artifacts after the clients exit, or the run ending.
        Task? closing = null;
        Task CloseTelemetryAsync()
        {
            return closing ??= CloseAsync();

            async Task CloseAsync()
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
        }

        int cued = 0;
        LiveRunHooks hooks = new(
            OnLifecycle: async (notice, ct) =>
            {
                if (notice.Kind == LifecycleKind.Started
                    && string.Equals(notice.ClientInstanceId, context.Capture.ClientInstanceId, StringComparison.Ordinal)
                    && Interlocked.Exchange(ref cued, 1) == 0)
                    await onAir(ct).ConfigureAwait(false);
            },
            CollectArtifacts: assignment is null
                ? null
                : async ct =>
                {
                    await CloseTelemetryAsync().ConfigureAwait(false);
                    AgentSeatSummary? finished = await seatRun.ConfigureAwait(false);
                    return finished is null
                        ? []
                        : [new LiveArtifact(assignment.ClientInstanceId, CaptureMediaTypes.DecisionTrace, finished.TracePath)];
                });

        LiveAcceptanceEvidence evidence;
        try
        {
            evidence = await run(request, hooks, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await CloseTelemetryAsync().ConfigureAwait(false);
        }

        AgentSeatSummary? summary = await seatRun.ConfigureAwait(false);
        LiveArtifactEvidence? trace = evidence.Artifacts?.FirstOrDefault(static a => a.MediaType == CaptureMediaTypes.DecisionTrace);
        if (trace?.Failure is not null) telemetryIssues.Add($"decision trace not uploaded: {trace.Failure}");
        return new ChannelMatchResult(
            evidence,
            request.EvidenceDirectory,
            DecisionTracePath: summary?.TracePath,
            PlaybookRevisions: summary?.Revisions.Count ?? 0,
            Winner: tracker.Winner,
            TelemetryObserved: telemetry is null ? null : tracker.Observed,
            TelemetryEnded: telemetry is null ? null : tracker.Ended,
            TelemetryIssue: telemetryIssues.Count == 0 ? null : string.Join("; ", telemetryIssues),
            DecisionTraceContentHash: trace?.ContentHash);
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
        if (context.AgentSeat is { } agent
            && !clients.Any(c => string.Equals(c.Instance, agent.ClientInstanceId, StringComparison.Ordinal) && c.Class == ClientClass.Player))
            throw new InvalidOperationException($"agent seat client {agent.ClientInstanceId} is not a player of this match");
    }

    /// <summary>The agent's player client declares the agent; the other seat's declaration is left as the caller set it.</summary>
    internal static LiveAcceptanceRequest DeclareController(LiveAcceptanceRequest request, AgentSeatAssignment assignment)
    {
        if (string.Equals(request.First.ClientInstanceId, assignment.ClientInstanceId, StringComparison.Ordinal))
            return request with { First = request.First with { Controller = assignment.Controller } };
        if (string.Equals(request.Second.ClientInstanceId, assignment.ClientInstanceId, StringComparison.Ordinal))
            return request with { Second = request.Second with { Controller = assignment.Controller } };
        throw new InvalidOperationException($"agent seat client {assignment.ClientInstanceId} is not a player of this match");
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
