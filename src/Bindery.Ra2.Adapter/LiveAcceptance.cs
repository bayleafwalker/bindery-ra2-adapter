// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Text.Json;

namespace Bindery.Ra2.Adapter;

public sealed record LiveClientLaunch(
    string GameExecutable,
    string WorkingDirectory,
    string MapId,
    string PlayerName,
    string SpawnMode = "multiplayer",
    string? SpawnerExecutable = null,
    IReadOnlyList<string>? SpawnerArguments = null,
    int Side = 0,
    int Color = 0,
    int SpawnLocation = -1,
    // Seat and colour default per client below; -1 means "not chosen".
    string SpawnerLogName = "syringe.log");

public sealed record LiveAcceptanceRequest(
    SessionCreationRequest Session,
    MatchClientDefinition First,
    MatchClientDefinition Second,
    LiveClientLaunch FirstLaunch,
    LiveClientLaunch SecondLaunch,
    string SessionIdempotencyKey,
    string FirstEnrollmentIdempotencyKey,
    string SecondEnrollmentIdempotencyKey,
    string EvidenceDirectory,
    string GoldenApplianceId,
    string TelemetryEndpoint,
    string TelemetryProtocol = Ra2LabProfile.TelemetryProtocol,
    Uri? TunnelV2Uri = null,
    SpawnGameOptions? GameOptions = null);

public sealed record LiveClientEvidence(
    string ClientId,
    string AccountId,
    string ClientInstanceId,
    string GoldenApplianceId,
    string GameExecutableSha256,
    string SpawnIniSha256,
    IReadOnlyList<string> Reports,
    int? ProcessExitCode,
    string? Failure);

public sealed record LiveRelayEvidence(
    string ProviderId,
    string AllocationId,
    string Endpoint,
    bool TrafficObserved,
    long? PacketsForwarded);

public sealed record LiveTelemetryEvidence(
    string Protocol,
    string Endpoint,
    bool RawEventsObserved,
    long? RawEventCount);

public sealed record LiveQualificationFlags(
    bool ControlPlaneLifecycleComplete,
    bool RelayTrafficObserved,
    bool KctlIntakeAuthorized,
    bool OracleReadsTraced,
    bool HumanAcceptanceRecorded,
    bool QualificationEligible);

