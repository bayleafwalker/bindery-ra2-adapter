// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bindery.Ra2.Adapter;
using Bindery.Ra2.Adapter.Channel;
using Xunit;

namespace Bindery.Ra2.Adapter.Tests;

public sealed class ChannelTests
{
    private static readonly CaptureSource playerView = new("instance-a", ClientClass.Player);

    private static BroadcastPlan LocalOnly() => new(
        [new BroadcastDestination("room", BroadcastDestinationKind.LocalRoom, new Uri("rtmp://127.0.0.1:1935/ra2"))]);

    private static BroadcastPlan WithTwitch(bool publish) => new(
        [
            new BroadcastDestination("room", BroadcastDestinationKind.LocalRoom, new Uri("rtmp://127.0.0.1:1935/ra2")),
            new BroadcastDestination("twitch", BroadcastDestinationKind.Twitch, new Uri("rtmp://live.twitch.tv/app"), "TWITCH_STREAM_KEY"),
        ],
        PublishPublicly: publish);

    private static ChannelRequest Request(int matches, BroadcastPlan? plan = null) =>
        new("channel-1", "MAP01.MAP", playerView, plan ?? LocalOnly(), matches, HoldingDuration: TimeSpan.Zero);

    [Fact]
    public void BroadcastPlanRequiresExactlyOneLocalRoom()
    {
        BroadcastPlan none = new([new BroadcastDestination("twitch", BroadcastDestinationKind.Twitch, new Uri("rtmp://live.twitch.tv/app"), "KEY")]);
        Assert.Throws<ArgumentException>(none.Validate);
        LocalOnly().Validate();
    }

    [Fact]
    public void PublicOutputIsOptInAndKeyedByEnvironment()
    {
        Assert.Null(WithTwitch(publish: false).Public);
        Assert.Equal("twitch", WithTwitch(publish: true).Public!.Name);
        Assert.Throws<ArgumentException>(() => (LocalOnly() with { PublishPublicly = true }).Validate());

        BroadcastPlan keyless = new(
            [
                new BroadcastDestination("room", BroadcastDestinationKind.LocalRoom, new Uri("rtmp://127.0.0.1:1935/ra2")),
                new BroadcastDestination("twitch", BroadcastDestinationKind.Twitch, new Uri("rtmp://live.twitch.tv/app")),
            ]);
        Assert.Throws<ArgumentException>(keyless.Validate);

        BroadcastPlan pasted = new(
            [
                new BroadcastDestination("room", BroadcastDestinationKind.LocalRoom, new Uri("rtmp://127.0.0.1:1935/ra2")),
                new BroadcastDestination("twitch", BroadcastDestinationKind.Twitch, new Uri("rtmp://live.twitch.tv/app/live_123_abc"), "KEY"),
            ]);
        Assert.Throws<ArgumentException>(pasted.Validate);
    }

    [Fact]
    public async Task ChannelPlaysMatchesBackToBackAndReleasesOutput()
    {
        FakeProduction production = new();
        FakeLauncher launcher = new(_ => Task.FromResult(new ChannelMatchResult(Evidence(complete: true))));
        MemorySink sink = new();
        ChannelRunner runner = new(launcher, production, sink, NoDelay);

        ChannelSessionSummary summary = await runner.RunAsync(Request(3));

        Assert.Equal(3, summary.Matches.Count);
        Assert.All(summary.Matches, static m => Assert.Equal(ChannelMatchOutcome.Completed, m.Outcome));
        Assert.Equal([1, 2, 3], summary.Matches.Select(static m => m.MatchIndex));
        Assert.Equal(4242, summary.Matches[0].Seed);
        Assert.Equal(["sha256:game"], summary.Matches[0].ClientExecutableHashes);
        Assert.Equal(3, sink.Records.Count);
        Assert.Equal("match budget reached", summary.StopReason);
        Assert.Equal(ChannelPhase.Stopped, runner.Phase);
        Assert.Equal(
            ["start", "holding", "match:instance-a", "holding", "match:instance-a", "holding", "match:instance-a", "holding", "stop"],
            production.Calls);
        Assert.Equal(ChannelPhase.OnAir, summary.Phases[2]);
    }

    [Fact]
    public async Task RepeatedFailuresDrainTheChannel()
    {
        FakeLauncher launcher = new(_ => throw new InvalidOperationException("no placement"));
        ChannelRunner runner = new(launcher, new FakeProduction(), new MemorySink(), NoDelay);

        ChannelSessionSummary summary = await runner.RunAsync(Request(10));

        Assert.Equal(ChannelRequest.MaximumConsecutiveFailures, summary.Matches.Count);
        Assert.All(summary.Matches, static m => Assert.Equal(ChannelMatchOutcome.Failed, m.Outcome));
        Assert.Contains("no placement", summary.Matches[0].Failure);
        Assert.Contains("consecutive", summary.StopReason);
    }

