// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Bindery.Ra2.Adapter;
using Bindery.Ra2.Adapter.Channel;
using Xunit;
using static Bindery.Ra2.Adapter.Tests.ChannelTests;

namespace Bindery.Ra2.Adapter.Tests;

public sealed class ChannelExtensionTests
{
    private static readonly CaptureSource playerView = new("instance-a", ClientClass.Player);

    [Fact]
    public void TrackerNamesTheLastStandingHouseOnlyAfterTheMatchEnds()
    {
        MatchTelemetryTracker tracker = new();
        tracker.Observe(Observation(Ra2TelemetryEventTypes.MatchStarted, "{}"));
        tracker.Observe(Observation(Ra2TelemetryEventTypes.PlayerJoined, "{\"house\":\"Americans\"}"));
        tracker.Observe(Observation(Ra2TelemetryEventTypes.PlayerJoined, "{\"house\":\"Soviets\"}"));
        tracker.Observe(Observation(Ra2TelemetryEventTypes.PlayerDefeated, "{\"house\":\"Soviets\"}"));
        Assert.Null(tracker.Winner);

        tracker.Observe(Observation(Ra2TelemetryEventTypes.MatchEnded, "{}"));

        Assert.True(tracker.Started);
        Assert.True(tracker.Ended);
        Assert.Equal("Americans", tracker.Winner);
    }

    [Fact]
    public void TrackerPrefersADeclaredWinnerAndLeavesDrawsUnnamed()
    {
        MatchTelemetryTracker declared = new();
        declared.Observe(Observation(Ra2TelemetryEventTypes.MatchEnded, "{\"winner\":\"Yuri\"}"));
        Assert.Equal("Yuri", declared.Winner);

        MatchTelemetryTracker draw = new();
        draw.Observe(Observation(Ra2TelemetryEventTypes.PlayerJoined, "{\"house\":\"Americans\"}"));
        draw.Observe(Observation(Ra2TelemetryEventTypes.PlayerJoined, "{\"house\":\"Soviets\"}"));
        draw.Observe(Observation(Ra2TelemetryEventTypes.MatchEnded, "{}"));
        Assert.Null(draw.Winner);
    }

    [Fact]
    public async Task FanOutGivesEveryBranchTheWholeStream()
    {
        FakeTelemetry source = new(
        [
            Observation(Ra2TelemetryEventTypes.MatchStarted, "{}"),
            Observation(Ra2TelemetryEventTypes.MatchEnded, "{}"),
        ]);
        TelemetryFanOut fanOut = new(source);
        IRa2TelemetrySource left = fanOut.Branch();
        IRa2TelemetrySource right = fanOut.Branch();

        await fanOut.RunAsync(CancellationToken.None);

        Assert.Equal(2, await CountAsync(left));
        Assert.Equal(2, await CountAsync(right));
    }

