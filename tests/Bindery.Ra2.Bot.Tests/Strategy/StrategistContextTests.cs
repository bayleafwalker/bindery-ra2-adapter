// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arbitration;
using Bindery.Ra2.Bot.Runtime;
using Bindery.Ra2.Bot.Strategy;
using Bindery.Ra2.Bot.Tests.Arbitration;
using Bindery.Ra2.Bot.Tests.Runtime;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Strategy;

/// <summary>
/// What a strategist is told about its request (why it was asked, who installed the active intent and since
/// when), what the runtime records about each decision (its observation mode, the features it saw, a shadow
/// strategist's inputs), and a refinement arriving after the plan it refines has ended.
/// </summary>
public sealed class StrategistContextTests
{
    private readonly DecisionLog log = new();
    private readonly BotMetrics metrics = new();
    private readonly IntentArbiter arbiter;

    public StrategistContextTests()
    {
        arbiter = new IntentArbiter(Fx.Playbooks, null, log, metrics);
    }

    private StrategyScheduler Create(IStrategist primary, IStrategist? shadow = null) =>
        new(primary, ScriptedStrategist.Fallback(), shadow, new IntentValidator(), arbiter, Fx.Rules, Fx.Playbooks, log,
            new SchedulerOptions { StrategicCadenceSeconds = 5 }, metrics);

    private static void Tick(StrategyScheduler scheduler, double seconds, IReadOnlyList<StrategicEvent>? events = null) =>
        scheduler.Tick(Fx.Belief(seconds), Fx.Features(seconds, events: events));

    [Fact]
    public void A_request_says_why_it_was_asked_and_how_the_active_intent_was_installed()
    {
        ScriptedStrategist primary = new("primary", IntentSource.Llm, static _ => null);
        using StrategyScheduler scheduler = Create(primary);

        Tick(scheduler, 0);
        Tick(scheduler, 5);
        Tick(scheduler, 6, [new StrategicEvent(StrategicEventKind.BaseUnderAttack, Fx.T(6), 0.9, "base")]);

        Assert.Equal(["initial", "cadence", "event:BaseUnderAttack"], primary.Contexts.Select(static c => c.Trigger));
        Assert.Null(primary.Contexts[0].ActiveRole);
        // The primary had no opinion at t=0, so the fallback's placeholder is what the later requests see.
        Assert.Equal(ProposalRole.Fallback, primary.Contexts[1].ActiveRole);
        Assert.Equal(arbiter.ActiveSince, primary.Contexts[1].ActiveSince);
        Assert.Equal(Fx.T(0), primary.Contexts[1].ActiveSince);
    }

    [Fact]
    public async Task Two_speed_sends_an_event_triggered_request_to_the_slow_strategist_even_when_its_features_show_no_event()
    {
        ScriptedStrategist slow = new("slow", IntentSource.Llm, c => Proposals.For(c, $"slow-{c.Features.SnapshotVersion}", "allied-boom", StrategicPosture.Boom));
        ScriptedStrategist fast = new("fast", IntentSource.Llm, c => Proposals.For(c, $"fast-{c.Features.SnapshotVersion}", "allied-boom", StrategicPosture.Boom));
        TwoSpeedStrategist twoSpeed = new(slow, fast, 20);
        StrategistContext Context(double seconds, StrategicIntent? active, string trigger) =>
            new(Fx.Features(seconds), Fx.Rules, Fx.Playbooks, active, [], null, Trigger: trigger);

        StrategicIntent own = (await twoSpeed.ProposeAsync(Context(0, null, "initial")))!.Intent;
        await twoSpeed.ProposeAsync(Context(5, own, "cadence"));
        Assert.Single(fast.Contexts);

        // Deferred behind the fast request, the event's own frame is gone from the features; the trigger is not.
        await twoSpeed.ProposeAsync(Context(6, own, "event:BaseUnderAttack"));
        await twoSpeed.ProposeAsync(Context(7, own, "replan:abort"));

        Assert.Equal(3, slow.Contexts.Count);
        Assert.Single(fast.Contexts);
    }

