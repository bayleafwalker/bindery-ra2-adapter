// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Nodes;
using Bindery.Ra2.Adapter;
using Bindery.Ra2.Adapter.Channel;
using Xunit;
using static Bindery.Ra2.Adapter.Tests.ChannelTests;

namespace Bindery.Ra2.Adapter.Tests;

public sealed class BroadcastExtrasTests
{
    private static readonly CaptureSource view = new("instance-a", ClientClass.Player);

    [Fact]
    public void OverlayShowsCreditsDefeatsAndTheWinner()
    {
        MatchOverlay overlay = new(new ChannelMatchContext("channel-1", 3, "MAP01.MAP", view, null));
        overlay.Observe(Observation(Ra2TelemetryEventTypes.MatchStarted, "{}"));
        overlay.Observe(Observation(Ra2TelemetryEventTypes.PlayerJoined, "{\"house\":\"Americans\"}"));
        overlay.Observe(Observation(Ra2TelemetryEventTypes.PlayerJoined, "{\"house\":\"Soviets\"}"));
        overlay.Observe(Observation(Ra2TelemetryEventTypes.CreditsSampled, "{\"house\":\"Americans\",\"credits\":12500}"));
        overlay.Observe(Observation(Ra2TelemetryEventTypes.PlayerDefeated, "{\"house\":\"Soviets\"}"));
        overlay.Observe(Observation(Ra2TelemetryEventTypes.MatchEnded, "{}"));

        string text = OverlayText.Render(overlay.State);

        Assert.StartsWith("Match 3 · MAP01.MAP · ", text, StringComparison.Ordinal);
        Assert.Contains("Americans — $12,500", text, StringComparison.Ordinal);
        Assert.Contains("Soviets — defeated", text, StringComparison.Ordinal);
        Assert.EndsWith("Winner: Americans", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LauncherThrottlesOverlayUpdatesButNeverDropsHeadlines()
    {
        RecordingOverlay sink = new();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        RawObservation At(double seconds, string type, string payload) => Observation(type, payload) with { ReceivedAt = t0.AddSeconds(seconds) };
        LiveAcceptanceMatchLauncher launcher = new(
            (_, _, _) => Task.FromResult(Evidence(complete: true)),
            _ => LiveRequestFor(),
            new LiveChannelMatchOptions(
                Telemetry: _ => new FakeTelemetry(
                [
                    At(0, Ra2TelemetryEventTypes.MatchStarted, "{}"),
                    At(0.1, Ra2TelemetryEventTypes.CreditsSampled, "{\"house\":\"Americans\",\"credits\":1}"),
                    At(0.2, Ra2TelemetryEventTypes.CreditsSampled, "{\"house\":\"Americans\",\"credits\":2}"),
                    At(1.5, Ra2TelemetryEventTypes.CreditsSampled, "{\"house\":\"Americans\",\"credits\":3}"),
                    At(1.6, Ra2TelemetryEventTypes.PlayerDefeated, "{\"house\":\"Soviets\"}"),
                    At(1.7, Ra2TelemetryEventTypes.MatchEnded, "{}"),
                ]),
                Overlay: sink));

        ChannelMatchResult result = await launcher.RunMatchAsync(new ChannelMatchContext("channel-1", 1, "MAP01.MAP", view, null), _ => Task.CompletedTask, CancellationToken.None);

        // started, credits at 1.5 (interval passed), defeated, ended; 0.1 and 0.2 were throttled.
        Assert.Equal(4, sink.States.Count);
        Assert.True(sink.States[^1].Ended);
        Assert.Null(result.TelemetryIssue);
    }

    [Fact]
    public async Task AFailingOverlayIsNotedOnceAndTheMatchStands()
    {
        LiveAcceptanceMatchLauncher launcher = new(
            (_, _, _) => Task.FromResult(Evidence(complete: true)),
            _ => LiveRequestFor(),
            new LiveChannelMatchOptions(
                Telemetry: _ => new FakeTelemetry([Observation(Ra2TelemetryEventTypes.MatchStarted, "{}"), Observation(Ra2TelemetryEventTypes.MatchEnded, "{}")]),
                Overlay: new RecordingOverlay { Fail = true }));

        ChannelMatchResult result = await launcher.RunMatchAsync(new ChannelMatchContext("channel-1", 1, "MAP01.MAP", view, null), _ => Task.CompletedTask, CancellationToken.None);

        Assert.NotNull(result.Evidence);
        Assert.Equal(1, result.TelemetryIssue!.Split("overlay:").Length - 1);
    }

    [Fact]
    public void AudioMonitorReportsSilenceAndMissingMeters()
    {
        ManualClock clock = new(DateTimeOffset.UtcNow);
        ObsAudioMonitor monitor = new(_ => throw new NotSupportedException(), null, ["game-audio", "commentary"], tolerance: TimeSpan.FromSeconds(5), clock: clock);
        static JsonArray Meters(double peak) => new(new JsonObject
        {
            ["inputName"] = "game-audio",
            ["inputLevelsMul"] = new JsonArray(new JsonArray(0.1, peak, peak), new JsonArray(0.1, peak / 2, peak)),
        });

        monitor.Meter(Meters(0.5));
        Assert.Null(monitor.EndMatch());

        monitor.BeginMatch();
        monitor.Meter(Meters(0.5));
        clock.Advance(TimeSpan.FromSeconds(1));
        monitor.Meter(Meters(0.5));
        clock.Advance(TimeSpan.FromSeconds(4));
        monitor.Meter(Meters(0.00001));
        clock.Advance(TimeSpan.FromSeconds(4));
        monitor.Meter(Meters(0.00001));
        clock.Advance(TimeSpan.FromSeconds(1));
        monitor.Meter(Meters(0.5));
        string? issue = monitor.EndMatch();

        Assert.Contains("'game-audio' was silent for 9s", issue);
        Assert.Contains("'commentary' sent no meters", issue);

        monitor.BeginMatch();
        monitor.Meter(Meters(0.5));
        clock.Advance(TimeSpan.FromSeconds(2));
        monitor.Meter(Meters(0.5));
        Assert.Equal("audio input 'commentary' sent no meters", monitor.EndMatch());
    }

    [Fact]
    public async Task ChannelRecordsWhatBroadcastHealthSaw()
    {
        FakeHealth health = new() { Issue = "audio input 'game-audio' was silent for 30s" };
        ChannelRunner runner = new(new StartingLauncher(), new NoBroadcastProduction(), new MemoryRecords(), (_, _) => Task.CompletedTask, health);

        ChannelSessionSummary summary = await runner.RunAsync(new ChannelRequest(
            "channel-1", "MAP01.MAP", view,
            new BroadcastPlan([new BroadcastDestination("room", BroadcastDestinationKind.LocalRoom, new Uri("rtmp://127.0.0.1:1935/ra2"))]),
            MaximumMatches: 2, HoldingDuration: TimeSpan.Zero));

        Assert.Equal(2, health.Begun);
        Assert.All(summary.Matches, static m => Assert.Contains("silent for 30s", m.BroadcastIssue));
        Assert.All(summary.Matches, static m => Assert.Equal(ChannelMatchOutcome.Completed, m.Outcome));
    }

    [Fact]
    public async Task ReportComparesControllersOnCompletedMatchesOnly()
    {
        ControllerDeclaration planner = ControllerDeclaration.Agent("bindery.playbook/rules/v1", "0.1.0");
        ChannelMatchRecord Match(int index, ChannelMatchOutcome outcome, string? winner, ControllerDeclaration? controller, int revisions = 0, string map = "MAP01.MAP") => new(
            "channel-1", index, null, null, map, 100 + index, "golden-1", [], Ra2LabProfile.AdapterId, Ra2LabProfile.AdapterVersion,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, outcome, winner, null, null, null, revisions, view, false,
            AgentController: controller, AgentHouse: controller is null ? null : "Americans",
            DecisionTraceContentHash: controller is null ? null : "sha256:t" + index);

        ChannelMatchRecord[] records =
        [
            Match(1, ChannelMatchOutcome.Completed, "Americans", planner, 4),
            Match(2, ChannelMatchOutcome.Completed, "Soviets", planner, 2),
            Match(3, ChannelMatchOutcome.Completed, null, planner, 3),
            Match(4, ChannelMatchOutcome.Incomplete, "Americans", planner, 9),
            Match(5, ChannelMatchOutcome.Completed, "Soviets", null),
            Match(6, ChannelMatchOutcome.Failed, null, planner, map: "MAP02.MAP"),
        ];
        string directory = Path.Combine(Path.GetTempPath(), "bindery-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            NdjsonChannelRecordSink sink = new(directory);
            foreach (ChannelMatchRecord record in records) await sink.WriteAsync(record, CancellationToken.None);
            IReadOnlyList<ExperimentRow> rows = ChannelExperimentReport.Build(ChannelExperimentReport.Read(sink.Path));

            Assert.Equal(3, rows.Count);
            ExperimentRow agent = rows.Single(static r => r.MapId == "MAP01.MAP" && r.Controller == "bindery.playbook/rules/v1@0.1.0");
            Assert.Equal((4, 3, 1, 1, 1), (agent.Matches, agent.Completed, agent.AgentWins, agent.AgentLosses, agent.Undecided));
            Assert.Equal(3, agent.MeanPlaybookRevisions);
            Assert.Equal(4, agent.TracesInBindery);
            ExperimentRow baseline = rows.Single(static r => r.Controller == ChannelExperimentReport.NoAgent);
            Assert.Equal((1, 1, 0, 0), (baseline.Matches, baseline.Completed, baseline.AgentWins, baseline.AgentLosses));
            Assert.Equal(0, rows.Single(static r => r.MapId == "MAP02.MAP").Completed);
            Assert.StartsWith("map\tcontroller\tmatches", ChannelExperimentReport.RenderTable(rows), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static LiveAcceptanceRequest LiveRequestFor() => new(
        new SessionCreationRequest(
            new SessionCompatibility(Ra2LabProfile.GameFamily, Ra2LabProfile.GameVersion, "sha256:game", Ra2LabProfile.AdapterId, Ra2LabProfile.AdapterVersion, Ra2LabProfile.ModId, "sha256:mod", "MAP01.MAP", "sha256:map"),
            new ParticipantPolicy(2, 2, 0),
            new PlacementIntent(["eu-north"], 100),
            new CapturePolicy(true, true, false)),
        new MatchClientDefinition(new IdentityCredentials("a", "t"), "instance-a", ClientClass.Player, new AdapterIdentity(Ra2LabProfile.AdapterId, Ra2LabProfile.AdapterVersion), new CompatibilityHashes("g", "m", "p")),
        new MatchClientDefinition(new IdentityCredentials("b", "t"), "instance-b", ClientClass.Player, new AdapterIdentity(Ra2LabProfile.AdapterId, Ra2LabProfile.AdapterVersion), new CompatibilityHashes("g", "m", "p")),
        new LiveClientLaunch("C:/a/gamemd.exe", "C:/a", "MAP01.MAP", "player-a"),
        new LiveClientLaunch("C:/b/gamemd.exe", "C:/b", "MAP01.MAP", "player-b"),
        "s", "a", "b", Path.GetTempPath(), "golden-1", "127.0.0.1:14521");

    private sealed class RecordingOverlay : IOverlaySink
    {
        public List<OverlayState> States { get; } = [];

        public bool Fail { get; init; }

        public Task UpdateAsync(OverlayState state, CancellationToken cancellationToken)
        {
            if (Fail) throw new InvalidOperationException("OBS rejected SetInputSettings");
            States.Add(state);
            return Task.CompletedTask;
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset now = now;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class FakeHealth : IBroadcastHealth
    {
        public int Begun { get; private set; }

        public string? Issue { get; init; }

        public void BeginMatch() => Begun++;

        public string? EndMatch() => Issue;
    }

    private sealed class StartingLauncher : IChannelMatchLauncher
    {
        public async Task<ChannelMatchResult> RunMatchAsync(ChannelMatchContext context, Func<CancellationToken, Task> onAir, CancellationToken cancellationToken)
        {
            await onAir(cancellationToken);
            return new ChannelMatchResult(Evidence(complete: true));
        }
    }

    private sealed class MemoryRecords : IChannelRecordSink
    {
        public Task WriteAsync(ChannelMatchRecord record, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