    [Fact]
    public async Task LauncherCutsToTheMatchWhenTheCapturedClientStarts()
    {
        List<string> order = [];
        LiveAcceptanceMatchLauncher launcher = new(
            async (request, hooks, ct) =>
            {
                Func<LiveLifecycleNotice, CancellationToken, Task> onLifecycle = hooks.OnLifecycle!;
                await onLifecycle(new LiveLifecycleNotice("instance-a", ClientClass.Player, LifecycleKind.Ready), ct);
                order.Add("a-ready");
                await onLifecycle(new LiveLifecycleNotice("instance-b", ClientClass.Player, LifecycleKind.Started), ct);
                order.Add("b-started");
                await onLifecycle(new LiveLifecycleNotice("instance-a", ClientClass.Player, LifecycleKind.Started), ct);
                order.Add("a-started");
                await onLifecycle(new LiveLifecycleNotice("instance-a", ClientClass.Player, LifecycleKind.Started), ct);
                return Evidence(complete: true);
            },
            _ => Request());

        ChannelMatchResult result = await launcher.RunMatchAsync(Context(), _ =>
        {
            order.Add("on-air");
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(["a-ready", "b-started", "on-air", "a-started"], order);
        Assert.Null(result.TelemetryObserved);
        Assert.Null(result.Winner);
    }

    [Fact]
    public void LauncherRejectsACaptureClientOutsideTheMatch()
    {
        ChannelMatchContext context = Context() with { Capture = new CaptureSource("observer-1", ClientClass.Observer) };
        Assert.Throws<InvalidOperationException>(() => LiveAcceptanceMatchLauncher.Validate(context, Request()));

        LiveAcceptanceRequest withObserver = Request() with { Observer = Observer() };
        LiveAcceptanceMatchLauncher.Validate(context, withObserver);
    }

    [Fact]
    public void AnAgentSeatMustPlayItsClientsHouseAndEveryPlayerNameMustBeDistinct()
    {
        ChannelMatchContext context = Context() with { AgentSeat = Agent() };
        LiveAcceptanceMatchLauncher.Validate(context, Request());

        // instance-a plays "Americans"; a seat for "Soviets" there would steer the other house's view.
        ChannelMatchContext wrongHouse = Context() with { AgentSeat = Agent() with { House = "Soviets" } };
        Assert.Throws<InvalidOperationException>(() => LiveAcceptanceMatchLauncher.Validate(wrongHouse, Request()));
        // Two houses under one name would make the filter admit the enemy as "own".
        Assert.Throws<InvalidOperationException>(() => LiveAcceptanceMatchLauncher.Validate(context, Request() with { SecondLaunch = Launch("americans") }));
        Assert.Throws<InvalidOperationException>(() => LiveAcceptanceMatchLauncher.Validate(context, Request() with { Observer = Observer() with { Launch = Launch("Americans", spectator: true) } }));
    }

    [Fact]
    public async Task TheSeatFactoryIsGivenTheAgentClientsOwnLaunch()
    {
        AgentSeatLaunch? given = null;
        LiveAcceptanceMatchLauncher launcher = new(
            (request, hooks, ct) => Task.FromResult(Evidence(complete: true)),
            _ => Request() with { FirstLaunch = Launch("Americans") with { CommandEndpoint = "192.168.122.10:14521" }, SecondLaunch = Launch("Soviets") with { CommandEndpoint = "192.168.122.20:14521" } },
            new LiveChannelMatchOptions(
                Telemetry: _ => new FakeTelemetry([Observation(Ra2TelemetryEventTypes.MatchStarted, "{}"), Observation(Ra2TelemetryEventTypes.MatchEnded, "{}")]),
                AgentSeat: launch =>
                {
                    given = launch;
                    return new AgentSeat(new ScriptedController(), new MemoryCommands("Americans"), new PlayerObservationFilter("Americans"));
                }));

        await launcher.RunMatchAsync(Context() with { AgentSeat = Agent() }, _ => Task.CompletedTask, CancellationToken.None);

        Assert.NotNull(given);
        Assert.Equal("192.168.122.10:14521", given.Launch.CommandEndpoint);
        Assert.Equal("Americans", given.Launch.PlayerName);
        Assert.Equal(Agent(), given.Assignment);
    }

    [Fact]
    public async Task LauncherRecordsWinnerAndAgentTraceFromTelemetry()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bindery-launcher-" + Guid.NewGuid().ToString("N"));
        try
        {
            FakeTelemetry telemetry = new(
            [
                Observation(Ra2TelemetryEventTypes.MatchStarted, "{}"),
                Observation(Ra2TelemetryEventTypes.PlayerJoined, "{\"house\":\"Americans\"}"),
                Observation(Ra2TelemetryEventTypes.PlayerJoined, "{\"house\":\"Soviets\"}"),
                Observation(Ra2TelemetryEventTypes.CreditsSampled, "{\"house\":\"Americans\",\"credits\":100}"),
                Observation(Ra2TelemetryEventTypes.PlayerDefeated, "{\"house\":\"Soviets\"}"),
                Observation(Ra2TelemetryEventTypes.MatchEnded, "{}"),
            ]);
            LiveAcceptanceMatchLauncher launcher = new(
                (request, hooks, ct) => Task.FromResult(Evidence(complete: true)),
                _ => Request() with { EvidenceDirectory = directory },
                new LiveChannelMatchOptions(
                    Telemetry: _ => telemetry,
                    AgentSeat: _ => new AgentSeat(new ScriptedController(), new MemoryCommands("Americans"), new PlayerObservationFilter("Americans"))));

            ChannelMatchResult result = await launcher.RunMatchAsync(Context() with { AgentSeat = Agent() }, _ => Task.CompletedTask, CancellationToken.None);

            Assert.Equal("Americans", result.Winner);
            Assert.True(result.TelemetryEnded);
            Assert.Equal(6, result.TelemetryObserved);
            Assert.Equal(1, result.PlaybookRevisions);
            Assert.Equal(Path.Combine(directory, AgentSeat.TraceFileName), result.DecisionTracePath);
            Assert.Null(result.TelemetryIssue);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LauncherRequiresASeatForANamedAgentHouse()
    {
        LiveAcceptanceMatchLauncher launcher = new((_, _, _) => Task.FromResult(Evidence(complete: true)), _ => Request());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            launcher.RunMatchAsync(Context() with { AgentSeat = Agent() }, _ => Task.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public async Task BrokenTelemetryIsRecordedWithoutFailingThePlayedMatch()
    {
        LiveAcceptanceMatchLauncher launcher = new(
            (_, _, _) => Task.FromResult(Evidence(complete: true)),
            _ => Request(),
            new LiveChannelMatchOptions(Telemetry: _ => new BrokenTelemetry(), TelemetryDrain: TimeSpan.FromMilliseconds(10)));

        ChannelMatchResult result = await launcher.RunMatchAsync(Context(), _ => Task.CompletedTask, CancellationToken.None);

        Assert.NotNull(result.Evidence);
        Assert.Contains("bridge went away", result.TelemetryIssue);
        ChannelMatchRecord record = ChannelRunner.FromResult(ChannelRequestFor(), Context(), DateTimeOffset.UtcNow, result);
        Assert.Equal(ChannelMatchOutcome.Completed, record.Outcome);
        Assert.Equal(result.TelemetryIssue, record.TelemetryIssue);
    }

    [Fact]
    public void ObserverMustBeASpectatorWithItsOwnIdentity()
    {
        LiveAcceptanceRunner.ValidateObserver(Request(), Observer());

        Assert.Throws<ArgumentException>(() => LiveAcceptanceRunner.ValidateObserver(Request(), Observer() with { Launch = Observer().Launch with { IsSpectator = false } }));
        Assert.Throws<ArgumentException>(() => LiveAcceptanceRunner.ValidateObserver(Request(), Observer() with { Definition = Observer().Definition with { ClientClass = ClientClass.Player } }));
        Assert.Throws<ArgumentException>(() => LiveAcceptanceRunner.ValidateObserver(Request(), Observer() with { Definition = Observer().Definition with { ClientInstanceId = "instance-a" } }));
        Assert.Throws<ArgumentException>(() => LiveAcceptanceRunner.ValidateObserver(Request(), Observer() with { EnrollmentIdempotencyKey = "enroll-a" }));
        Assert.Throws<ArgumentException>(() => LiveAcceptanceRunner.ValidateObserver(Request(), Observer() with { Launch = Observer().Launch with { MapId = "OTHER.MAP" } }));
    }

    [Fact]
    public void SpectatorIsRenderedAsAPeerWithoutAStartingLocation()
    {
        SpawnParticipant a = new("player-a", 50001, SpawnLocation: 0);
        SpawnParticipant b = new("player-b", 50002, Color: 1, SpawnLocation: 1);
        SpawnParticipant observer = new("observer", 50003, SpawnLocation: -1, IsSpectator: true);
        SpawnMatchPlan plan = new("spawnmap.ini", "123", 42, false, observer, [a, b], [a, b, observer], [], "192.168.122.1", 50000);

        string ini = SpawnIniRenderer.Render(plan);

        Assert.Contains("IsSpectator=Yes", ini.Split("[Other1]")[0], StringComparison.Ordinal);
        Assert.Contains("PlayerCount=3", ini, StringComparison.Ordinal);
        Assert.Contains("[Other2]", ini, StringComparison.Ordinal);
        Assert.Contains("Multi1=0", ini, StringComparison.Ordinal);
        Assert.Contains("Multi2=1", ini, StringComparison.Ordinal);
        Assert.DoesNotContain("Multi3=", ini, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrivateDriverEnrollsTheObserverIntoThePlayersSession()
    {
        using ObserverEnrollmentHandler handler = new();
        using HttpClient http = new(handler) { BaseAddress = new Uri("https://control-plane.test") };
        CncNetPrivateMatchDriver driver = new(new BinderyAdapterClient(http), http.BaseAddress!);
        SessionCredentials session = new(
            "0198c2c3-4d5e-7f70-8123-456789abcdef",
            "join-token",
            new RelayPlacement("eu-north", "cncnet-private", "0198c2c3-4d5e-7f60-8123-456789abcdef", "192.168.122.1:50000", "relay-placement/v1"));

        PreparedLiveClient observer = await driver.EnrollObserverAsync(session, Observer().Definition, "enroll-observer");

        Assert.Equal("observer", handler.ClientClass);
        Assert.Equal("observer-1", observer.Definition.ClientInstanceId);
        Assert.Equal(ClientClass.Observer, observer.Configuration.ClientClass);
        Assert.Equal("192.168.122.1", observer.Configuration.RelayHost);
        Assert.Null(observer.Configuration.RelayCredential);

        SessionCredentials native = session with { Placement = session.Placement! with { RelayProviderId = "bindery-native" } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.EnrollObserverAsync(native, Observer().Definition, "enroll-observer-2"));
        await Assert.ThrowsAsync<NotSupportedException>(() => new TwoClientMatchDriver(new BinderyAdapterClient(http), http.BaseAddress!).EnrollObserverAsync(session, Observer().Definition, "k"));
    }

    private static AgentSeatAssignment Agent() => new("Americans", "instance-a", ControllerDeclaration.Agent("scripted", "0.0.1"));

    private static ChannelMatchContext Context() => new("channel-1", 1, "MAP01.MAP", playerView, null);

    private static ChannelRequest ChannelRequestFor() => new(
        "channel-1",
        "MAP01.MAP",
        playerView,
        new BroadcastPlan([new BroadcastDestination("room", BroadcastDestinationKind.LocalRoom, new Uri("rtmp://127.0.0.1:1935/ra2"))]));

    private static MatchClientDefinition Definition(string account, string instance, ClientClass clientClass = ClientClass.Player) => new(
        new IdentityCredentials(account, "token-" + account),
        instance,
        clientClass,
        new AdapterIdentity(Ra2LabProfile.AdapterId, Ra2LabProfile.AdapterVersion),
        new CompatibilityHashes("sha256:game", "sha256:mod", "sha256:map"),
        [new RegionProbe("eu-north", 40)]);

    private static LiveClientLaunch Launch(string name, bool spectator = false) =>
        new("C:/Bindery/client/gamemd.exe", "C:/Bindery/client", "MAP01.MAP", name, IsSpectator: spectator);

    private static LiveObserverClient Observer() => new(Definition("account-o", "observer-1", ClientClass.Observer), Launch("observer", spectator: true), "enroll-o");

    private static LiveAcceptanceRequest Request() => new(
        new SessionCreationRequest(
            new SessionCompatibility(Ra2LabProfile.GameFamily, Ra2LabProfile.GameVersion, "sha256:game", Ra2LabProfile.AdapterId, Ra2LabProfile.AdapterVersion, Ra2LabProfile.ModId, "sha256:mod", "MAP01.MAP", "sha256:map"),
            new ParticipantPolicy(2, 2, 1),
            new PlacementIntent(["eu-north"], 100),
            new CapturePolicy(true, true, true)),
        Definition("account-a", "instance-a"),
        Definition("account-b", "instance-b"),
        Launch("Americans"),
        Launch("Soviets"),
        "session-key",
        "enroll-a",
        "enroll-b",
        Path.GetTempPath(),
        "golden-1",
        "127.0.0.1:14521");

    private static async Task<int> CountAsync(IRa2TelemetrySource source)
    {
        int count = 0;
        await foreach (RawObservation _ in source.ReadAsync()) count++;
        return count;
    }

    private sealed class BrokenTelemetry : IRa2TelemetrySource
    {
        public Ra2TelemetryCapture Capture { get; } = new(Ra2LabProfile.TelemetryProtocol, new Ra2YrcppEndpoint("127.0.0.1", 14521), false, null, null);

        public async IAsyncEnumerable<RawObservation> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return Observation(Ra2TelemetryEventTypes.MatchStarted, "{}");
            throw new IOException("bridge went away");
        }
    }

    private sealed class ObserverEnrollmentHandler : HttpMessageHandler
    {
        public string? ClientClass { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Post || !path.EndsWith("/enrollments", StringComparison.Ordinal))
                throw new InvalidOperationException($"unexpected {request.Method} {path}");
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            ClientClass = body.RootElement.GetProperty("client_class").GetString();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    public_enrollment = new { client_id = "0198c2c3-4d5e-7f75-8123-456789abcdef" },
                    client_lease_token = "lease-o",
                    transport_credential = "unused",
                }), Encoding.UTF8, "application/json"),
            };
        }
    }
}
