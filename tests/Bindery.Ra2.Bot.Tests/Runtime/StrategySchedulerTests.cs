// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arbitration;
using Bindery.Ra2.Bot.Runtime;
using Bindery.Ra2.Bot.Tests.Arbitration;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Runtime;

public sealed class StrategySchedulerTests
{
    private readonly DecisionLog log = new();
    private readonly BotMetrics metrics = new();
    private readonly IntentArbiter arbiter;

    public StrategySchedulerTests()
    {
        arbiter = new IntentArbiter(Fx.Playbooks, null, log, metrics);
    }

    private StrategyScheduler Create(IStrategist primary, IStrategist? fallback = null, IStrategist? shadow = null, SchedulerOptions? options = null) =>
        new(primary, fallback ?? ScriptedStrategist.Fallback(), shadow, new IntentValidator(), arbiter, Fx.Rules, Fx.Playbooks, log, options, metrics);

    private static SchedulerTickReport Tick(StrategyScheduler scheduler, double seconds, IReadOnlyList<StrategicEvent>? events = null, double credits = 5000) =>
        scheduler.Tick(Fx.Belief(seconds), Fx.Features(seconds, events: events, credits: credits));

    private IEnumerable<string?> ActivatedIds() =>
        log.OfKind(DecisionRecordKinds.IntentActivated).Select(r => r.Data.GetProperty("intentId").GetString());

    [Fact]
    public void Proposal_arriving_after_the_age_limit_is_discarded_as_late()
    {
        ControlledStrategist primary = new();
        using StrategyScheduler scheduler = Create(primary);
        Tick(scheduler, 0);
        Assert.StartsWith("fb-", arbiter.Active?.IntentId);

        for (int s = 1; s <= 15; s++) Tick(scheduler, s);
        primary.CompleteLast("late", "allied-boom", StrategicPosture.Boom);
        Tick(scheduler, 15.2);

        DecisionRecord late = Assert.Single(log.OfKind(DecisionRecordKinds.LateDiscarded));
        Assert.Equal("age", late.Data.GetProperty("reason").GetString());
        Assert.Equal(1, metrics.LateDiscarded);
        Assert.DoesNotContain("late", ActivatedIds());
        Assert.DoesNotContain(log.OfKind(DecisionRecordKinds.Validation), r => r.Data.GetProperty("intentId").GetString() == "late");
    }

    [Fact]
    public void Proposal_arriving_within_the_age_limit_is_applied()
    {
        ControlledStrategist primary = new();
        using StrategyScheduler scheduler = Create(primary);
        Tick(scheduler, 0);
        for (int s = 1; s <= 14; s++) Tick(scheduler, s);
        primary.CompleteLast("timely", "allied-boom", StrategicPosture.Boom);
        Tick(scheduler, 14.5);

        Assert.Equal("timely", arbiter.Active?.IntentId);
        Assert.Empty(log.OfKind(DecisionRecordKinds.LateDiscarded));
    }

    [Fact]
    public void Severe_event_after_the_request_discards_the_result()
    {
        ControlledStrategist primary = new();
        using StrategyScheduler scheduler = Create(primary);
        Tick(scheduler, 0);
        Tick(scheduler, 2, [new StrategicEvent(StrategicEventKind.BaseUnderAttack, Fx.T(2), 0.8, "base")]);
        primary.CompleteLast("overtaken", "allied-boom", StrategicPosture.Boom);
        Tick(scheduler, 3);

        DecisionRecord late = Assert.Single(log.OfKind(DecisionRecordKinds.LateDiscarded));
        Assert.Equal("event", late.Data.GetProperty("reason").GetString());
        Assert.Equal("BaseUnderAttack", late.Data.GetProperty("eventKind").GetString());
        Assert.DoesNotContain("overtaken", ActivatedIds());
    }

    [Fact]
    public void Milder_event_after_the_request_does_not_discard()
    {
        ControlledStrategist primary = new();
        using StrategyScheduler scheduler = Create(primary);
        Tick(scheduler, 0);
        Tick(scheduler, 2, [new StrategicEvent(StrategicEventKind.MinerLost, Fx.T(2), 0.65, "miner")]);
        primary.CompleteLast("survives", "allied-boom", StrategicPosture.Boom);
        Tick(scheduler, 3);

        Assert.Empty(log.OfKind(DecisionRecordKinds.LateDiscarded));
        Assert.Equal("survives", arbiter.Active?.IntentId);
    }

    [Fact]
    public async Task Strategist_that_never_answers_does_not_stall_the_tick_and_times_out()
    {
        NeverStrategist primary = new();
        using StrategyScheduler scheduler = Create(primary);

        Task work = Task.Run(() =>
        {
            for (long frame = 0; frame <= 61 * GameTime.FramesPerSecond; frame++)
            {
                double s = frame / (double)GameTime.FramesPerSecond;
                scheduler.Tick(Fx.Belief(s), Fx.Features(s));
            }
        });
        Assert.True(await Completes(work), "Tick blocked on a strategist that never completes.");
        Assert.NotNull(arbiter.Active);
        Assert.StartsWith("fb-", arbiter.Active!.IntentId);
        DecisionRecord failed = Assert.Single(log.OfKind(DecisionRecordKinds.ProposalFailed));
        Assert.Equal("timeout", failed.Data.GetProperty("reason").GetString());
        Assert.True(primary.Tokens[0].IsCancellationRequested);
        Assert.Equal(2, primary.Tokens.Count);
    }