    [Fact]
    public void A_refinement_of_a_plan_that_ended_while_it_was_in_flight_is_discarded()
    {
        ControlledStrategist primary = new();
        using StrategyScheduler scheduler = Create(primary);
        Tick(scheduler, 0);
        string placeholder = arbiter.Active!.IntentId;

        var call = primary.Calls[^1];
        StrategistProposal stale = Proposals.For(call.Context, "refined", "generic-defend", StrategicPosture.Defend) with { RefinesIntentId = "an-ended-plan" };
        call.Completion.SetResult(stale);
        Tick(scheduler, 1);

        DecisionRecord late = Assert.Single(log.OfKind(DecisionRecordKinds.LateDiscarded));
        Assert.Equal("refined_intent_ended", late.Data.GetProperty("reason").GetString());
        Assert.Equal(placeholder, arbiter.Active!.IntentId);
    }

    [Fact]
    public void A_refinement_of_the_active_plan_is_applied()
    {
        ControlledStrategist primary = new();
        using StrategyScheduler scheduler = Create(primary);
        Tick(scheduler, 0);
        string active = arbiter.Active!.IntentId;

        var call = primary.Calls[^1];
        call.Completion.SetResult(Proposals.For(call.Context, "refined", "generic-defend", StrategicPosture.Defend) with { RefinesIntentId = active });
        Tick(scheduler, 1);

        Assert.Empty(log.OfKind(DecisionRecordKinds.LateDiscarded));
        Assert.Equal("refined", arbiter.Active!.IntentId);
    }

    [Fact]
    public void Activation_records_carry_the_observation_mode_and_examples_the_features_the_strategist_saw()
    {
        ControlledStrategist primary = new();
        using StrategyScheduler scheduler = Create(primary);
        Tick(scheduler, 0);
        primary.CompleteLast("chosen", "allied-boom", StrategicPosture.Boom);
        Tick(scheduler, 2);

        DecisionRecord activation = log.OfKind(DecisionRecordKinds.IntentActivated).Single(r => r.Data.GetProperty("intentId").GetString() == "chosen");
        Assert.Equal("Belief", activation.Data.GetProperty("mode").GetString());

        // Read as an oracle run's log, the record's own mode still wins.
        DecisionExample example = Assert.Single(DecisionDataset.FromDecisionLog(log.Records, DatasetFilter.PrimaryOnly, mode: ObservationMode.Oracle).Examples);
        Assert.Equal(ObservationMode.Belief, example.Mode);
        // Asked at t=0, applied at t=2: the example is the t=0 features, at their frame and version.
        Assert.Equal(Fx.T(0).Frame, example.Frame);
        Assert.Equal(Fx.Features(0).SnapshotVersion, example.SnapshotVersion);
        Assert.NotEqual(activation.Time.Frame, example.Frame);
    }

    [Fact]
    public void A_shadow_strategists_answers_can_be_distilled()
    {
        ScriptedStrategist primary = new("primary", IntentSource.Selector, c => Proposals.For(c, $"p-{c.Features.SnapshotVersion}", "generic-defend", StrategicPosture.Defend));
        ScriptedStrategist shadow = new("shadow", IntentSource.Llm, c => Proposals.For(c, $"s-{c.Features.SnapshotVersion}", "allied-boom", StrategicPosture.Boom));
        using StrategyScheduler scheduler = Create(primary, shadow);
        Tick(scheduler, 0);
        Tick(scheduler, 1);

        Assert.DoesNotContain(DecisionDataset.FromDecisionLog(log.Records).Examples, static e => e.Role == "Shadow");
        DecisionExample example = Assert.Single(DecisionDataset.FromDecisionLog(log.Records, DatasetFilter.ShadowOnly).Examples);
        Assert.Equal("allied-boom", example.PlaybookId);
        Assert.Equal("Shadow", example.Role);
        Assert.Equal(FeatureVector.Dimension, example.Features.Count);
    }
}
