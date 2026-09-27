// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json;
using Bindery.Ra2.Adapter;
using Bindery.Ra2.Adapter.Channel;
using Xunit;
using static Bindery.Ra2.Adapter.Tests.ChannelTests;

namespace Bindery.Ra2.Adapter.Tests;

public sealed class EnablerTests
{
    private const string SessionId = "0198c2c3-4d5e-7f70-8123-456789abcdef";
    private const string CaptureId = "0198c2c3-4d5e-7f76-8123-456789abcdef";
    private const string ClientId = "0198c2c3-4d5e-7f71-8123-456789abcdef";

    [Fact]
    public void ControllerDeclarationsFollowTheCoreContract()
    {
        ControllerDeclaration.Human.Validate();
        ControllerDeclaration.BuiltinAi.Validate();
        ControllerDeclaration.Agent("planner/v1", "2026.09.27+abc").Validate();
        Assert.Throws<ArgumentException>(() => ControllerDeclaration.Agent("", "1").Validate());
        Assert.Throws<ArgumentException>(() => ControllerDeclaration.Agent("has space", "1").Validate());
        Assert.Throws<ArgumentException>(() => ControllerDeclaration.Agent("/leading-slash", "1").Validate());
        Assert.Throws<ArgumentException>(() => ControllerDeclaration.Agent("planner", "+1").Validate());
        Assert.Throws<ArgumentException>(() => ControllerDeclaration.Agent(new string('a', 129), "1").Validate());
        Assert.Throws<ArgumentException>(() => new ControllerDeclaration("human", "id").Validate());
        Assert.Throws<ArgumentException>(() => new ControllerDeclaration("robot").Validate());
    }