    [Fact]
    public void At_most_one_request_is_in_flight_and_queued_triggers_run_after_it()
    {
        ControlledStrategist primary = new();
        using StrategyScheduler scheduler = Create(primary);
        for (int s = 0; s <= 45; s++)
        {
            Tick(scheduler, s, s == 10 ? [new StrategicEvent(StrategicEventKind.NewEnemyTech, Fx.T(10), 0.9, "tech")] : null);
        }
        Assert.Single(primary.Calls);
        Assert.True(scheduler.PrimaryInFlight);

        primary.CompleteLast("p1", "allied-boom", StrategicPosture.Boom);
        Tick(scheduler, 45.5);
        Assert.Equal(2, primary.Calls.Count);
    }

    [Fact]
    public void Requests_follow_the_cadence_and_major_events()
    {
        ScriptedStrategist primary = new("p", IntentSource.Selector, c => Proposals.For(c, $"p-{c.Features.SnapshotVersion}", "allied-boom", StrategicPosture.Boom));
        using StrategyScheduler scheduler = Create(primary);
        StrategicEvent evt = new(StrategicEventKind.McvLost, Fx.T(5), 0.6, "mcv");
        for (long frame = 0; frame <= 45 * GameTime.FramesPerSecond; frame++)
        {
            double s = frame / (double)GameTime.FramesPerSecond;
            // The event stays in the feature window for 3 s; it must trigger exactly once.
            Tick(scheduler, s, s is >= 5 and < 8 ? [evt] : null);
        }
        Assert.Equal([0.0, 5.0, 25.0, 45.0], primary.Contexts.Select(c => c.Features.Time.Seconds).ToArray());
        string[] triggers = log.OfKind(RuntimeRecordKinds.Request)
            .Where(r => r.Data.GetProperty("role").GetString() == "Primary")
            .Select(r => r.Data.GetProperty("trigger").GetString()!).ToArray();
        Assert.Equal(["initial", "event:McvLost", "cadence", "cadence"], triggers);
    }

    [Fact]
    public void Replan_trigger_of_the_active_intent_requests_a_proposal()
    {
        ScriptedStrategist primary = new("p", IntentSource.Selector, c =>
            new StrategistProposal(Fx.Intent($"p-{c.Features.SnapshotVersion}", "allied-boom", StrategicPosture.Boom,
                issuedAt: c.Features.Time.Seconds, version: c.Features.SnapshotVersion,
                replan: [new Condition(ConditionMetric.Credits, Comparison.Lt, 1000)]), new ProposalCost(0, 0, 0, 0, null), null));
        using StrategyScheduler scheduler = Create(primary);
        Tick(scheduler, 0);
        Tick(scheduler, 1);
        Assert.Single(primary.Contexts);
        Tick(scheduler, 2, credits: 500);
        Assert.Equal(2, primary.Contexts.Count);
        Assert.Equal("replan:trigger", log.OfKind(RuntimeRecordKinds.Request)[^1].Data.GetProperty("trigger").GetString());
    }

    [Fact]
    public void Shadow_proposals_are_recorded_and_never_applied()
    {
        ScriptedStrategist primary = new("p", IntentSource.Selector, c => Proposals.For(c, $"p-{c.Features.SnapshotVersion}", "allied-boom", StrategicPosture.Boom, 0.2));
        ScriptedStrategist shadow = new("s", IntentSource.Llm, c => Proposals.For(c, $"shadow-{c.Features.SnapshotVersion}", "allied-turtle", StrategicPosture.Turtle, 1.0));
        using StrategyScheduler scheduler = Create(primary, shadow: shadow);
        for (int s = 0; s <= 100; s++) Tick(scheduler, s);

        IReadOnlyList<DecisionRecord> shadows = log.OfKind(DecisionRecordKinds.ShadowProposal);
        Assert.Equal(6, shadows.Count);
        Assert.All(shadows, r => Assert.True(r.Data.GetProperty("accepted").GetBoolean()));
        Assert.Contains(shadows, r => r.Data.GetProperty("wouldBe").GetString() == "Activated");
        Assert.DoesNotContain(ActivatedIds(), id => id!.StartsWith("shadow-", StringComparison.Ordinal));
        Assert.Equal("allied-boom", arbiter.Active?.PlaybookId);
        Assert.Equal(6, metrics.ShadowProposals);
        Assert.Equal(6, metrics.Proposals);
    }

