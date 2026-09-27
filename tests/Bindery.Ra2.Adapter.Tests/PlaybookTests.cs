// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Adapter;
using Bindery.Ra2.Adapter.Channel;
using Xunit;

namespace Bindery.Ra2.Adapter.Tests;

public sealed class PlaybookTests
{
    private static readonly DateTimeOffset start = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static ulong sequence;

    [Fact]
    public async Task OpeningPlansImmediatelyAndRoutineRunsOnTheNewPlaybook()
    {
        RecordingRoutine routine = new();
        PlaybookController controller = new("Americans", new RulePlaybookPlanner(), routine);

        ControllerStep step = await controller.ObserveAsync(At(0, Ra2TelemetryEventTypes.MatchStarted, "{}"), CancellationToken.None);

        Assert.Equal("opening", step.Revision!.Trigger);
        Assert.StartsWith("r1: ", step.Revision.Playbook, StringComparison.Ordinal);
        Assert.Equal("scout", controller.Current.Directive("posture"));
        Assert.Equal(1, routine.LastRevision);
        Assert.Contains(step.Notes!, static n => n.StartsWith("trigger opening", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThreatsRespectTheCooldownAndThePlanInterval()
    {
        PlaybookController controller = new("Americans", new RulePlaybookPlanner(), minimumPlanInterval: TimeSpan.FromSeconds(20));
        await controller.ObserveAsync(At(0, Ra2TelemetryEventTypes.MatchStarted, "{}"), CancellationToken.None);

        ControllerStep tooSoon = await controller.ObserveAsync(At(10, Ra2TelemetryEventTypes.UnitCreated, "{\"house\":\"Soviets\",\"visible_to\":[\"Americans\"],\"type\":\"HTNK\"}"), CancellationToken.None);
        Assert.Null(tooSoon.Revision);
        Assert.Contains(tooSoon.Notes!, static n => n.Contains("dropped: last plan", StringComparison.Ordinal));

        // The threat trigger's own 60 s cooldown started at t=10, so t=40 is silent...
        ControllerStep cooling = await controller.ObserveAsync(At(40, Ra2TelemetryEventTypes.UnitCreated, "{\"house\":\"Soviets\",\"type\":\"HTNK\"}"), CancellationToken.None);
        Assert.Null(cooling.Notes);
        // ...and t=75 fires and plans.
        ControllerStep threat = await controller.ObserveAsync(At(75, Ra2TelemetryEventTypes.UnitCreated, "{\"house\":\"Soviets\",\"type\":\"HTNK\"}"), CancellationToken.None);
        Assert.Equal("new_threat", threat.Revision!.Trigger);
        Assert.Equal("defend", controller.Current.Directive("posture"));
        Assert.Equal(3, controller.View.Summarize().EnemySightings);
    }

    [Fact]
    public async Task AStalledEconomyFiresOnceAndReArmsAfterRecovery()
    {
        PlaybookController controller = new("Americans", new RulePlaybookPlanner(), minimumPlanInterval: TimeSpan.Zero);
        List<string> triggers = [];
        async Task Credits(int seconds, long credits)
        {
            ControllerStep step = await controller.ObserveAsync(At(seconds, Ra2TelemetryEventTypes.CreditsSampled, $"{{\"house\":\"Americans\",\"credits\":{credits}}}"), CancellationToken.None);
            if (step.Revision is { } revision) triggers.Add(revision.Trigger);
        }

        await Credits(0, 5000);
        await Credits(60, 5000);
        await Credits(95, 4800); // 95 s of no growth: stalled
        await Credits(120, 4700); // still stalled, already fired
        await Credits(200, 9000); // recovered: re-arms
        await Credits(300, 9000); // stalled again

        Assert.Equal(["stalled_economy", "stalled_economy"], triggers);
        Assert.Equal("expand", controller.Current.Directive("economy"));
    }

    [Fact]
    public async Task TechTransitionFiresOncePerTechBuilding()
    {
        PlaybookController controller = new("Americans", new RulePlaybookPlanner(), minimumPlanInterval: TimeSpan.Zero);
        ControllerStep first = await controller.ObserveAsync(At(0, Ra2TelemetryEventTypes.BuildingPlaced, "{\"house\":\"Americans\",\"type\":\"GATECH\"}"), CancellationToken.None);
        ControllerStep again = await controller.ObserveAsync(At(5, Ra2TelemetryEventTypes.BuildingPlaced, "{\"house\":\"Americans\",\"type\":\"GATECH\"}"), CancellationToken.None);
        ControllerStep enemy = await controller.ObserveAsync(At(9, Ra2TelemetryEventTypes.BuildingPlaced, "{\"house\":\"Soviets\",\"type\":\"NATECH\"}"), CancellationToken.None);

        Assert.Equal("tech_transition", first.Revision!.Trigger);
        Assert.Null(again.Revision);
        Assert.NotEqual("tech_transition", enemy.Revision?.Trigger);
        Assert.Equal(["GATECH"], controller.View.Summarize().OwnBuildingTypes);
    }

    [Fact]
    public async Task ASlowPlanStaysInFlightWhileRoutineKeepsTheOldPlaybook()
    {
        GatedPlanner planner = new();
        RecordingRoutine routine = new();
        PlaybookController controller = new("Americans", planner, routine, minimumPlanInterval: TimeSpan.Zero);

        ControllerStep opening = await controller.ObserveAsync(At(0, Ra2TelemetryEventTypes.MatchStarted, "{}"), CancellationToken.None);
        Assert.Null(opening.Revision);
        Assert.Equal(0, routine.LastRevision);

        ControllerStep busy = await controller.ObserveAsync(At(1, Ra2TelemetryEventTypes.BuildingPlaced, "{\"house\":\"Americans\",\"type\":\"GATECH\"}"), CancellationToken.None);
        Assert.Contains(busy.Notes!, static n => n.Contains("in flight", StringComparison.Ordinal));

        planner.Release(new Playbook(1, "slow plan", new Dictionary<string, string>()));
        ControllerStep arrived = await controller.ObserveAsync(At(2, Ra2TelemetryEventTypes.CreditsSampled, "{\"house\":\"Americans\",\"credits\":1}"), CancellationToken.None);
        Assert.Equal("opening", arrived.Revision!.Trigger);
        Assert.Equal(1, routine.LastRevision);
    }

    [Fact]
    public async Task AFailedPlanKeepsTheCurrentPlaybook()
    {
        PlaybookController controller = new("Americans", new FailingPlanner());
        ControllerStep step = await controller.ObserveAsync(At(0, Ra2TelemetryEventTypes.MatchStarted, "{}"), CancellationToken.None);

        Assert.Null(step.Revision);
        Assert.Contains(step.Notes!, static n => n.Contains("planner failed on opening: model unavailable", StringComparison.Ordinal));
        Assert.Equal(0, controller.Current.Revision);
    }

    [Fact]
    public void ViewTracksOnlyItsOwnHouse()
    {
        PlayerView view = new("Americans");
        view.Apply(At(0, Ra2TelemetryEventTypes.UnitCreated, "{\"house\":\"Americans\",\"type\":\"MTNK\"}"));
        view.Apply(At(1, Ra2TelemetryEventTypes.UnitCreated, "{\"house\":\"Americans\",\"type\":\"MTNK\"}"));
        view.Apply(At(2, Ra2TelemetryEventTypes.UnitDestroyed, "{\"house\":\"Americans\",\"type\":\"MTNK\"}"));
        view.Apply(At(3, Ra2TelemetryEventTypes.UnitCreated, "{\"house\":\"Soviets\",\"type\":\"HTNK\"}"));
        view.Apply(At(4, Ra2TelemetryEventTypes.CreditsSampled, "{\"house\":\"Soviets\",\"credits\":99999}"));
        view.Apply(At(5, Ra2TelemetryEventTypes.PlayerDefeated, "{\"house\":\"Soviets\"}"));

        PlayerViewSummary summary = view.Summarize();
        Assert.Equal(1, summary.OwnUnits["MTNK"]);
        Assert.False(summary.OwnUnits.ContainsKey("HTNK"));
        Assert.Null(summary.Credits);
        Assert.Equal(1, summary.EnemySightings);
        Assert.Equal(["Soviets"], summary.DefeatedHouses);
        Assert.Equal(TimeSpan.FromSeconds(5), summary.Elapsed);
    }

    [Fact]
    public async Task SeatTracesControllerNotes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bindery-playbook-" + Guid.NewGuid().ToString("N"));
        try
        {
            PlaybookController controller = new("Americans", new RulePlaybookPlanner());
            AgentSeat seat = new(controller, new ChannelTests.MemoryCommands("Americans"), new PlayerObservationFilter("Americans"));
            AgentSeatSummary summary = await seat.RunAsync(new ChannelTests.FakeTelemetry(
            [
                At(0, Ra2TelemetryEventTypes.MatchStarted, "{}"),
                At(1, Ra2TelemetryEventTypes.MatchEnded, "{}"),
            ]), directory);

            string[] kinds = (await File.ReadAllLinesAsync(summary.TracePath))
                .Select(static line => JsonDocument.Parse(line).RootElement.GetProperty("kind").GetString()!)
                .ToArray();
            Assert.Equal(["seat_opened", "controller_note", "playbook_revised", "seat_closed"], kinds);
            Assert.Equal("bindery.playbook/rules/v1", controller.ControllerId);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TheMcvRoutineDeploysTheHousesOwnMcvOnceAtMatchStart()
    {
        DeployMcvRoutineController routine = new();
        PlayerView view = new("Americans");
        List<PlayerCommand> commands = [];
        void Observe(RawObservation observation)
        {
            view.Apply(observation);
            commands.AddRange(routine.Decide(Playbook.Empty, view, observation));
        }

        Observe(At(0, Ra2TelemetryEventTypes.MatchStarted, "{}"));
        // Someone else's MCV and our own tank are not ours to deploy.
        Observe(At(0, Ra2TelemetryEventTypes.UnitCreated, "{\"house\":\"Soviets\",\"type\":\"SMCV\",\"object\":177}"));
        Observe(At(0, Ra2TelemetryEventTypes.UnitCreated, "{\"house\":\"Americans\",\"type\":\"MTNK\",\"object\":162}"));
        Observe(At(0, Ra2TelemetryEventTypes.UnitCreated, "{\"house\":\"Americans\",\"type\":\"AMCV\",\"object\":161}"));
        Observe(At(1, Ra2TelemetryEventTypes.CreditsSampled, "{\"house\":\"Americans\",\"credits\":10000}"));
        // A second MCV later in the match (bought, not the opening one) is left alone.
        Observe(At(300, Ra2TelemetryEventTypes.UnitCreated, "{\"house\":\"Americans\",\"type\":\"AMCV\",\"object\":170}"));

        PlayerCommand deploy = Assert.Single(commands);
        Assert.Equal(PlayerCommandKinds.Deploy, deploy.Kind);
        Assert.Equal([161u], deploy.Arguments.GetProperty("objects").EnumerateArray().Select(static o => o.GetUInt32()));
        Assert.False(deploy.Arguments.TryGetProperty("unique_ids", out _));
    }

    [Fact]
    public void TheMcvRoutinePinsTheMcvsStableIdWhenTheStreamHasOne()
    {
        DeployMcvRoutineController routine = new();
        PlayerView view = new("Americans");
        RawObservation started = At(0, Ra2TelemetryEventTypes.MatchStarted, "{}");
        RawObservation mcv = At(0, Ra2TelemetryEventTypes.UnitCreated, "{\"house\":\"Americans\",\"type\":\"AMCV\",\"object\":161,\"unique_id\":7001}");
        view.Apply(started);
        view.Apply(mcv);

        PlayerCommand deploy = Assert.Single(routine.Decide(Playbook.Empty, view, mcv));

        Assert.True(deploy.Arguments.TryGetProperty("unique_ids", out JsonElement ids));
        Assert.Equal([7001u], ids.EnumerateArray().Select(static o => o.GetUInt32()));
    }

    [Fact]
    public async Task APlaybookSeatWithTheMcvRoutineIssuesTheDeploy()
    {
        PlaybookController controller = new("Americans", new RulePlaybookPlanner(), new DeployMcvRoutineController());

        await controller.ObserveAsync(At(0, Ra2TelemetryEventTypes.MatchStarted, "{}"), CancellationToken.None);
        ControllerStep step = await controller.ObserveAsync(At(0, Ra2TelemetryEventTypes.UnitCreated, "{\"house\":\"Americans\",\"type\":\"AMCV\",\"object\":161}"), CancellationToken.None);

        Assert.Equal(PlayerCommandKinds.Deploy, Assert.Single(step.Commands).Kind);
    }

    private static RawObservation At(int seconds, string type, string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        ulong next = Interlocked.Increment(ref sequence);
        return new RawObservation($"pb-{next}", "capture-1", next, type, Ra2LabProfile.AdapterId, Ra2LabProfile.AdapterVersion, start.AddSeconds(seconds), document.RootElement.Clone(), "sha256:raw");
    }

    private sealed class RecordingRoutine : IRoutineController
    {
        public int LastRevision { get; private set; } = -1;

        public IReadOnlyList<PlayerCommand> Decide(Playbook playbook, PlayerView view, RawObservation observation)
        {
            LastRevision = playbook.Revision;
            return [];
        }
    }

    private sealed class GatedPlanner : IPlaybookPlanner
    {
        private readonly TaskCompletionSource<Playbook> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string PlannerId => "gated";

        public Task<Playbook> PlanAsync(PlanningRequest request, CancellationToken cancellationToken) => gate.Task;

        public void Release(Playbook playbook) => gate.SetResult(playbook);
    }

    private sealed class FailingPlanner : IPlaybookPlanner
    {
        public string PlannerId => "failing";

        public Task<Playbook> PlanAsync(PlanningRequest request, CancellationToken cancellationToken) =>
            Task.FromException<Playbook>(new InvalidOperationException("model unavailable"));
    }
}
