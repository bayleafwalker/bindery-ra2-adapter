// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arbitration;
using Bindery.Ra2.Bot.Runtime;
using Bindery.Ra2.Bot.Tests.Arbitration;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Runtime;

public sealed class BotRuntimeTests
{
    private static ScriptedStrategist Selector() =>
        new("selector", IntentSource.Selector, c => c.Features.Time.Seconds < 50
            ? Proposals.For(c, $"sel-{c.Features.SnapshotVersion}", "allied-boom", StrategicPosture.Boom, 0.5)
            : Proposals.For(c, $"sel-{c.Features.SnapshotVersion}", "allied-turtle", StrategicPosture.Turtle, 0.9));

    [Fact]
    public void Tick_runs_the_layers_and_returns_only_gated_commands()
    {
        using BotRuntime runtime = Runtimes.Create(Selector());
        IReadOnlyList<GameCommand> commands = runtime.Tick(Frames.At(0));

        Assert.Equal("allied-boom", runtime.ActiveIntent?.PlaybookId);
        Assert.NotNull(runtime.CurrentFeatures);
        // Credits 1000 cover one 900-credit tank; the second is over budget. The rogue's order on unit 4 is unleased.
        Assert.Single(commands.OfType<ProduceCommand>());
        AttackMoveCommand move = Assert.Single(commands.OfType<AttackMoveCommand>());
        Assert.Equal([new EntityId(2), new EntityId(3)], move.Units);
        Assert.Empty(commands.OfType<MoveCommand>());
        Assert.Equal(2, runtime.Metrics.CommandsDropped);
        Assert.Contains(runtime.LastDropped, d => d.Reason == CommandGate.BudgetInsufficient);
        Assert.Contains(runtime.LastDropped, d => d.Reason == CommandGate.LeaseMissing && d.Command.Controller == "rogue");
        Assert.Equal(2, runtime.Log.Records.Count(r => r.Kind == DecisionRecordKinds.CommandDropped));
    }

    [Fact]
    public void Ledger_reservations_never_exceed_credits_plus_forecast_income()
    {
        using BotRuntime runtime = Runtimes.Create(Selector());
        for (long f = 0; f < 60; f++)
        {
            runtime.Tick(Frames.At(f, credits: 1000));
            int limit = 1000 + (int)Math.Floor(600 * runtime.Ledger.Options.PlanningPeriodSeconds / 60);
            Assert.True(runtime.Ledger.TotalReserved + runtime.Ledger.TotalSpent <= limit);
        }
    }

    [Fact]
    public void Planner_reservations_are_honoured_and_unknown_pools_reported()
    {
        FakePlanner planner = new() { Reservations = new Dictionary<string, int> { ["Army"] = 100, ["navy"] = 50 } };
        using BotRuntime runtime = Runtimes.Create(Selector(), planner: planner);
        IReadOnlyList<GameCommand> commands = runtime.Tick(Frames.At(0));
        Assert.Empty(commands.OfType<ProduceCommand>());
        Assert.Equal(100, runtime.Ledger.Reserved(BudgetPools.Army, "ops"));
        DecisionRecord plan = Assert.Single(runtime.Log.Records, r => r.Kind == DecisionRecordKinds.Plan);
        Assert.Equal("navy", plan.Data.GetProperty("unknownPools")[0].GetString());
    }

    [Fact]
    public void Operations_run_every_second_and_on_operational_events()
    {
        FakePlanner planner = new();
        using BotRuntime runtime = Runtimes.Create(Selector(), planner: planner,
            events: t => t.Frame is >= 20 and < 25 ? [new StrategicEvent(StrategicEventKind.LowPower, new GameTime(20), 0.5, "power")] : []);
        for (long f = 0; f <= 30; f++) runtime.Tick(Frames.At(f));
        Assert.Equal([0L, 15L, 20L], planner.Calls.Select(t => t.Frame).ToArray());
    }

    [Fact]
    public void Tactics_run_at_the_configured_rate()
    {
        FakeSquadController squad = new();
        using BotRuntime runtime = Runtimes.Create(Selector(), tactics: [squad],
            options: new BotOptions(TacticalHz: 5, RunDeterministicStrategistsInline: true));
        for (long f = 0; f < 9; f++) runtime.Tick(Frames.At(f));
        Assert.Equal(3, squad.Calls);
    }

    [Fact]
    public void Leases_of_units_that_disappear_are_swept()
    {
        using BotRuntime runtime = Runtimes.Create(Selector(), tactics: []);
        runtime.Tick(Frames.At(0));
        runtime.Leases.TryAcquire(LeaseKey.Unit(new EntityId(2)), "squad", 1, new GameTime(0), 0, 60);
        runtime.Leases.TryAcquire(LeaseKey.Unit(new EntityId(3)), "squad", 1, new GameTime(0), 0, 60);
        Assert.Equal("squad", runtime.Leases.OwnerOf(LeaseKey.Unit(new EntityId(2)), new GameTime(1)));
        runtime.Tick(Frames.At(1, includeUnit2: false));
        Assert.Null(runtime.Leases.OwnerOf(LeaseKey.Unit(new EntityId(2)), new GameTime(1)));
        Assert.Equal("squad", runtime.Leases.OwnerOf(LeaseKey.Unit(new EntityId(3)), new GameTime(1)));
    }