    [Fact]
    public async Task RepeatedIncompleteMatchesAlsoStopTheChannel()
    {
        // A seat whose game crashes at startup every match, or telemetry that
        // never connects, records Incomplete, not Failed: it must not run
        // unattended to the match budget.
        ChannelRunner runner = new(new FakeLauncher(_ => Task.FromResult(new ChannelMatchResult(Evidence(complete: true), TelemetryObserved: 0))), new FakeProduction(), new MemorySink(), NoDelay);

        ChannelSessionSummary summary = await runner.RunAsync(Request(5));

        Assert.Equal(ChannelRequest.MaximumConsecutiveFailures, summary.Matches.Count);
        Assert.All(summary.Matches, static m => Assert.Equal(ChannelMatchOutcome.Incomplete, m.Outcome));
        Assert.Contains("consecutive", summary.StopReason);
    }

    [Fact]
    public async Task AStartupCrashIsRetriedOnceThenTheChannelContinues()
    {
        int call = 0;
        FakeLauncher launcher = new(_ => Task.FromResult(++call switch
        {
            1 => new ChannelMatchResult(StartupCrashEvidence(), TelemetryObserved: 0),
            2 => new ChannelMatchResult(Evidence(complete: true)),
            _ => new ChannelMatchResult(Evidence(complete: true)),
        }));
        ChannelRunner runner = new(launcher, new FakeProduction(), new MemorySink(), NoDelay);

        ChannelSessionSummary summary = await runner.RunAsync(Request(2));

        Assert.Equal(3, summary.Matches.Count);
        Assert.Equal(1, summary.Matches[0].MatchIndex);
        Assert.Equal(1, summary.Matches[0].Attempt);
        Assert.Equal("startup_crash", summary.Matches[0].FailureClass);
        Assert.Equal(ChannelMatchOutcome.Incomplete, summary.Matches[0].Outcome);
        Assert.Contains("saw no events", summary.Matches[0].Failure);

        Assert.Equal(1, summary.Matches[1].MatchIndex);
        Assert.Equal(2, summary.Matches[1].Attempt);
        Assert.Null(summary.Matches[1].FailureClass);
        Assert.Equal(ChannelMatchOutcome.Completed, summary.Matches[1].Outcome);

        // The retry's success reset the run, so the second logical match played
        // as usual and the channel never drained on consecutive failures.
        Assert.Equal(2, summary.Matches[2].MatchIndex);
        Assert.Equal(1, summary.Matches[2].Attempt);
        Assert.Equal(ChannelMatchOutcome.Completed, summary.Matches[2].Outcome);
        Assert.Equal("match budget reached", summary.StopReason);
    }

    [Fact]
    public async Task MaximumMatchesCountsMatchesNotAttempts()
    {
        int call = 0;
        FakeLauncher launcher = new(_ => Task.FromResult(++call == 1
            ? new ChannelMatchResult(StartupCrashEvidence(), TelemetryObserved: 0)
            : new ChannelMatchResult(Evidence(complete: true))));
        ChannelRunner runner = new(launcher, new FakeProduction(), new MemorySink(), NoDelay);

        ChannelSessionSummary summary = await runner.RunAsync(Request(1));

        Assert.Equal(2, summary.Matches.Count);
        Assert.All(summary.Matches, static m => Assert.Equal(1, m.MatchIndex));
        Assert.Equal("match budget reached", summary.StopReason);
    }

    [Fact]
    public async Task ACrashAfterTheMatchStartedIsNotRetried()
    {
        // Telemetry observed events, so the match did start; a crash from that
        // point on is an ordinary incomplete match, not a startup crash.
        FakeLauncher launcher = new(_ => Task.FromResult(new ChannelMatchResult(StartupCrashEvidence(), TelemetryObserved: 5)));
        ChannelRunner runner = new(launcher, new FakeProduction(), new MemorySink(), NoDelay);

        ChannelSessionSummary summary = await runner.RunAsync(Request(1));

        Assert.Single(summary.Matches);
        Assert.Null(summary.Matches[0].FailureClass);
        Assert.Equal(1, summary.Matches[0].Attempt);
        Assert.Equal(ChannelMatchOutcome.Incomplete, summary.Matches[0].Outcome);
    }

    [Fact]
    public async Task NoAttachedTelemetryIsNeverClassifiedOrRetried()
    {
        // Fail closed: without telemetry attached there is nothing to
        // classify the crash from, so the match is not retried.
        FakeLauncher launcher = new(_ => Task.FromResult(new ChannelMatchResult(StartupCrashEvidence())));
        ChannelRunner runner = new(launcher, new FakeProduction(), new MemorySink(), NoDelay);

        ChannelSessionSummary summary = await runner.RunAsync(Request(1));

        Assert.Single(summary.Matches);
        Assert.Null(summary.Matches[0].FailureClass);
        Assert.Equal(1, summary.Matches[0].Attempt);
    }

