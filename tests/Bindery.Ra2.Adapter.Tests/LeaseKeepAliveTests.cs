// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Bindery.Ra2.Adapter.Tests;

/// <summary>
/// The control plane expires an enrollment two minutes after its last
/// heartbeat. A live match that outlasts that lease must still end with both
/// enrollments departed, so the runner has to heartbeat every enrolled client
/// from enrollment until its terminal report.
/// </summary>
public sealed class LeaseKeepAliveTests
{
    private const string SessionId = "00000000-0000-4000-8000-000000000001";
    private const string ClientA = "00000000-0000-4000-8000-00000000000a";
    private const string ClientB = "00000000-0000-4000-8000-00000000000b";
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task A_match_longer_than_one_lease_keeps_both_enrollments_alive()
    {
        using LiveFixture fixture = new(matchLength: TimeSpan.FromMinutes(5));

        LiveAcceptanceEvidence evidence = await fixture.RunAsync();

        Assert.True(evidence.Qualification.ControlPlaneLifecycleComplete, string.Join(" | ", evidence.Clients.Select(static c => c.Failure)));
        Assert.Equal("ended", evidence.FinalSessionPhase);
        Assert.Equal(["departed", "departed"], evidence.FinalEnrollmentPhases);
        Assert.Empty(fixture.ControlPlane.Rejections);
        foreach (LiveClientEvidence client in evidence.Clients)
        {
            // Five minutes of match at a third of a two-minute lease.
            Assert.True(client.HeartbeatCount >= 5, $"{client.ClientId} sent {client.HeartbeatCount} heartbeats");
            Assert.Null(client.LastHeartbeatError);
        }
    }

    [Fact]
    public async Task Heartbeats_stop_once_the_terminal_report_is_sent()
    {
        using LiveFixture fixture = new(matchLength: TimeSpan.FromMinutes(3));

        await fixture.RunAsync();
        int afterRun = fixture.ControlPlane.Heartbeats(ClientA) + fixture.ControlPlane.Heartbeats(ClientB);
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(50);

        Assert.True(afterRun > 0);
        Assert.Equal(afterRun, fixture.ControlPlane.Heartbeats(ClientA) + fixture.ControlPlane.Heartbeats(ClientB));
        foreach (string client in new[] { ClientA, ClientB })
        {
            IReadOnlyList<string> log = fixture.ControlPlane.Log(client);
            int exited = log.ToList().IndexOf("report:exited");
            Assert.True(exited >= 0, string.Join(",", log));
            Assert.DoesNotContain("heartbeat", log.Skip(exited + 1));
        }
    }

    [Fact]
    public async Task A_failing_heartbeat_is_recorded_in_evidence_not_thrown()
    {
        using LiveFixture fixture = new(matchLength: TimeSpan.FromSeconds(90));
        fixture.ControlPlane.FailHeartbeats(ClientB);

        LiveAcceptanceEvidence evidence = await fixture.RunAsync();

        // Inside one lease the failures cost nothing: the match still completes.
        Assert.True(evidence.Qualification.ControlPlaneLifecycleComplete, string.Join(" | ", evidence.Clients.Select(static c => c.Failure)));
        LiveClientEvidence b = evidence.Clients.Single(static c => c.ClientId == ClientB);
        Assert.Equal(0, b.HeartbeatCount);
        Assert.Contains("503", b.LastHeartbeatError, StringComparison.Ordinal);
        Assert.True(fixture.ControlPlane.Log(ClientB).Count(static e => e == "heartbeat_rejected") >= 2);
        LiveClientEvidence a = evidence.Clients.Single(static c => c.ClientId == ClientA);
        Assert.True(a.HeartbeatCount >= 2);
        Assert.Null(a.LastHeartbeatError);
    }