    [Fact]
    public void Throwing_planner_is_contained_and_logged()
    {
        FakePlanner planner = new() { Throw = true };
        using BotRuntime runtime = Runtimes.Create(Selector(), planner: planner);
        IReadOnlyList<GameCommand> commands = runtime.Tick(Frames.At(0));
        Assert.Empty(commands.OfType<ProduceCommand>());
        Assert.Single(runtime.Log.Records, r => r.Kind == RuntimeRecordKinds.PlanFailed);
        Assert.Equal(1, runtime.Metrics.ComponentFailures);
    }

    [Fact]
    public async Task Slow_strategist_never_blocks_the_runtime()
    {
        using BotRuntime runtime = Runtimes.Create(new NeverStrategist());
        Task work = Task.Run(() =>
        {
            for (long f = 0; f < 200; f++) runtime.Tick(Frames.At(f));
        });
        Assert.True(await StrategySchedulerTests.Completes(work));
        Assert.Equal("generic-defend", runtime.ActiveIntent?.PlaybookId);
        Assert.Equal(200, runtime.Metrics.Frames);
    }

    [Fact]
    public async Task Run_async_pumps_frames_to_the_sink_until_the_source_ends()
    {
        using BotRuntime runtime = Runtimes.Create(Selector());
        ListSink sink = new();
        long frames = await runtime.RunAsync(new ListSource(Enumerable.Range(0, 30).Select(f => Frames.At(f))), sink);
        Assert.Equal(30, frames);
        Assert.NotEmpty(sink.Commands);
        Assert.Equal(runtime.Metrics.CommandsPassed, sink.Commands.Count);
    }

    [Fact]
    public void Same_inputs_produce_the_same_decision_log_hash()
    {
        static string Run(int credits)
        {
            DecisionLog log = new();
            using BotRuntime runtime = Runtimes.Create(Selector(), shadow: new ScriptedStrategist("shadow", IntentSource.Llm,
                c => Proposals.For(c, $"sh-{c.Features.SnapshotVersion}", "allied-expand", StrategicPosture.Boom)), log: log,
                events: t => t.Frame == 400 ? [new StrategicEvent(StrategicEventKind.BaseUnderAttack, t, 0.9, "base")] : []);
            for (long f = 0; f < 70 * GameTime.FramesPerSecond; f++) runtime.Tick(Frames.At(f, credits));
            return log.ComputeHash();
        }

        string first = Run(1000);
        Assert.Equal(first, Run(1000));
        Assert.NotEqual(first, Run(3000));
    }

    [Fact]
    public void Recorded_llm_run_replays_identically_from_its_decision_log()
    {
        static (DecisionLog Log, BotRuntime Runtime) Run(IStrategist primary)
        {
            DecisionLog log = new();
            BotRuntime runtime = Runtimes.Create(primary, log: log, options: new BotOptions(StrategicCadenceSeconds: 10, RunDeterministicStrategistsInline: true));
            for (long f = 0; f < 120 * GameTime.FramesPerSecond; f++) runtime.Tick(Frames.At(f));
            return (log, runtime);
        }

        (DecisionLog recorded, BotRuntime recordedRuntime) = Run(new DelayedStrategist(40));
        using (recordedRuntime)
        {
            Assert.Contains(recorded.Records, r => r.Kind == DecisionRecordKinds.Proposal);
            Assert.Contains(recorded.Records, r => r.Kind == DecisionRecordKinds.ProposalFailed);
        }

        ReplayStrategist replay = ReplayStrategist.FromNdjson(new StringReader(recorded.ToNdjson()));
        (DecisionLog replayed, BotRuntime replayRuntime) = Run(replay);
        using (replayRuntime)
        {
            Assert.Equal(0, replay.Misses);
            Assert.Equal("delayed-llm", replay.Id);
            Assert.Equal(recorded.ComputeHash(), replayed.ComputeHash());
        }

        // A replay with the wrong latency is detectably different.
        (DecisionLog other, BotRuntime otherRuntime) = Run(new DelayedStrategist(20));
        using (otherRuntime) Assert.NotEqual(recorded.ComputeHash(), other.ComputeHash());
    }