    [Fact]
    public async Task TwoMatchesThatEachCrashTwiceDrainTheChannel()
    {
        FakeLauncher launcher = new(_ => Task.FromResult(new ChannelMatchResult(StartupCrashEvidence(), TelemetryObserved: 0)));
        ChannelRunner runner = new(launcher, new FakeProduction(), new MemorySink(), NoDelay);

        ChannelSessionSummary summary = await runner.RunAsync(Request(5));

        Assert.Equal(4, summary.Matches.Count);
        Assert.Equal([1, 1, 2, 2], summary.Matches.Select(static m => m.MatchIndex));
        Assert.Equal([1, 2, 1, 2], summary.Matches.Select(static m => m.Attempt));
        Assert.All(summary.Matches, static m => Assert.Equal(ChannelMatchOutcome.Incomplete, m.Outcome));
        Assert.Contains("consecutive", summary.StopReason);
    }

    [Fact]
    public async Task ADrainDuringTheRetryDelayStopsBeforeTheSecondAttempt()
    {
        ChannelRunner? runner = null;
        FakeLauncher launcher = new(_ => Task.FromResult(new ChannelMatchResult(StartupCrashEvidence(), TelemetryObserved: 0)));
        runner = new ChannelRunner(launcher, new FakeProduction(), new MemorySink(), (_, _) => { runner!.RequestDrain(); return Task.CompletedTask; });

        ChannelSessionSummary summary = await runner.RunAsync(Request(5));

        Assert.Equal(1, Assert.Single(summary.Matches).Attempt);
        Assert.Equal("drain requested", summary.StopReason);
    }

    [Fact]
    public async Task AStartupCrashIsNotRetriedWhileAClientsExitIsUnobserved()
    {
        // The launch agent starts a new game even while an earlier one runs, so
        // a client whose process was never seen to exit may still be playing:
        // a retry would put a second game beside it.
        FakeLauncher launcher = new(_ => Task.FromResult(new ChannelMatchResult(StartupCrashEvidence(secondExitUnobserved: true), TelemetryObserved: 0)));
        ChannelRunner runner = new(launcher, new FakeProduction(), new MemorySink(), NoDelay);

        ChannelSessionSummary summary = await runner.RunAsync(Request(1));

        ChannelMatchRecord match = Assert.Single(summary.Matches);
        Assert.Null(match.FailureClass);
        Assert.Equal(1, match.Attempt);
    }

    [Fact]
    public async Task ADrainDuringTheRetryDelayIsTheStopReasonAfterAnEarlierFailure()
    {
        // An ordinary failed match, then a startup crash drained before its
        // retry: the channel stopped because of the drain, not two failures.
        ChannelRunner? runner = null;
        int launches = 0;
        FakeLauncher launcher = new(_ => Task.FromResult(++launches == 1
            ? new ChannelMatchResult(Evidence(complete: false), TelemetryObserved: 5)
            : new ChannelMatchResult(StartupCrashEvidence(), TelemetryObserved: 0)));
        int delays = 0;
        runner = new ChannelRunner(launcher, new FakeProduction(), new MemorySink(), (_, _) =>
        {
            // The first pause is the holding between matches; the second is the retry's.
            if (++delays == 2) runner!.RequestDrain();
            return Task.CompletedTask;
        });

        ChannelSessionSummary summary = await runner.RunAsync(Request(5));

        Assert.Equal([1, 1], summary.Matches.Select(static m => m.Attempt));
        Assert.Equal("drain requested", summary.StopReason);
    }

    [Fact]
    public void TheReportCountsOnlyRetriesThatWerePlayed()
    {
        // A startup crash drained before its retry was not retried.
        ChannelMatchContext context = new("channel-1", 1, "MAP01.MAP", playerView, null);
        ChannelMatchRecord crash = ChannelRunner.FromResult(Request(1), context, DateTimeOffset.UtcNow, new ChannelMatchResult(StartupCrashEvidence(), TelemetryObserved: 0));

        ExperimentRow row = Assert.Single(ChannelExperimentReport.Build([crash]));

        Assert.Equal(1, row.Matches);
        Assert.Equal(0, row.StartupCrashesRetried);
    }