    [Fact]
    public async Task Heartbeats_failing_past_the_lease_leave_the_existing_lost_handling_in_place()
    {
        using LiveFixture fixture = new(matchLength: TimeSpan.FromMinutes(4));
        fixture.ControlPlane.FailHeartbeats(ClientB);

        LiveAcceptanceEvidence evidence = await fixture.RunAsync();

        Assert.False(evidence.Qualification.ControlPlaneLifecycleComplete);
        Assert.Equal(["departed", "lost"], evidence.FinalEnrollmentPhases);
        LiveClientEvidence b = evidence.Clients.Single(static c => c.ClientId == ClientB);
        Assert.NotNull(b.LastHeartbeatError);
        Assert.Contains("410", b.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void The_heartbeat_interval_is_a_third_of_the_remaining_lease_with_a_floor_and_a_ceiling()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), EnrollmentLeaseKeeper.NextInterval(TimeSpan.FromMinutes(1)));
        // The remaining lease is the server's expires_at against this machine's clock; a guest clock running
        // behind would overstate it, so the interval never exceeds the ceiling.
        Assert.Equal(EnrollmentLeaseKeeper.MaximumInterval, EnrollmentLeaseKeeper.NextInterval(TimeSpan.FromMinutes(2)));
        Assert.Equal(EnrollmentLeaseKeeper.MaximumInterval, EnrollmentLeaseKeeper.NextInterval(TimeSpan.FromHours(1)));
        Assert.Equal(EnrollmentLeaseKeeper.MinimumInterval, EnrollmentLeaseKeeper.NextInterval(TimeSpan.FromSeconds(3)));
        Assert.Equal(EnrollmentLeaseKeeper.MinimumInterval, EnrollmentLeaseKeeper.NextInterval(TimeSpan.FromSeconds(-30)));
    }

    /// <summary>A runner wired to a fake control plane, a manual clock and two scripted hosts.</summary>
    private sealed class LiveFixture : IDisposable
    {
        private readonly TimeSpan matchLength;
        private readonly HttpClient http;
        private readonly TunnelStub tunnel = new();
        private readonly string directory = Directory.CreateTempSubdirectory("lease-keepalive-").FullName;
        private readonly TaskCompletionSource matchOver = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LiveFixture(TimeSpan matchLength)
        {
            this.matchLength = matchLength;
            ControlPlane = new FakeControlPlane(Clock);
            http = new HttpClient(ControlPlane) { BaseAddress = new Uri("https://control-plane.test") };
        }

        public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 9, 27, 20, 22, 40, TimeSpan.Zero));

        public FakeControlPlane ControlPlane { get; }

        public Task<LiveAcceptanceEvidence> RunAsync()
        {
            BinderyAdapterClient client = new(http);
            LiveAcceptanceRunner runner = new(
                new FixedDriver(),
                client,
                new ScriptedHost("host-a", async () =>
                {
                    // The first host plays the match and owns the clock. Each
                    // step waits for both clients' heartbeat timers to be armed,
                    // so fake time never outruns the runner; if none ever arm,
                    // the clock just runs on.
                    TimeSpan step = TimeSpan.FromSeconds(10);
                    bool keepersSeen = true;
                    for (TimeSpan played = TimeSpan.Zero; played < matchLength; played += step)
                    {
                        if (keepersSeen) keepersSeen = await Clock.WaitForTimersAsync(2);
                        Clock.Advance(step);
                    }
                    if (keepersSeen) await Clock.WaitForTimersAsync(2);
                    matchOver.TrySetResult();
                }),
                new ScriptedHost("host-b", () => matchOver.Task),
                null,
                Clock);
            return runner.RunAsync(Request());
        }

        private LiveAcceptanceRequest Request() => new(
            new SessionCreationRequest(
                new SessionCompatibility(Ra2LabProfile.GameFamily, Ra2LabProfile.GameVersion, "sha256:game", Ra2LabProfile.AdapterId, Ra2LabProfile.AdapterVersion, Ra2LabProfile.ModId, "sha256:mod", "MAP01.MAP", "sha256:map"),
                new ParticipantPolicy(2, 2, 0),
                new PlacementIntent(["eu-north"], 100),
                new CapturePolicy(true, true, false)),
            Definition("account-a", "instance-a"),
            Definition("account-b", "instance-b"),
            new LiveClientLaunch("C:/a/gamemd.exe", "C:/a", "MAP01.MAP", "Americans"),
            new LiveClientLaunch("C:/b/gamemd.exe", "C:/b", "MAP01.MAP", "Soviets"),
            "session-key",
            "enroll-a",
            "enroll-b",
            directory,
            "golden-1",
            "127.0.0.1:14521",
            TunnelV2Uri: tunnel.Uri);

        public void Dispose()
        {
            http.Dispose();
            tunnel.Dispose();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    private static MatchClientDefinition Definition(string account, string instance) => new(
        new IdentityCredentials(account, "token-" + account),
        instance,
        ClientClass.Player,
        new AdapterIdentity(Ra2LabProfile.AdapterId, Ra2LabProfile.AdapterVersion),
        new CompatibilityHashes("sha256:game", "sha256:mod", "sha256:map"));

    private static PreparedLiveClient Prepared(MatchClientDefinition definition, string clientId, RelayPlacement placement) => new(
        definition,
        new EnrollmentCredentials(clientId, "lease-" + clientId, "transport"),
        new AdapterConfiguration(
            new Uri("https://control-plane.test"),
            definition.Identity.AccountToken,
            "join",
            SessionId,
            definition.ClientInstanceId,
            clientId,
            "lease-" + clientId,
            ClientClass.Player,
            definition.Adapter,
            definition.Compatibility,
            RelayProvider.CncNetPrivate,
            "127.0.0.1",
            50000,
            null),
        placement);

    private sealed class FixedDriver : ILiveMatchDriver
    {
        private static readonly RelayPlacement placement = new("eu-north", "cncnet-private", "allocation", "127.0.0.1:50000", "relay-placement/v1");

        public Task<PreparedLiveMatch> PrepareLiveAsync(SessionCreationRequest sessionRequest, MatchClientDefinition first, MatchClientDefinition second, string sessionIdempotencyKey, string firstEnrollmentIdempotencyKey, string secondEnrollmentIdempotencyKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PreparedLiveMatch(
                new SessionCredentials(SessionId, "join", placement),
                Prepared(first, ClientA, placement),
                Prepared(second, ClientB, placement),
                new NoOwner()));

        public Task<PreparedLiveClient> EnrollObserverAsync(SessionCredentials session, MatchClientDefinition observer, string enrollmentIdempotencyKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoOwner : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Reports ready and started, plays <paramref name="play"/>, then reports exited.</summary>
    private sealed class ScriptedHost(string description, Func<Task> play) : ILiveClientHost
    {
        public string Description => description;

        public Task ValidateAsync(LiveClientLaunch launch, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> Sha256Async(string path, CancellationToken cancellationToken) => Task.FromResult("sha256:game");

        public Task InstallSpawnIniAsync(string workingDirectory, string content, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RestoreSpawnIniAsync(string workingDirectory, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResetSpawnerLogAsync(string workingDirectory, string logName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string?> ReadSpawnerLogAsync(string workingDirectory, string logName, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public string ResolveSpawnIniPath(string workingDirectory, string localSpawnIniPath) => localSpawnIniPath;

        public async Task<int> RunAsync(SpawnConfiguration spawn, LiveClientLaunch launch, string spawnIniPath, Func<LifecycleReport, Task> report, CancellationToken cancellationToken)
        {
            await report(new LifecycleReport(Guid.NewGuid().ToString(), LifecycleKind.Ready));
            await report(new LifecycleReport(Guid.NewGuid().ToString(), LifecycleKind.Started));
            await play();
            await report(new LifecycleReport(Guid.NewGuid().ToString(), LifecycleKind.Exited));
            return 0;
        }
    }

    /// <summary>
    /// The lease rules of bindery-core's external runtime: a lease lasts two
    /// minutes from enrollment or the last heartbeat, and a client whose lease
    /// ran out is lost and answered 410 Gone.
    /// </summary>
    internal sealed class FakeControlPlane(ManualClock clock) : HttpMessageHandler
    {
        private readonly object gate = new();
        private readonly Dictionary<string, DateTimeOffset> expires = new()
        {
            [ClientA] = clock.GetUtcNow() + Lease,
            [ClientB] = clock.GetUtcNow() + Lease,
        };
        private readonly Dictionary<string, string> phases = new() { [ClientA] = "active", [ClientB] = "active" };
        private readonly Dictionary<string, List<string>> logs = new() { [ClientA] = [], [ClientB] = [] };
        private readonly HashSet<string> failing = [];

        public List<string> Rejections { get; } = [];

        public void FailHeartbeats(string clientId)
        {
            lock (gate) failing.Add(clientId);
        }

        public int Heartbeats(string clientId)
        {
            lock (gate) return logs[clientId].Count(static e => e == "heartbeat");
        }

        public IReadOnlyList<string> Log(string clientId)
        {
            lock (gate) return logs[clientId].ToArray();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (gate)
            {
                DateTimeOffset now = clock.GetUtcNow();
                foreach (string id in expires.Keys.ToArray())
                    if (phases[id] == "active" && now > expires[id]) phases[id] = "lost";

                if (request.Method == HttpMethod.Post && path.EndsWith(":heartbeat", StringComparison.Ordinal))
                {
                    string id = path["/v1/enrollments/".Length..^":heartbeat".Length];
                    if (failing.Contains(id))
                    {
                        logs[id].Add("heartbeat_rejected");
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    }
                    logs[id].Add("heartbeat");
                    if (phases[id] != "active") return Reject(id, "heartbeat");
                    expires[id] = now + Lease;
                    return Json(new { client_id = id, expires_at = expires[id] });
                }
                if (request.Method == HttpMethod.Post && path.EndsWith("/reports", StringComparison.Ordinal))
                {
                    string id = path["/v1/enrollments/".Length..^"/reports".Length];
                    string kind = JsonDocument.Parse(body).RootElement.GetProperty("kind").GetString()!;
                    logs[id].Add("report:" + kind);
                    if (phases[id] != "active") return Reject(id, "report:" + kind);
                    if (kind == "exited") phases[id] = "departed";
                    return Json(new { public_session = Session(), public_enrollment = Enrollment(id) });
                }
                if (request.Method == HttpMethod.Get && path == $"/v1/sessions/{SessionId}") return Json(Session());
                if (request.Method == HttpMethod.Get && path.StartsWith("/v1/enrollments/", StringComparison.Ordinal))
                    return Json(Enrollment(path["/v1/enrollments/".Length..]));
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private HttpResponseMessage Reject(string id, string what)
        {
            Rejections.Add($"{id} {what}");
            return new HttpResponseMessage(HttpStatusCode.Gone);
        }

        private object Session() => new
        {
            session_id = SessionId,
            phase = phases.Values.Any(static p => p == "lost") ? "failed" : phases.Values.All(static p => p == "departed") ? "ended" : "running",
            placement = (object?)null,
            enrollments = phases.Keys.Select(Enrollment).ToArray(),
        };

        private object Enrollment(string id) => new
        {
            client_id = id,
            account_id = "account-" + id,
            client_class = "player",
            phase = phases[id],
            adapter_id = Ra2LabProfile.AdapterId,
            adapter_version = Ra2LabProfile.AdapterVersion,
        };

        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
    }

    [Fact]
    public void TheTunnelStubRetriesOnAFreshListenerWhenItsPortIsTaken()
    {
        // An HttpListener whose Start failed is disposed, so a retry that reuses
        // it throws ObjectDisposedException (two Windows CI failures, PR #21 and
        // #23; reproduced on Linux by this test).
        using HttpListener occupier = new();
        int taken = 0;
        for (int attempt = 0; taken == 0; attempt++)
        {
            int port = Random.Shared.Next(20000, 60000);
            occupier.Prefixes.Clear();
            occupier.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { occupier.Start(); taken = port; }
            catch (HttpListenerException) when (attempt < 20) { }
        }
        int free = taken == 59999 ? 20000 : taken + 1;
        Queue<int> ports = new([taken, free, free + 1, free + 2]);

        using TunnelStub stub = new(ports.Dequeue);

        Assert.NotEqual(taken, stub.Uri.Port);
        Assert.True(stub.Listening);
    }

    /// <summary>Answers the tunnel's port request on a loopback listener.</summary>
    private sealed class TunnelStub : IDisposable
    {
        private HttpListener listener = new();

        public TunnelStub()
            : this(static () => Random.Shared.Next(20000, 60000))
        {
        }

        /// <param name="nextPort">The port each attempt tries (tests pass a taken one first).</param>
        public TunnelStub(Func<int> nextPort)
        {
            for (int attempt = 0; ; attempt++)
            {
                int port = nextPort();
                Uri = new Uri($"http://127.0.0.1:{port}/");
                // A listener whose Start failed is disposed: each attempt gets a new one.
                if (attempt > 0) listener = new HttpListener();
                listener.Prefixes.Add(Uri.ToString());
                try
                {
                    listener.Start();
                    break;
                }
                catch (HttpListenerException) when (attempt < 20)
                {
                }
            }
            _ = Task.Run(ServeAsync);
        }

        public Uri Uri { get; private set; } = null!;

        public bool Listening => listener.IsListening;

        private async Task ServeAsync()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
                byte[] body = Encoding.ASCII.GetBytes("[50001,50002,50003]");
                context.Response.StatusCode = 200;
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        }

        public void Dispose() => listener.Close();
    }
}

/// <summary>A <see cref="TimeProvider"/> that moves only when told to.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate) return now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer = new(this, callback, state);
        lock (gate) timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public int ArmedTimers
    {
        get
        {
            lock (gate) return timers.Count(static t => t.Due is not null);
        }
    }

    /// <summary>Waits in real time until <paramref name="count"/> timers are armed; false if they never are.</summary>
    public async Task<bool> WaitForTimersAsync(int count)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (ArmedTimers < count)
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(1);
        }
        return true;
    }

    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (gate) target = now + by;
        while (true)
        {
            ManualTimer? next;
            lock (gate)
            {
                next = timers.Where(t => t.Due <= target).OrderBy(static t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    now = target;
                    return;
                }
                now = next.Due!.Value;
                next.Due = next.Period > TimeSpan.Zero && next.Period != Timeout.InfiniteTimeSpan ? now + next.Period : null;
            }
            next.Fire();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (gate) timers.Remove(timer);
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public TimeSpan Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock.gate)
            {
                Period = period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.now + dueTime;
            }
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => clock.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