    [Theory]
    [InlineData(true, "boom-sync")]
    [InlineData(false, "boom-async")]
    public void Strategist_exceptions_are_logged_as_failed_proposals(bool synchronous, string message)
    {
        using StrategyScheduler scheduler = Create(new ThrowingStrategist(synchronous));
        Tick(scheduler, 0);
        DecisionRecord failed = Assert.Single(log.OfKind(DecisionRecordKinds.ProposalFailed));
        Assert.Equal("exception", failed.Data.GetProperty("reason").GetString());
        Assert.Equal(message, failed.Data.GetProperty("message").GetString());
        Assert.NotNull(arbiter.Active);
    }

    [Fact]
    public void Null_proposal_is_logged_as_no_opinion()
    {
        using StrategyScheduler scheduler = Create(new ScriptedStrategist("p", IntentSource.Selector, _ => null));
        Tick(scheduler, 0);
        Assert.Equal("no_opinion", Assert.Single(log.OfKind(DecisionRecordKinds.ProposalFailed)).Data.GetProperty("reason").GetString());
    }

    [Fact]
    public void Invalid_proposal_is_counted_as_rejected_and_not_applied()
    {
        using StrategyScheduler scheduler = Create(new ScriptedStrategist("p", IntentSource.Selector,
            c => Proposals.For(c, "bad", "soviet-rush", StrategicPosture.AllIn)));
        Tick(scheduler, 0);
        Assert.Equal(1, metrics.Rejected);
        Assert.DoesNotContain("bad", ActivatedIds());
        DecisionRecord validation = log.OfKind(DecisionRecordKinds.Validation).First(r => r.Data.GetProperty("intentId").GetString() == "bad");
        Assert.False(validation.Data.GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public void Emergency_intent_is_installed_when_no_strategist_answers()
    {
        ScriptedStrategist silent = new("p", IntentSource.Selector, _ => null);
        using StrategyScheduler scheduler = Create(silent, fallback: new ScriptedStrategist("fb", IntentSource.Fallback, _ => null));
        Tick(scheduler, 0);
        Assert.Equal("generic-defend", arbiter.Active?.PlaybookId);
        Assert.Equal(ProposalRole.Emergency, arbiter.ActiveRole);
        Assert.Equal(1, metrics.EmergencyActivations);
    }

    [Fact]
    public void Dispose_cancels_in_flight_requests()
    {
        ControlledStrategist primary = new();
        StrategyScheduler scheduler = Create(primary);
        Tick(scheduler, 0);
        CancellationToken token = primary.Calls[0].Token;
        Assert.False(token.IsCancellationRequested);
        scheduler.Dispose();
        Assert.True(token.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => Tick(scheduler, 1));
    }

    [Fact]
    public void Inline_mode_applies_a_thread_pool_deterministic_strategist_on_the_same_frame()
    {
        using (StrategyScheduler inline = Create(new ThreadPoolStrategist(), options: new SchedulerOptions(RunDeterministicStrategistsInline: true)))
        {
            Tick(inline, 0);
            Assert.Equal("tp-0", arbiter.Active?.IntentId);
        }
    }

    [Fact]
    public void Without_inline_mode_a_thread_pool_strategist_is_not_awaited()
    {
        using StrategyScheduler scheduler = Create(new ThreadPoolStrategist());
        Tick(scheduler, 0);
        Assert.StartsWith("fb-", arbiter.Active?.IntentId);
        Assert.True(scheduler.PrimaryInFlight);
    }

    [Fact]
    public async Task Inline_mode_never_waits_for_an_llm_strategist()
    {
        NeverStrategist primary = new();
        using StrategyScheduler scheduler = Create(primary, options: new SchedulerOptions(RunDeterministicStrategistsInline: true, InlineWaitTimeoutSeconds: 60));
        Assert.True(await Completes(Task.Run(() => Tick(scheduler, 0)), 10));
    }

    /// <summary>True when <paramref name="work"/> finishes within the wall-clock bound (a hang fails the test instead of hanging it).</summary>
    internal static async Task<bool> Completes(Task work, double seconds = 20)
    {
        Task winner = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(seconds)));
        if (winner != work) return false;
        await work;
        return true;
    }

    [Fact]
    public void Abort_of_the_active_intent_brings_in_the_fallback_despite_commitment()
    {
        ScriptedStrategist primary = new("p", IntentSource.Selector, c =>
            new StrategistProposal(Fx.Intent($"p-{c.Features.SnapshotVersion}", "allied-pressure", StrategicPosture.Pressure,
                issuedAt: c.Features.Time.Seconds, version: c.Features.SnapshotVersion,
                abort: [new Condition(ConditionMetric.Credits, Comparison.Lt, 100)]), new ProposalCost(0, 0, 0, 0, null), null));
        using StrategyScheduler scheduler = Create(primary);
        Tick(scheduler, 0);
        Assert.Equal("allied-pressure", arbiter.Active?.PlaybookId);

        Tick(scheduler, 5, credits: 50);
        Assert.Equal("generic-defend", arbiter.Active?.PlaybookId);
        Assert.Equal(1, metrics.FallbackActivations);
    }
}