    [Fact]
    public void TheReportCountsMatchesAndRetriesNotAttempts()
    {
        // One match that crashed at startup twice: one match, one retry.
        ChannelMatchContext context = new("channel-1", 1, "MAP01.MAP", playerView, null);
        ChannelMatchRecord first = ChannelRunner.FromResult(Request(1), context, DateTimeOffset.UtcNow, new ChannelMatchResult(StartupCrashEvidence(), TelemetryObserved: 0));
        ChannelMatchRecord second = ChannelRunner.FromResult(Request(1), context with { Attempt = 2 }, DateTimeOffset.UtcNow, new ChannelMatchResult(StartupCrashEvidence(), TelemetryObserved: 0));

        ExperimentRow row = Assert.Single(ChannelExperimentReport.Build([first, second]));

        Assert.Equal(1, row.Matches);
        Assert.Equal(1, row.StartupCrashesRetried);
    }

    [Fact]
    public void EachAttemptHasItsOwnEvidenceFolder()
    {
        // A retry must not overwrite the crashed attempt's evidence.
        ChannelMatchContext context = new("channel-1", 1, "MAP01.MAP", playerView, null);

        Assert.Equal("match-001", context.EvidenceFolderName);
        Assert.Equal("match-001-attempt2", (context with { Attempt = 2 }).EvidenceFolderName);
    }

    [Fact]
    public async Task ACompletedMatchResetsTheRun()
    {
        int call = 0;
        ChannelRunner runner = new(new FakeLauncher(_ => Task.FromResult(++call % 2 == 1
            ? new ChannelMatchResult(Evidence(complete: true), TelemetryObserved: 0)
            : new ChannelMatchResult(Evidence(complete: true)))), new FakeProduction(), new MemorySink(), NoDelay);

        ChannelSessionSummary summary = await runner.RunAsync(Request(4));

        Assert.Equal(4, summary.Matches.Count);
    }

    [Fact]
    public async Task DrainLetsTheCurrentMatchFinish()
    {
        ChannelRunner? runner = null;
        FakeLauncher launcher = new(_ =>
        {
            runner!.RequestDrain();
            return Task.FromResult(new ChannelMatchResult(Evidence(complete: true)));
        });
        runner = new ChannelRunner(launcher, new FakeProduction(), new MemorySink(), NoDelay);

        ChannelSessionSummary summary = await runner.RunAsync(Request(5));

        Assert.Single(summary.Matches);
        Assert.Equal("drain requested", summary.StopReason);
    }

    [Fact]
    public async Task BroadcastFailureDoesNotStopTheMatch()
    {
        FakeProduction production = new() { FailSceneSwitches = true };
        ChannelRunner runner = new(new FakeLauncher(_ => Task.FromResult(new ChannelMatchResult(Evidence(complete: true)))), production, new MemorySink(), NoDelay);

        ChannelSessionSummary summary = await runner.RunAsync(Request(1));

        Assert.Equal(ChannelMatchOutcome.Completed, Assert.Single(summary.Matches).Outcome);
        Assert.NotEmpty(runner.BroadcastIssues);
    }