    [Fact]
    public async Task EnrollmentDeclaresTheControllerAndKeepsCaptureOffers()
    {
        RecordingHandler handler = new(request => Json(HttpStatusCode.Created, new
        {
            public_enrollment = new { client_id = ClientId },
            client_lease_token = "lease-a",
            transport_credential = "unused",
            capture_stream_offers = new[] { new { schema_version = "1", capture_id = CaptureId, producer_class = "player", capture_method = "ra2yrcpp", max_batch_bytes = 1, max_batch_events = 1, max_object_bytes = 4096 } },
        }));
        using HttpClient http = new(handler) { BaseAddress = new Uri("https://control-plane.test") };
        BinderyAdapterClient client = new(http);

        EnrollmentCredentials enrollment = await client.EnrollAsync(Request(ClientClass.Player, ControllerDeclaration.Agent("planner", "1.0")), "key", CancellationToken.None);

        using JsonDocument body = JsonDocument.Parse(handler.Bodies[0]);
        JsonElement controller = body.RootElement.GetProperty("controller");
        Assert.Equal("agent", controller.GetProperty("kind").GetString());
        Assert.Equal("planner", controller.GetProperty("controller_id").GetString());
        Assert.Equal("1.0", controller.GetProperty("controller_version").GetString());
        CaptureStreamOffer offer = Assert.Single(enrollment.CaptureOffers!);
        Assert.Equal(CaptureId, offer.CaptureId);
        Assert.Equal(4096, offer.MaxObjectBytes);

        await client.EnrollAsync(Request(ClientClass.Player, null), "key-2", CancellationToken.None);
        using JsonDocument undeclared = JsonDocument.Parse(handler.Bodies[1]);
        Assert.False(undeclared.RootElement.TryGetProperty("controller", out _));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EnrollAsync(Request(ClientClass.Observer, ControllerDeclaration.Human), "key-3", CancellationToken.None));
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Fact]
    public async Task CaptureObjectsAreUploadedUnderTheClientLease()
    {
        RecordingHandler handler = new(request => Json(HttpStatusCode.Created, new
        {
            schema_version = "1",
            content_hash = "sha256:abc",
            media_type = request.Content!.Headers.ContentType!.MediaType,
            bytes = 5,
            capture_id = CaptureId,
            producer_client_id = ClientId,
            capture_method = "ra2yrcpp",
            received_at = DateTimeOffset.UtcNow,
        }));
        using HttpClient http = new(handler) { BaseAddress = new Uri("https://control-plane.test") };
        BinderyAdapterClient client = new(http);
        CaptureStreamOffer offer = new(CaptureId, ClientClass.Player, "ra2yrcpp", 8);

        CaptureObjectManifest manifest = await client.StoreCaptureObjectAsync("lease-a", offer, CaptureMediaTypes.DecisionTrace, "hello"u8.ToArray());

        Assert.Equal("sha256:abc", manifest.ContentHash);
        Assert.Equal(CaptureMediaTypes.DecisionTrace, manifest.MediaType);
        Assert.Equal($"/v1/captures/{CaptureId}/objects", handler.Paths[0]);
        Assert.Equal("Bearer lease-a", handler.Authorizations[0]);
        await Assert.ThrowsAsync<ArgumentException>(() => client.StoreCaptureObjectAsync("lease-a", offer, CaptureMediaTypes.DecisionTrace, new byte[9]));
    }

    [Fact]
    public async Task RunnerUploadsArtifactsAndRecordsWhatCouldNotBeStored()
    {
        string trace = Path.GetTempFileName();
        await File.WriteAllTextAsync(trace, "{\"kind\":\"seat_opened\"}\n");
        try
        {
            RecordingHandler handler = new(request => Json(HttpStatusCode.Created, new
            {
                content_hash = "sha256:trace",
                media_type = CaptureMediaTypes.DecisionTrace,
                bytes = 23,
                capture_id = CaptureId,
                producer_client_id = ClientId,
            }));
            using HttpClient http = new(handler) { BaseAddress = new Uri("https://control-plane.test") };
            BinderyAdapterClient controlPlane = new(http);
            LiveAcceptanceRunner runner = new(new CncNetPrivateMatchDriver(controlPlane, http.BaseAddress!), controlPlane, new NullHost(), new NullHost());
            PreparedLiveClient offered = Prepared("instance-a", [new CaptureStreamOffer(CaptureId, ClientClass.Player, "ra2yrcpp", 1 << 20)]);
            PreparedLiveClient bare = Prepared("instance-b", null);

            IReadOnlyList<LiveArtifactEvidence> results = await runner.UploadArtifactsAsync(
                _ => Task.FromResult<IReadOnlyList<LiveArtifact>>(
                [
                    new LiveArtifact("instance-a", CaptureMediaTypes.DecisionTrace, trace),
                    new LiveArtifact("instance-b", CaptureMediaTypes.DecisionTrace, trace),
                    new LiveArtifact("instance-z", CaptureMediaTypes.DecisionTrace, trace),
                    new LiveArtifact("instance-a", CaptureMediaTypes.DecisionTrace, trace + ".missing"),
                ]),
                [offered, bare],
                CancellationToken.None);

            Assert.Equal("sha256:trace", results[0].ContentHash);
            Assert.Null(results[0].Failure);
            Assert.Contains("no capture", results[1].Failure);
            Assert.Contains("instance id", results[2].Failure);
            Assert.Contains("FileNotFoundException", results[3].Failure);
            Assert.Single(handler.Paths);
        }
        finally
        {
            File.Delete(trace);
        }
    }

    [Fact]
    public async Task LauncherDeclaresTheAgentAndUploadsItsTrace()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bindery-enabler-" + Guid.NewGuid().ToString("N"));
        try
        {
            AgentSeatAssignment agent = new("Americans", "instance-a", ControllerDeclaration.Agent("scripted", "0.0.1"));
            LiveAcceptanceRequest? seen = null;
            IReadOnlyList<LiveArtifact>? collected = null;
            LiveAcceptanceMatchLauncher launcher = new(
                async (request, hooks, ct) =>
                {
                    seen = request;
                    collected = await hooks.CollectArtifacts!(ct);
                    LiveArtifact trace = Assert.Single(collected);
                    return Evidence(complete: true) with
                    {
                        Artifacts = [new LiveArtifactEvidence(trace.ClientInstanceId, trace.MediaType, trace.Path, "sha256:trace", 10, CaptureId, null)],
                    };
                },
                _ => LiveRequest(directory),
                new LiveChannelMatchOptions(
                    Telemetry: _ => new FakeTelemetry([Observation(Ra2TelemetryEventTypes.MatchStarted, "{}"), Observation(Ra2TelemetryEventTypes.MatchEnded, "{}")]),
                    AgentSeat: _ => new AgentSeat(new ScriptedController(), new MemoryCommands("Americans"), new PlayerObservationFilter("Americans"))));

            ChannelMatchContext context = new("channel-1", 1, "MAP01.MAP", new CaptureSource("instance-a", ClientClass.Player), agent);
            ChannelMatchResult result = await launcher.RunMatchAsync(context, _ => Task.CompletedTask, CancellationToken.None);

            Assert.Equal(agent.Controller, seen!.First.Controller);
            Assert.Null(seen.Second.Controller);
            LiveArtifact artifact = Assert.Single(collected!);
            Assert.Equal("instance-a", artifact.ClientInstanceId);
            Assert.Equal(CaptureMediaTypes.DecisionTrace, artifact.MediaType);
            Assert.True(File.Exists(artifact.Path));
            Assert.Equal("sha256:trace", result.DecisionTraceContentHash);

            ChannelRequest channel = new("channel-1", "MAP01.MAP", context.Capture,
                new BroadcastPlan([new BroadcastDestination("room", BroadcastDestinationKind.LocalRoom, new Uri("rtmp://127.0.0.1:1935/ra2"))]),
                AgentSeat: agent);
            ChannelMatchRecord record = ChannelRunner.FromResult(channel, context, DateTimeOffset.UtcNow, result);
            Assert.Equal("sha256:trace", record.DecisionTraceContentHash);
            Assert.Equal("agent", record.AgentController!.Kind);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AgentSeatMustBeAPlayerOfTheMatch()
    {
        LiveAcceptanceRequest request = LiveRequest(Path.GetTempPath());
        ChannelMatchContext onObserver = new("c", 1, "MAP01.MAP", new CaptureSource("instance-a", ClientClass.Player),
            new AgentSeatAssignment("Americans", "instance-z", ControllerDeclaration.Agent("x", "1")));
        Assert.Throws<InvalidOperationException>(() => LiveAcceptanceMatchLauncher.Validate(onObserver, request));
        Assert.Throws<ArgumentException>(() => new AgentSeatAssignment("Americans", "instance-a", ControllerDeclaration.Human).Validate());
    }

    [Fact]
    public async Task RecordingRoundTripsAndFollowsAGrowingFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "bindery-rec-" + Guid.NewGuid().ToString("N") + ".ndjson");
        try
        {
            RawObservation started = Observation(Ra2TelemetryEventTypes.MatchStarted, "{\"map\":\"MAP01\"}");
            RawObservation credits = Observation(Ra2TelemetryEventTypes.CreditsSampled, "{\"house\":\"Americans\",\"credits\":10}");
            RawObservation ended = Observation(Ra2TelemetryEventTypes.MatchEnded, "{\"winner\":\"Americans\"}");
            string startedLine = NdjsonTelemetryFormat.Serialize(started);
            Assert.Contains("\"event_type\":\"ra2.match.started\"", startedLine, StringComparison.Ordinal);

            NdjsonTelemetrySource follower = new(path, follow: true, pollInterval: TimeSpan.FromMilliseconds(10));
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            Task<List<RawObservation>> reading = Collect(follower, timeout.Token);

            await Task.Delay(50);
            await File.WriteAllTextAsync(path, startedLine + "\n" + NdjsonTelemetryFormat.Serialize(credits)[..20]);
            await Task.Delay(50);
            await File.AppendAllTextAsync(path, NdjsonTelemetryFormat.Serialize(credits)[20..] + "\n" + NdjsonTelemetryFormat.Serialize(ended) + "\n");

            List<RawObservation> seen = await reading;
            Assert.Equal([started.EventId, credits.EventId, ended.EventId], seen.Select(static o => o.EventId));
            Assert.Equal(10, seen[1].Payload.GetProperty("credits").GetInt32());
            Assert.Equal(3, follower.Capture.RawEventCount);

            List<RawObservation> replay = await Collect(new NdjsonTelemetrySource(path), CancellationToken.None);
            Assert.Equal(3, replay.Count);

            await File.AppendAllTextAsync(path, "{not json}\n");
            await Assert.ThrowsAsync<InvalidDataException>(() => Collect(new NdjsonTelemetrySource(path), CancellationToken.None));
            await Assert.ThrowsAsync<FileNotFoundException>(() => Collect(new NdjsonTelemetrySource(path + ".absent"), CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<List<RawObservation>> Collect(IRa2TelemetrySource source, CancellationToken cancellationToken)
    {
        List<RawObservation> seen = [];
        await foreach (RawObservation observation in source.ReadAsync(cancellationToken)) seen.Add(observation);
        return seen;
    }

    private static ClientEnrollmentRequest Request(ClientClass clientClass, ControllerDeclaration? controller) => new(
        "account-token",
        "join-token",
        SessionId,
        "instance-a",
        clientClass,
        new AdapterIdentity(Ra2LabProfile.AdapterId, Ra2LabProfile.AdapterVersion),
        new CompatibilityHashes("sha256:game", "sha256:mod", "sha256:map"),
        null,
        controller);

    private static MatchClientDefinition Definition(string account, string instance) => new(
        new IdentityCredentials(account, "token-" + account),
        instance,
        ClientClass.Player,
        new AdapterIdentity(Ra2LabProfile.AdapterId, Ra2LabProfile.AdapterVersion),
        new CompatibilityHashes("sha256:game", "sha256:mod", "sha256:map"));

    private static PreparedLiveClient Prepared(string instance, IReadOnlyList<CaptureStreamOffer>? offers) => new(
        Definition("account-" + instance, instance),
        new EnrollmentCredentials(ClientId, "lease-" + instance, "unused", offers),
        null!,
        new RelayPlacement("eu-north", "cncnet-private", "allocation", "192.168.122.1:50000", "relay-placement/v1"));

    private static LiveAcceptanceRequest LiveRequest(string directory) => new(
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
        "127.0.0.1:14521");

    private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        public List<string> Paths { get; } = [];

        public List<string?> Authorizations { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Authorizations.Add(request.Headers.Authorization?.ToString());
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }

    private sealed class NullHost : ILiveClientHost
    {
        public string Description => "null";

        public Task ValidateAsync(LiveClientLaunch launch, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> Sha256Async(string path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task InstallSpawnIniAsync(string workingDirectory, string content, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RestoreSpawnIniAsync(string workingDirectory, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ResetSpawnerLogAsync(string workingDirectory, string logName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> ReadSpawnerLogAsync(string workingDirectory, string logName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public string ResolveSpawnIniPath(string workingDirectory, string localSpawnIniPath) => throw new NotSupportedException();

        public Task<int> RunAsync(SpawnConfiguration spawn, LiveClientLaunch launch, string spawnIniPath, Func<LifecycleReport, Task> report, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
