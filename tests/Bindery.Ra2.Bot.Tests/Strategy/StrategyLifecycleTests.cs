// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arbitration;
using Bindery.Ra2.Bot.Runtime;
using Bindery.Ra2.Bot.Strategy;
using Bindery.Ra2.Bot.Tests.Arbitration;
using Bindery.Ra2.Bot.Tests.Runtime;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Strategy;

/// <summary>
/// Strategists whose routing or learning depends on what became of their earlier proposals: the two-speed
/// strategist (which of its two models answers) and the bandit (which decisions it learns from). Both are run
/// against the real scheduler, validator and arbiter where the outcome matters.
/// </summary>
public sealed class StrategyLifecycleTests
{
    private readonly DecisionLog log = new();
    private readonly BotMetrics metrics = new();
    private readonly IntentArbiter arbiter;

    public StrategyLifecycleTests()
    {
        arbiter = new IntentArbiter(Fx.Playbooks, null, log, metrics);
    }

    private StrategyScheduler Create(IStrategist primary) =>
        new(primary, ScriptedStrategist.Fallback(), null, new IntentValidator(), arbiter, Fx.Rules, Fx.Playbooks, log,
            new SchedulerOptions { StrategicCadenceSeconds = 5 }, metrics);

    private static void Tick(StrategyScheduler scheduler, double seconds, IReadOnlyList<StrategicEvent>? events = null) =>
        scheduler.Tick(Fx.Belief(seconds), Fx.Features(seconds, events: events));

    /// <summary>A Refine-mode stand-in: keeps the active playbook and posture, as the Claude strategist's Refine mode must.</summary>
    private static ScriptedStrategist Refiner() =>
        new("fast", IntentSource.Llm, c => c.ActiveIntent is { } active
            ? Proposals.For(c, $"refine-{c.Features.SnapshotVersion}", active.PlaybookId, active.Posture)
            : null);

    [Fact]
    public void Two_speed_sends_the_request_an_event_deferred_to_the_slow_strategist()
    {
        ControlledStrategist slow = new();
        ScriptedStrategist fast = Refiner();
        using StrategyScheduler scheduler = Create(new TwoSpeedStrategist(slow, fast, 20));

        Tick(scheduler, 0);
        Assert.Single(slow.Calls);
        Assert.StartsWith("fb-", arbiter.Active?.IntentId);

        // The attack arrives while the slow request is in flight: its answer is discarded, and the request the
        // event triggered starts on the next frame, whose own features carry no event any more.
        Tick(scheduler, 2, [new StrategicEvent(StrategicEventKind.BaseUnderAttack, Fx.T(2), 0.9, "base")]);
        slow.CompleteLast("overtaken", "allied-boom", StrategicPosture.Boom);
        Tick(scheduler, 3);

        Assert.Single(log.OfKind(DecisionRecordKinds.LateDiscarded));
        Assert.Equal(2, slow.Calls.Count);
        Assert.Empty(fast.Contexts);
    }

    [Fact]
    public void Two_speed_never_lets_the_fast_strategist_renew_a_placeholder_after_a_failed_slow_request()
    {
        int slowCalls = 0;
        ScriptedStrategist slow = new("slow", IntentSource.Llm, c => ++slowCalls == 1
            ? null
            : Proposals.For(c, $"slow-{c.Features.SnapshotVersion}", "allied-boom", StrategicPosture.Boom, 0.9));
        ScriptedStrategist fast = Refiner();
        using StrategyScheduler scheduler = Create(new TwoSpeedStrategist(slow, fast, 20));

        for (int s = 0; s <= 22; s++) Tick(scheduler, s);

        // The fast strategist may only refine what the slow one chose, never the fallback's placeholder.
        Assert.All(fast.Contexts, c => Assert.DoesNotMatch("^fb-", c.ActiveIntent!.IntentId));
        Assert.Equal("allied-boom", arbiter.Active?.PlaybookId);
        Assert.Equal(2, slow.Contexts.Count);
        Assert.StartsWith("slow-", log.OfKind(DecisionRecordKinds.IntentActivated)
            .Select(r => r.Data.GetProperty("intentId").GetString()).First(id => id is not null && !id.StartsWith("fb-", StringComparison.Ordinal)));
    }

    [Fact]
    public void Two_speed_keeps_the_fast_strategist_between_slow_turns_once_the_slow_choice_is_active()
    {
        int slowCalls = 0;
        ScriptedStrategist slow = new("slow", IntentSource.Llm, c =>
        {
            slowCalls++;
            return Proposals.For(c, $"slow-{c.Features.SnapshotVersion}", "allied-boom", StrategicPosture.Boom, 0.9);
        });
        ScriptedStrategist fast = Refiner();
        using StrategyScheduler scheduler = Create(new TwoSpeedStrategist(slow, fast, 20));

        for (int s = 0; s <= 19; s++) Tick(scheduler, s);

        // Slow at 0; its choice is active, so 5, 10 and 15 are refinements.
        Assert.Equal(1, slowCalls);
        Assert.Equal(3, fast.Contexts.Count);
        Assert.Equal("allied-boom", arbiter.Active?.PlaybookId);
    }
}