    [Fact]
    public async Task CancellationStillStopsTheOutput()
    {
        using CancellationTokenSource cancel = new();
        FakeProduction production = new();
        FakeLauncher launcher = new(ct =>
        {
            cancel.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ChannelMatchResult(null));
        });
        ChannelRunner runner = new(launcher, production, new MemorySink(), NoDelay);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(Request(3), cancel.Token));

        Assert.Equal("stop", production.Calls[^1]);
        Assert.Equal(ChannelPhase.Stopped, runner.Phase);
    }

    [Fact]
    public void DesyncOrIncompleteLifecycleIsNotACompletedMatch()
    {
        ChannelMatchContext context = new("channel-1", 1, "MAP01.MAP", playerView, null);
        ChannelMatchRecord incomplete = ChannelRunner.FromResult(Request(1), context, DateTimeOffset.UtcNow, new ChannelMatchResult(Evidence(complete: false)));
        Assert.Equal(ChannelMatchOutcome.Incomplete, incomplete.Outcome);

        ChannelMatchRecord desync = ChannelRunner.FromResult(Request(1), context, DateTimeOffset.UtcNow, new ChannelMatchResult(Evidence(complete: true, desync: true)));
        Assert.Equal(ChannelMatchOutcome.Incomplete, desync.Outcome);
        Assert.Contains("desynchron", desync.Failure);
        Assert.Null(desync.Winner);
    }

    [Fact]
    public void AMatchWhoseLiveTelemetrySawNothingIsNotCompleted()
    {
        // Lab run 20260928-111904-s2: gamemd aborted on frame 0 (exit 3), the
        // control plane still wound down cleanly, and the channel recorded
        // "completed" with zero telemetry events. Attached telemetry that saw
        // nothing means no match was played.
        ChannelMatchContext context = new("channel-1", 1, "MAP01.MAP", playerView, null);
        ChannelMatchRecord record = ChannelRunner.FromResult(Request(1), context, DateTimeOffset.UtcNow,
            new ChannelMatchResult(Evidence(complete: true), TelemetryObserved: 0, TelemetryEnded: false));

        Assert.Equal(ChannelMatchOutcome.Incomplete, record.Outcome);
        Assert.Contains("saw no events", record.Failure);
    }

    [Fact]
    public void AnEmptyTelemetryStreamCarriesTheSourcesOwnIssue()
    {
        // Zero events does not prove the game quit: a source that failed
        // (or a wrong endpoint) also sees nothing. Say what was seen, and why
        // if the source knows.
        ChannelMatchContext context = new("channel-1", 1, "MAP01.MAP", playerView, null);
        ChannelMatchRecord record = ChannelRunner.FromResult(Request(1), context, DateTimeOffset.UtcNow,
            new ChannelMatchResult(Evidence(complete: true), TelemetryObserved: 0, TelemetryEnded: false, TelemetryIssue: "returned no object type classes 5 times"));

        Assert.Equal(ChannelMatchOutcome.Incomplete, record.Outcome);
        Assert.Contains("saw no events", record.Failure);
        Assert.Contains("returned no object type classes", record.Failure);
        Assert.DoesNotContain("frame", record.Failure);
    }

    [Fact]
    public void AMatchWithoutAttachedTelemetryIsJudgedOnTheLifecycleAlone()
    {
        ChannelMatchContext context = new("channel-1", 1, "MAP01.MAP", playerView, null);
        ChannelMatchRecord record = ChannelRunner.FromResult(Request(1), context, DateTimeOffset.UtcNow, new ChannelMatchResult(Evidence(complete: true)));

        Assert.Equal(ChannelMatchOutcome.Completed, record.Outcome);
    }

    [Fact]
    public void AFailedObserverDegradesTheViewButNotTheMatch()
    {
        ChannelMatchContext context = new("channel-1", 1, "MAP01.MAP", playerView, null);
        ChannelMatchRecord record = ChannelRunner.FromResult(Request(1), context, DateTimeOffset.UtcNow, new ChannelMatchResult(Evidence(complete: true, observerFailure: "desync: the game wrote SYNC0.TXT")));

        Assert.Equal(ChannelMatchOutcome.Completed, record.Outcome);
        Assert.Null(record.Failure);
        Assert.Contains("SYNC0", record.ObserverIssue);
    }

    [Fact]
    public async Task NdjsonSinkAppendsOneLinePerMatch()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bindery-channel-" + Guid.NewGuid().ToString("N"));
        try
        {
            NdjsonChannelRecordSink sink = new(directory);
            ChannelMatchContext context = new("channel-1", 1, "MAP01.MAP", playerView, null);
            ChannelMatchRecord record = ChannelRunner.FromResult(Request(1, WithTwitch(true)), context, DateTimeOffset.UtcNow, new ChannelMatchResult(Evidence(complete: true)));
            await sink.WriteAsync(record, CancellationToken.None);
            await sink.WriteAsync(record with { MatchIndex = 2 }, CancellationToken.None);

            string[] lines = await File.ReadAllLinesAsync(sink.Path);
            Assert.Equal(2, lines.Length);
            using JsonDocument first = JsonDocument.Parse(lines[0]);
            Assert.Equal("completed", first.RootElement.GetProperty("outcome").GetString());
            Assert.True(first.RootElement.GetProperty("publishedPublicly").GetBoolean());
            Assert.DoesNotContain("TWITCH_STREAM_KEY", lines[0], StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ObsAuthenticationFollowsTheV5Scheme()
    {
        string expectedSecret = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData("supersecretsalt"u8.ToArray()));
        string expected = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expectedSecret + "challenge")));
        Assert.Equal(expected, ObsWebSocketProduction.Authentication("supersecret", "salt", "challenge"));
    }

    [Fact]
    public async Task ObsProductionIdentifiesThenSwitchesScenesAndStreams()
    {
        FakeObsConnection obs = new(authenticated: true, outputActive: false);
        await using ObsWebSocketProduction production = new(_ => Task.FromResult<IObsConnection>(obs), "pw", new Dictionary<string, string> { ["instance-a"] = "client-a" });

        await production.StartAsync(LocalOnly(), CancellationToken.None);
        await production.ShowMatchAsync(playerView, CancellationToken.None);
        await production.ShowMatchAsync(new CaptureSource("observer-1", ClientClass.Observer), CancellationToken.None);
        await production.ShowHoldingAsync("between", CancellationToken.None);

        JsonNode identify = obs.Sent[0];
        Assert.Equal(1, identify["op"]!.GetValue<int>());
        Assert.Equal(ObsWebSocketProduction.Authentication("pw", "s", "c"), identify["d"]!["authentication"]!.GetValue<string>());
        Assert.Equal(
            ["GetSceneList", "SetCurrentProgramScene:ra2-holding", "GetStreamStatus", "StartStream", "SetCurrentProgramScene:client-a", "SetCurrentProgramScene:ra2-match", "SetCurrentProgramScene:ra2-holding"],
            obs.Requests);
    }

    [Fact]
    public async Task ObsProductionReportsRejectedRequests()
    {
        FakeObsConnection obs = new(authenticated: false, outputActive: true) { RejectScenes = true };
        await using ObsWebSocketProduction production = new(_ => Task.FromResult<IObsConnection>(obs), null);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => production.StartAsync(LocalOnly(), CancellationToken.None));
        Assert.Contains("600", error.Message);
    }

    [Fact]
    public async Task ObsPreflightRefusesMissingScenesAndMutedAudio()
    {
        FakeObsConnection obs = new(authenticated: false, outputActive: false)
        {
            Scenes = ["ra2-holding"],
            Inputs = new() { ["game-audio"] = true },
        };
        await using ObsWebSocketProduction production = new(_ => Task.FromResult<IObsConnection>(obs), null, requiredAudioInputs: ["game-audio", "commentary"]);

        IReadOnlyList<string> problems = await production.PreflightAsync(LocalOnly(), CancellationToken.None);

        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, static p => p.Contains("'ra2-match' does not exist", StringComparison.Ordinal));
        Assert.Contains(problems, static p => p.Contains("'game-audio' is muted", StringComparison.Ordinal));
        Assert.Contains(problems, static p => p.Contains("'commentary' is unavailable", StringComparison.Ordinal));
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => production.StartAsync(LocalOnly(), CancellationToken.None));
        Assert.StartsWith("OBS is not ready", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("StartStream", obs.Requests);
    }

    [Fact]
    public void FilterAdmitsOnlyWhatTheHouseMayKnow()
    {
        PlayerObservationFilter filter = new("Americans");

        Assert.True(filter.Admits(Observation(Ra2TelemetryEventTypes.PlayerDefeated, "{\"house\":\"Soviets\"}")));
        Assert.True(filter.Admits(Observation(Ra2TelemetryEventTypes.CreditsSampled, "{\"house\":\"Americans\",\"credits\":5000}")));
        Assert.False(filter.Admits(Observation(Ra2TelemetryEventTypes.CreditsSampled, "{\"house\":\"Soviets\",\"credits\":5000}")));
        Assert.False(filter.Admits(Observation(Ra2TelemetryEventTypes.UnitCreated, "{\"unit_id\":7}")));
        Assert.True(filter.Admits(Observation(Ra2TelemetryEventTypes.UnitCreated, "{\"house\":\"Soviets\",\"visible_to\":[\"Americans\"]}")));
        Assert.False(filter.Admits(Observation(Ra2TelemetryEventTypes.OrderIssued, "[1,2]")));
    }

    [Fact]
    public void SeatRejectsASinkForAnotherHouse()
    {
        Assert.Throws<ArgumentException>(() => new AgentSeat(new ScriptedController(), new MemoryCommands("Soviets"), new PlayerObservationFilter("Americans")));
    }

    [Fact]
    public async Task SeatTracesDecisionsAndStopsAtMatchEnd()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bindery-seat-" + Guid.NewGuid().ToString("N"));
        try
        {
            ScriptedController controller = new();
            MemoryCommands commands = new("Americans") { RejectKind = "bad" };
            AgentSeat seat = new(controller, commands, new PlayerObservationFilter("Americans"));
            FakeTelemetry telemetry = new(
            [
                Observation(Ra2TelemetryEventTypes.MatchStarted, "{}"),
                Observation(Ra2TelemetryEventTypes.CreditsSampled, "{\"house\":\"Soviets\",\"credits\":9000}"),
                Observation(Ra2TelemetryEventTypes.CreditsSampled, "{\"house\":\"Americans\",\"credits\":100}"),
                Observation(Ra2TelemetryEventTypes.MatchEnded, "{}"),
                Observation(Ra2TelemetryEventTypes.CreditsSampled, "{\"house\":\"Americans\",\"credits\":1}"),
            ]);

            AgentSeatSummary summary = await seat.RunAsync(telemetry, directory);

            Assert.Equal(3, summary.ObservationsAdmitted);
            Assert.Equal(1, summary.ObservationsWithheld);
            Assert.DoesNotContain(controller.Seen, static o => o.Payload.ToString().Contains("Soviets", StringComparison.Ordinal));
            Assert.Equal(1, summary.CommandsSent);
            Assert.Equal(1, summary.CommandsFailed);
            Assert.Equal("economy stalled", Assert.Single(summary.Revisions).Trigger);

            string[] kinds = (await File.ReadAllLinesAsync(summary.TracePath))
                .Select(static line => JsonDocument.Parse(line).RootElement.GetProperty("kind").GetString()!)
                .ToArray();
            Assert.Equal(["seat_opened", "playbook_revised", "command_sent", "command_failed", "seat_closed"], kinds);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    private static ulong nextSequence;

    internal static RawObservation Observation(string type, string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        ulong sequence = Interlocked.Increment(ref nextSequence);
        return new RawObservation($"event-{sequence}", "capture-1", sequence, type, Ra2LabProfile.AdapterId, Ra2LabProfile.AdapterVersion, DateTimeOffset.UtcNow, document.RootElement.Clone(), "sha256:raw");
    }

    /// <summary>
    /// Evidence for a match whose control plane wound down with no telemetry
    /// observed, and whose first player's debugger log named an NTSTATUS exit
    /// -- the shape of the intermittent DDrawCompat STATUS_STACK_OVERFLOW seen
    /// at startup in the lab (~2 of 20 launches).
    /// </summary>
    internal static LiveAcceptanceEvidence StartupCrashEvidence(bool secondExitUnobserved = false)
    {
        LiveClientEvidence Client(string id, int? exitCode, string? failure, IReadOnlyList<RunObservation> observations) =>
            new(id, "account-" + id, "instance-" + id, "golden-1", "sha256:game", "sha256:ini", ["ready"], exitCode, failure, observations, "player");
        LiveClientEvidence[] clients =
        [
            Client("a", 3, null, [new RunObservation(RunObservation.HostedExitCode, "C00000FD -- an exception status: the game crashed", Notable: true)]),
            secondExitUnobserved
                ? Client("b", null, "InvalidOperationException: remote client status poll failed", [])
                : Client("b", 0, null, []),
        ];
        return new LiveAcceptanceEvidence(
            LiveAcceptanceRunner.EvidenceSchemaVersion,
            Guid.NewGuid().ToString("D"),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "session-1",
            "golden-1",
            new LiveRelayEvidence("cncnet-private", "allocation-1", "192.168.122.1:50000", false, null),
            new LiveTelemetryEvidence(Ra2LabProfile.TelemetryProtocol, "127.0.0.1:14521", false, null),
            clients,
            "ended",
            ["departed", "departed"],
            new LiveQualificationFlags(false, false, false, false, false, false),
            [],
            "MAP01.MAP",
            4242);
    }

    internal static LiveAcceptanceEvidence Evidence(bool complete, bool desync = false, string? observerFailure = null)
    {
        IReadOnlyList<RunObservation> observations = desync ? [new RunObservation(RunObservation.Desync, "the game wrote SYNC0.TXT", true)] : [];
        LiveClientEvidence Client(string id) => new(id, "account-" + id, "instance-" + id, "golden-1", "sha256:game", "sha256:ini", ["ready", "started", "exited"], 0, null, observations, "player");
        LiveClientEvidence[] clients = observerFailure is null
            ? [Client("a"), Client("b")]
            : [Client("a"), Client("b"), new("o", "account-o", "observer-1", "golden-1", "sha256:game", "sha256:ini", ["ready", "failed"], 0, observerFailure, [], "observer")];
        return new LiveAcceptanceEvidence(
            LiveAcceptanceRunner.EvidenceSchemaVersion,
            Guid.NewGuid().ToString("D"),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "session-1",
            "golden-1",
            new LiveRelayEvidence("cncnet-private", "allocation-1", "192.168.122.1:50000", false, null),
            new LiveTelemetryEvidence(Ra2LabProfile.TelemetryProtocol, "127.0.0.1:14521", false, null),
            clients,
            "ended",
            ["departed", "departed"],
            new LiveQualificationFlags(complete, false, false, false, false, false),
            [],
            "MAP01.MAP",
            4242,
            observerFailure is null ? null : true);
    }

    private sealed class FakeLauncher(Func<CancellationToken, Task<ChannelMatchResult>> play) : IChannelMatchLauncher
    {
        public async Task<ChannelMatchResult> RunMatchAsync(ChannelMatchContext context, Func<CancellationToken, Task> onAir, CancellationToken cancellationToken)
        {
            await onAir(cancellationToken);
            return await play(cancellationToken);
        }
    }

    private sealed class MemorySink : IChannelRecordSink
    {
        public List<ChannelMatchRecord> Records { get; } = [];

        public Task WriteAsync(ChannelMatchRecord record, CancellationToken cancellationToken)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProduction : IBroadcastProduction
    {
        public List<string> Calls { get; } = [];

        public bool FailSceneSwitches { get; init; }

        public Task StartAsync(BroadcastPlan plan, CancellationToken cancellationToken)
        {
            Calls.Add("start");
            return Task.CompletedTask;
        }

        public Task ShowHoldingAsync(string reason, CancellationToken cancellationToken) => Scene("holding");

        public Task ShowMatchAsync(CaptureSource source, CancellationToken cancellationToken) => Scene("match:" + source.ClientInstanceId);

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Calls.Add("stop");
            return Task.CompletedTask;
        }

        private Task Scene(string name)
        {
            Calls.Add(name);
            return FailSceneSwitches ? throw new InvalidOperationException("OBS is not answering") : Task.CompletedTask;
        }
    }

    private sealed class FakeObsConnection(bool authenticated, bool outputActive) : IObsConnection
    {
        private readonly Queue<string> inbound = new(
        [
            authenticated
                ? "{\"op\":0,\"d\":{\"rpcVersion\":1,\"authentication\":{\"salt\":\"s\",\"challenge\":\"c\"}}}"
                : "{\"op\":0,\"d\":{\"rpcVersion\":1}}",
            "{\"op\":2,\"d\":{\"negotiatedRpcVersion\":1}}",
        ]);

        public List<JsonNode> Sent { get; } = [];

        public List<string> Requests { get; } = [];

        public bool RejectScenes { get; init; }

        public string[] Scenes { get; init; } = ["ra2-holding", "ra2-match", "client-a"];

        public Dictionary<string, bool> Inputs { get; init; } = new() { ["game-audio"] = false };

        public Task SendAsync(string message, CancellationToken cancellationToken)
        {
            JsonNode node = JsonNode.Parse(message)!;
            Sent.Add(node);
            if (node["op"]!.GetValue<int>() != 6) return Task.CompletedTask;
            JsonNode d = node["d"]!;
            string type = d["requestType"]!.GetValue<string>();
            string id = d["requestId"]!.GetValue<string>();
            string? scene = d["requestData"]?["sceneName"]?.GetValue<string>();
            Requests.Add(scene is null ? type : $"{type}:{scene}");
            // An unrelated event first: the client must skip it.
            inbound.Enqueue("{\"op\":5,\"d\":{\"eventType\":\"StreamStateChanged\"}}");
            bool ok = !(RejectScenes && scene is not null);
            JsonObject response = new()
            {
                ["requestType"] = type,
                ["requestId"] = id,
                ["requestStatus"] = ok ? new JsonObject { ["result"] = true, ["code"] = 100 } : new JsonObject { ["result"] = false, ["code"] = 600, ["comment"] = "No scene" },
            };
            if (type == "GetStreamStatus") response["responseData"] = new JsonObject { ["outputActive"] = outputActive };
            if (type == "GetSceneList")
                response["responseData"] = new JsonObject { ["scenes"] = new JsonArray(Scenes.Select(static n => (JsonNode)new JsonObject { ["sceneName"] = n }).ToArray()) };
            if (type == "GetInputMute")
            {
                string input = d["requestData"]!["inputName"]!.GetValue<string>();
                if (!Inputs.TryGetValue(input, out bool muted))
                    response["requestStatus"] = new JsonObject { ["result"] = false, ["code"] = 600, ["comment"] = "No source was found" };
                else
                    response["responseData"] = new JsonObject { ["inputMuted"] = muted };
            }
            inbound.Enqueue(new JsonObject { ["op"] = 7, ["d"] = response }.ToJsonString());
            return Task.CompletedTask;
        }

        public Task<string> ReceiveAsync(CancellationToken cancellationToken) => Task.FromResult(inbound.Dequeue());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class ScriptedController : IPlayerController
    {
        public List<RawObservation> Seen { get; } = [];

        public string ControllerId => "scripted";

        public string ControllerVersion => "0.0.1";

        public Task<ControllerStep> ObserveAsync(RawObservation observation, CancellationToken cancellationToken)
        {
            Seen.Add(observation);
            if (observation.EventType != Ra2TelemetryEventTypes.CreditsSampled) return Task.FromResult(ControllerStep.None);
            using JsonDocument args = JsonDocument.Parse("{\"structure\":\"refinery\"}");
            return Task.FromResult(new ControllerStep(
                [new PlayerCommand("build", args.RootElement.Clone()), new PlayerCommand("bad", args.RootElement.Clone())],
                new PlaybookRevision("economy stalled", "expand to a second refinery")));
        }
    }

    internal sealed class MemoryCommands(string house) : IPlayerCommandSink
    {
        public string House { get; } = house;

        public string? RejectKind { get; init; }

        public Task SendAsync(PlayerCommand command, CancellationToken cancellationToken) =>
            command.Kind == RejectKind ? throw new InvalidOperationException("rejected") : Task.CompletedTask;
    }

    internal sealed class FakeTelemetry(IReadOnlyList<RawObservation> observations) : IRa2TelemetrySource
    {
        public Ra2TelemetryCapture Capture { get; } = new(Ra2LabProfile.TelemetryProtocol, new Ra2YrcppEndpoint("127.0.0.1", 14521), true, observations.Count, null);

        public async IAsyncEnumerable<RawObservation> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (RawObservation observation in observations)
            {
                await Task.Yield();
                yield return observation;
            }
        }
    }
}