public sealed record LiveAcceptanceEvidence(
    string SchemaVersion,
    string RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string SessionId,
    string GoldenApplianceId,
    LiveRelayEvidence Relay,
    LiveTelemetryEvidence Telemetry,
    IReadOnlyList<LiveClientEvidence> Clients,
    string FinalSessionPhase,
    IReadOnlyList<string> FinalEnrollmentPhases,
    LiveQualificationFlags Qualification,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Runs the real Windows process boundary after a two-client control-plane
/// match has been prepared. The generated evidence deliberately leaves relay
/// telemetry, Kctl authority, oracle tracing, and human acceptance as explicit
/// external inputs; this runner never upgrades global qualification itself.
/// </summary>
public sealed class LiveAcceptanceRunner
{
    public const string EvidenceSchemaVersion = Ra2LabProfile.LiveEvidenceSchemaVersion;

    private readonly ILiveMatchDriver matchDriver;
    private readonly BinderyAdapterClient controlPlane;
    private readonly ILiveClientHost firstHost;
    private readonly ILiveClientHost secondHost;

    /// <summary>Both clients on the machine running the orchestrator.</summary>
    public LiveAcceptanceRunner(ILiveMatchDriver matchDriver, BinderyAdapterClient controlPlane, ISpawnerBoundary spawner)
        : this(matchDriver, controlPlane, new LocalLiveClientHost(spawner), new LocalLiveClientHost(spawner))
    {
    }

    /// <summary>
    /// One host per client, so the two acceptance clients can be two separate
    /// cloned guests as docs/golden-appliance.md requires.
    /// </summary>
    public LiveAcceptanceRunner(ILiveMatchDriver matchDriver, BinderyAdapterClient controlPlane, ILiveClientHost firstHost, ILiveClientHost secondHost)
    {
        this.matchDriver = matchDriver ?? throw new ArgumentNullException(nameof(matchDriver));
        this.controlPlane = controlPlane ?? throw new ArgumentNullException(nameof(controlPlane));
        this.firstHost = firstHost ?? throw new ArgumentNullException(nameof(firstHost));
        this.secondHost = secondHost ?? throw new ArgumentNullException(nameof(secondHost));
    }

    public async Task<LiveAcceptanceEvidence> RunAsync(LiveAcceptanceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Only a client that runs on this machine needs this machine to be
        // Windows; a client driven through a launch agent runs on its own guest.
        if ((firstHost is LocalLiveClientHost || secondHost is LocalLiveClientHost) && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("live RA2/YR acceptance is Windows-only for locally hosted clients");
        ValidateLaunchArguments(request.FirstLaunch);
        ValidateLaunchArguments(request.SecondLaunch);
        // Two trees on one machine must not share a directory. Two separate
        // guests may legitimately use the same path, because the appliance is
        // cloned -- there the divergence is the machine, not the path.
        if (string.Equals(firstHost.Description, secondHost.Description, StringComparison.Ordinal)
            && string.Equals(request.FirstLaunch.WorkingDirectory.TrimEnd('\\', '/'), request.SecondLaunch.WorkingDirectory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("live clients sharing one host must use distinct runtime working directories");
        await firstHost.ValidateAsync(request.FirstLaunch, cancellationToken).ConfigureAwait(false);
        await secondHost.ValidateAsync(request.SecondLaunch, cancellationToken).ConfigureAwait(false);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.EvidenceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.GoldenApplianceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TelemetryEndpoint);
        Directory.CreateDirectory(request.EvidenceDirectory);

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        await using PreparedLiveMatch match = await matchDriver.PrepareLiveAsync(
            request.Session,
            request.First,
            request.Second,
            request.SessionIdempotencyKey,
            request.FirstEnrollmentIdempotencyKey,
            request.SecondEnrollmentIdempotencyKey,
            cancellationToken).ConfigureAwait(false);

        // One tunnel port per participant. The spawner reaches every peer as
        // 0.0.0.0 on its allocated port, so without these there is nothing to
        // write into the INI and no match can form.
        AdapterConfiguration relay = match.First.Configuration;
        Uri tunnelV2 = request.TunnelV2Uri ?? DefaultTunnelV2Uri(relay);
        IReadOnlyList<int> ports;
        using (HttpClient tunnelClient = new() { Timeout = TimeSpan.FromSeconds(15) })
        {
            ports = await TunnelPortAllocation.RequestAsync(tunnelClient, tunnelV2, 2, cancellationToken).ConfigureAwait(false);
        }

        int seed = Random.Shared.Next(1, int.MaxValue);
        // The spawner reads GameID as an integer, so the session UUID cannot be
        // passed through verbatim; both clients derive the same number from it.
        string gameId = StableGameId(match.Session.SessionId);
        // Distinct colours and distinct starting waypoints. Left at their
        // defaults both clients take colour 0 and the same cell, which is not
        // a playable match -- so the harness separates them rather than
        // relying on whoever wrote the settings to remember.
        int firstColor = request.FirstLaunch.Color;
        int secondColor = request.SecondLaunch.Color == firstColor ? firstColor + 1 : request.SecondLaunch.Color;
        int firstSeat = request.FirstLaunch.SpawnLocation >= 0 ? request.FirstLaunch.SpawnLocation : 0;
        int secondSeat = request.SecondLaunch.SpawnLocation >= 0 ? request.SecondLaunch.SpawnLocation : 1;
        if (firstSeat == secondSeat) throw new ArgumentException("the two clients cannot share a starting location");
        SpawnParticipant firstParticipant = new(request.FirstLaunch.PlayerName, ports[0], request.FirstLaunch.Side, firstColor, firstSeat);
        SpawnParticipant secondParticipant = new(request.SecondLaunch.PlayerName, ports[1], request.SecondLaunch.Side, secondColor, secondSeat);
        // The orchestrator's own client hosts: it is the machine that already
        // owns session creation, so hosting there needs no extra coordination.
        // Global player order: identical on both clients, first client first.
        SpawnParticipant[] globalOrder = [firstParticipant, secondParticipant];
        // The spawner reads the scenario from a copy of the map named
        // spawnmap.ini, which is what the real client writes. Pointing
        // Scenario at the .map file left the engine unable to read the map's
        // waypoints, so every player was seated on the same cell.
        const string scenario = "spawnmap.ini";
        SpawnMatchPlan firstPlan = new(scenario, gameId, seed, true, firstParticipant, [secondParticipant], globalOrder, relay.RelayHost!, relay.RelayPort!.Value, request.GameOptions);
        SpawnMatchPlan secondPlan = new(scenario, gameId, seed, false, secondParticipant, [firstParticipant], globalOrder, relay.RelayHost!, relay.RelayPort!.Value, request.GameOptions);

        string firstIni = await WriteSpawnIniAsync(request.EvidenceDirectory, "client-a", firstPlan, cancellationToken).ConfigureAwait(false);
        string secondIni = await WriteSpawnIniAsync(request.EvidenceDirectory, "client-b", secondPlan, cancellationToken).ConfigureAwait(false);
        Task<LiveClientRun> firstRun = RunClientAsync(firstHost, match.First, request.FirstLaunch, firstIni, request.GoldenApplianceId, cancellationToken);
        Task<LiveClientRun> secondRun = RunClientAsync(secondHost, match.Second, request.SecondLaunch, secondIni, request.GoldenApplianceId, cancellationToken);
        LiveClientRun[] runs = await Task.WhenAll(firstRun, secondRun).ConfigureAwait(false);

        SessionStatus finalSession = await controlPlane.GetSessionAsync(match.Session.SessionId, cancellationToken).ConfigureAwait(false);
        EnrollmentStatus finalFirst = await controlPlane.GetEnrollmentAsync(match.First.Enrollment.ClientId, cancellationToken).ConfigureAwait(false);
        EnrollmentStatus finalSecond = await controlPlane.GetEnrollmentAsync(match.Second.Enrollment.ClientId, cancellationToken).ConfigureAwait(false);
        // A Failed report must veto completeness even when the exit code is 0:
        // Syringe is a debugger and exits 0 after the game it hosted crashes.
        bool lifecycleComplete = runs.All(static run => run.ProcessExitCode == 0
                && run.Reports.Contains(LifecycleKind.Ready)
                && run.Reports.Contains(LifecycleKind.Started)
                && run.Reports.Contains(LifecycleKind.Exited)
                && !run.Reports.Contains(LifecycleKind.Failed))
            && finalSession.Phase == SessionPhase.Ended
            && finalFirst.Phase == EnrollmentPhase.Departed
            && finalSecond.Phase == EnrollmentPhase.Departed;

        LiveAcceptanceEvidence evidence = new(
            EvidenceSchemaVersion,
            Guid.NewGuid().ToString("D"),
            startedAt,
            DateTimeOffset.UtcNow,
            match.Session.SessionId,
            request.GoldenApplianceId,
            new LiveRelayEvidence(
                match.Session.Placement!.RelayProviderId,
                match.Session.Placement.RelayAllocationId,
                match.Session.Placement.RelayEndpoint,
                false,
                null),
            new LiveTelemetryEvidence(request.TelemetryProtocol, request.TelemetryEndpoint, false, null),
            runs.Select(static run => run.Evidence).ToArray(),
            finalSession.Phase.ToString().ToLowerInvariant(),
            [finalFirst.Phase.ToString().ToLowerInvariant(), finalSecond.Phase.ToString().ToLowerInvariant()],
            new LiveQualificationFlags(lifecycleComplete, false, false, false, false, false),
            [
                "relay traffic observation must be supplied from the relay/control-plane telemetry path",
                "spawner crash detection reads the debugger log; a spawner that logs nothing cannot be checked this way",
                "Kctl knowledge.candidate.intake authority must be verified by the served identity",
                "oracle reads must be traced and attached to the qualification packet",
                "human acceptance is required before global qualification"
            ]);
        await LiveAcceptanceEvidenceWriter.WriteAsync(request.EvidenceDirectory, evidence, cancellationToken).ConfigureAwait(false);
        return evidence;
    }

    private async Task<LiveClientRun> RunClientAsync(ILiveClientHost host, PreparedLiveClient client, LiveClientLaunch launch, string spawnIniPath, string goldenApplianceId, CancellationToken cancellationToken)
    {
        ConcurrentQueue<LifecycleKind> reports = new();
        bool spawnIniInstalled = false;
        try
        {
            await host.InstallSpawnIniAsync(
                launch.WorkingDirectory,
                await File.ReadAllTextAsync(spawnIniPath, cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
            spawnIniInstalled = true;
            // Clear the spawner's log first: a crash marker left by an earlier
            // run would otherwise be read as this run's outcome.
            await host.ResetSpawnerLogAsync(launch.WorkingDirectory, launch.SpawnerLogName, cancellationToken).ConfigureAwait(false);
            SpawnConfiguration spawn = new(
                launch.GameExecutable,
                launch.MapId,
                launch.PlayerName,
                client.Configuration.RelayHost,
                client.Configuration.RelayPort,
                launch.SpawnMode,
                launch.SpawnerExecutable,
                launch.SpawnerArguments);
            int exitCode = await host.RunAsync(
                spawn,
                launch,
                host.ResolveSpawnIniPath(launch.WorkingDirectory, spawnIniPath),
                async report =>
                {
                    reports.Enqueue(report.Kind);
                    await controlPlane.ReportAsync(client.Configuration, report, cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
            string gameHash = await host.Sha256Async(launch.GameExecutable, cancellationToken).ConfigureAwait(false);

            // Syringe exits 0 even when the game it hosted threw, so the exit
            // code alone cannot say whether the client ran. Ask its log.
            string? spawnerLog = await host.ReadSpawnerLogAsync(launch.WorkingDirectory, launch.SpawnerLogName, cancellationToken).ConfigureAwait(false);
            string? crash = SpawnerCrashDetection.FindCrash(spawnerLog);
            if (crash is not null)
            {
                LifecycleReport crashReport = new(Guid.NewGuid().ToString(), LifecycleKind.Failed, crash);
                reports.Enqueue(LifecycleKind.Failed);
                await controlPlane.ReportAsync(client.Configuration, crashReport, cancellationToken).ConfigureAwait(false);
                return new LiveClientRun(ToEvidence(client, launch, goldenApplianceId, gameHash, spawnIniPath, reports, exitCode, crash), reports, exitCode);
            }
            return new LiveClientRun(ToEvidence(client, launch, goldenApplianceId, gameHash, spawnIniPath, reports, exitCode, null), reports, exitCode);
        }
        catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException or IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException)
        {
            // A failed client still produces evidence, but its executable hash
            // may be unobtainable if the host itself is what failed.
            string gameHash;
            try { gameHash = await host.Sha256Async(launch.GameExecutable, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception hashException) when (hashException is InvalidOperationException or IOException or UnauthorizedAccessException or HttpRequestException) { gameHash = string.Empty; }
            return new LiveClientRun(ToEvidence(client, launch, goldenApplianceId, gameHash, spawnIniPath, reports, null, exception.GetType().Name), reports, null);
        }
        finally
        {
            if (spawnIniInstalled)
            {
                try { await host.RestoreSpawnIniAsync(launch.WorkingDirectory, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException)
                {
                    // Restoring the operator's previous SPAWN.INI is best effort;
                    // failing here must not mask the run's own outcome.
                }
            }
        }
    }

    private static LiveClientEvidence ToEvidence(PreparedLiveClient client, LiveClientLaunch launch, string goldenApplianceId, string gameExecutableSha256, string spawnIniPath, IEnumerable<LifecycleKind> reports, int? exitCode, string? failure) => new(
        client.Enrollment.ClientId,
        client.Definition.Identity.AccountId,
        client.Definition.ClientInstanceId,
        goldenApplianceId,
        gameExecutableSha256,
        Hashing.Sha256File(spawnIniPath),
        reports.Distinct().Select(LifecycleKindName).ToArray(),
        exitCode,
        failure);

    private static string LifecycleKindName(LifecycleKind kind) => kind switch
    {
        LifecycleKind.Ready => "ready",
        LifecycleKind.Started => "started",
        LifecycleKind.Exited => "exited",
        LifecycleKind.Failed => "failed",
        LifecycleKind.CaptureDegraded => "capture_degraded",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static async Task<string> WriteSpawnIniAsync(string directory, string name, SpawnMatchPlan plan, CancellationToken cancellationToken)
    {
        string path = Path.Combine(directory, $"spawn-{name}.ini");
        await File.WriteAllTextAsync(path, SpawnIniRenderer.Render(plan), cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static string StableGameId(string sessionId)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sessionId));
        int value = BitConverter.ToInt32(hash, 0) & 0x7FFFFFFF;
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // The V2 tunnel serves its port-allocation web endpoint and its UDP relay
    // on the same port, so the placed endpoint is both. The spawner speaks V2:
    // pointing [Tunnel] at the V3 listener instead makes every client talk a
    // protocol nothing there answers, which looks exactly like two players who
    // cannot see each other.
    private static Uri DefaultTunnelV2Uri(AdapterConfiguration relay)
    {
        if (string.IsNullOrWhiteSpace(relay.RelayHost) || relay.RelayPort is null)
            throw new InvalidOperationException("the placement did not carry a relay endpoint");
        return new Uri($"http://{relay.RelayHost}:{relay.RelayPort.Value}");
    }

    // Argument shape only. Whether the paths exist is the owning host's
    // question, because they may not be on this machine.
    private static void ValidateLaunchArguments(LiveClientLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentException.ThrowIfNullOrWhiteSpace(launch.GameExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(launch.WorkingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(launch.MapId);
        ArgumentException.ThrowIfNullOrWhiteSpace(launch.PlayerName);
    }

    private sealed record LiveClientRun(LiveClientEvidence Evidence, ConcurrentQueue<LifecycleKind> Reports, int? ProcessExitCode);
}

public static class LiveAcceptanceEvidenceWriter
{
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    public static async Task<string> WriteAsync(string directory, LiveAcceptanceEvidence evidence, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(evidence);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "live-acceptance-evidence.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence, json), cancellationToken).ConfigureAwait(false);
        return path;
    }
}