    /// <summary>
    /// The LLM strategist logs its own failure details (a role-less <c>strategy.proposal_failed</c> record) while it
    /// is being asked. A replay must write those records back at the same point, or a recorded live run with a
    /// failure could never replay byte-identically.
    /// </summary>
    [Fact]
    public void Strategist_authored_failure_records_are_replayed_at_the_same_point()
    {
        static (DecisionLog Log, BotRuntime Runtime) Run(Func<DecisionLog, IStrategist> primary)
        {
            DecisionLog log = new();
            BotRuntime runtime = Runtimes.Create(primary(log), log: log, options: new BotOptions(StrategicCadenceSeconds: 10, RunDeterministicStrategistsInline: true));
            for (long f = 0; f < 60 * GameTime.FramesPerSecond; f++) runtime.Tick(Frames.At(f));
            return (log, runtime);
        }

        (DecisionLog recorded, BotRuntime recordedRuntime) = Run(static log => new SelfLoggingFailingStrategist(log));
        recordedRuntime.Dispose();
        Assert.Contains(recorded.Records, static r => r.Kind == DecisionRecordKinds.ProposalFailed && !r.Data.TryGetProperty("role", out _));

        ReplayStrategist? replay = null;
        (DecisionLog replayed, BotRuntime replayRuntime) = Run(log => replay = new ReplayStrategist(recorded.Records, echoLog: log));
        replayRuntime.Dispose();

        Assert.Equal(0, replay!.Misses);
        Assert.Equal(recorded.ComputeHash(), replayed.ComputeHash());
    }
    private static DecisionLog RunFor(long frames, IStrategist primary, IStrategist? shadow = null)
    {
        DecisionLog log = new();
        using BotRuntime runtime = Runtimes.Create(primary, shadow: shadow, log: log,
            options: new BotOptions(StrategicCadenceSeconds: 10, RunDeterministicStrategistsInline: true));
        for (long f = 0; f < frames; f++) runtime.Tick(Frames.At(f));
        return log;
    }

    /// <summary>Invariant 6 for the llm-shadow arm: the shadow's answers are logged as <c>strategy.shadow</c> and replay from there.</summary>
    [Fact]
    public void Recorded_shadow_run_replays_identically_from_its_decision_log()
    {
        long frames = 60 * GameTime.FramesPerSecond;
        DecisionLog recorded = RunFor(frames, Selector(), new DelayedStrategist(40));
        Assert.Contains(recorded.Records, static r => r.Kind == DecisionRecordKinds.ShadowProposal);

        ReplayStrategist replay = new(recorded.Records, ProposalRole.Shadow);
        DecisionLog replayed = RunFor(frames, Selector(), replay);
        Assert.Equal(0, replay.Misses);
        Assert.Equal(recorded.ComputeHash(), replayed.ComputeHash());
    }

    /// <summary>
    /// A match normally ends with a request in flight; the replay must leave that request unanswered too, not
    /// answer it null and log a failure the recording never had.
    /// </summary>
    [Fact]
    public void Recorded_run_ending_mid_request_replays_identically()
    {
        long frames = 120 * GameTime.FramesPerSecond + 20;
        DecisionLog recorded = RunFor(frames, new DelayedStrategist(40));
        ReplayStrategist replay = ReplayStrategist.FromNdjson(new StringReader(recorded.ToNdjson()));
        DecisionLog replayed = RunFor(frames, replay);
        Assert.Equal(0, replay.Misses);
        Assert.Equal(1, replay.Unanswered);
        Assert.Equal(recorded.ComputeHash(), replayed.ComputeHash());
    }

    [Theory]
    [InlineData("never")]
    [InlineData("throwing")]
    public void Recorded_failures_replay_identically(string kind)
    {
        static IStrategist Make(string kind) => kind == "never" ? new NeverStrategist() : new ThrowingStrategist(false);
        long frames = 200 * GameTime.FramesPerSecond;
        DecisionLog recorded = RunFor(frames, Make(kind));
        Assert.Contains(recorded.Records, static r => r.Kind == DecisionRecordKinds.ProposalFailed);

        ReplayStrategist replay = ReplayStrategist.FromNdjson(new StringReader(recorded.ToNdjson()));
        DecisionLog replayed = RunFor(frames, replay);
        Assert.Equal(0, replay.Misses);
        Assert.Equal(recorded.ComputeHash(), replayed.ComputeHash());
    }
    /// <summary>
    /// Retail RA2 charges while building, so credits on hand still include what queued items owe. The ledger must
    /// size the period from credits less that remainder, or each pass funds the same money again on another queue.
    /// The simulator debits at order time and must not subtract it twice.
    /// </summary>
    [Theory]
    [InlineData(true, 1000 - 675 + 10)]
    [InlineData(false, 1000 + 10)]
    public void Ledger_capacity_excludes_what_queued_production_still_owes(bool chargedWhileBuilding, int expected)
    {
        using BotRuntime runtime = Runtimes.Create(Selector(),
            options: new BotOptions(RunDeterministicStrategistsInline: true, ProductionChargedWhileBuilding: chargedWhileBuilding));
        ProductionQueueState[] queues =
        [
            new(QueueKind.Vehicle, [new QueueItem("mtnk", 0.25, false, false)], 1),
            new(QueueKind.Building, [new QueueItem("gapowr", 1.0, true, false)], 1),
        ];
        runtime.Tick(Frames.At(0, credits: 1000) with { Queues = queues });

        // The tank being built still owes 900 × 0.75 = 675; the finished power plant owes nothing.
        Assert.Equal(expected, runtime.Ledger.Capacity);
    }
}
