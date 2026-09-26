// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arbitration;
using Bindery.Ra2.Bot.Runtime;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arbitration;

public sealed class IntentArbiterTests
{
    private readonly DecisionLog log = new();
    private readonly BotMetrics metrics = new();
    private readonly IntentArbiter arbiter;

    public IntentArbiterTests()
    {
        arbiter = new IntentArbiter(Fx.Playbooks, null, log, metrics);
    }

    private ArbitrationDecision Offer(StrategicIntent intent, StrategicFeatures features, ProposalRole role = ProposalRole.Primary) =>
        arbiter.Offer(Fx.Accepted(intent), features, role);

    [Fact]
    public void First_valid_intent_activates()
    {
        ArbitrationDecision decision = Offer(Fx.Intent("a", "allied-boom", StrategicPosture.Boom), Fx.Features(0));
        Assert.Equal(ArbitrationOutcome.Activated, decision.Outcome);
        Assert.Equal("a", arbiter.Active?.IntentId);
        Assert.Single(log.OfKind(DecisionRecordKinds.IntentActivated));
    }

    [Fact]
    public void Rejected_validation_result_never_becomes_active()
    {
        StrategicIntent intent = Fx.Intent("a", "allied-boom", StrategicPosture.Boom);
        ArbitrationDecision decision = arbiter.Offer(new ValidationResult(false, intent, []), Fx.Features(0));
        Assert.Equal(ArbitrationOutcome.Refused, decision.Outcome);
        Assert.Equal("not_validated", decision.Reason);
        Assert.Null(arbiter.Active);
    }

    [Fact]
    public void Commitment_blocks_a_switch_inside_the_minimum_commit_window()
    {
        Offer(Fx.Intent("a", "allied-boom", StrategicPosture.Boom), Fx.Features(0));

        ArbitrationDecision early = Offer(Fx.Intent("b", "allied-expand", StrategicPosture.Boom, issuedAt: 44), Fx.Features(44));
        Assert.Equal(ArbitrationOutcome.Refused, early.Outcome);
        Assert.Equal("commitment", early.Reason);
        Assert.Equal("a", arbiter.Active?.IntentId);

        ArbitrationDecision later = Offer(Fx.Intent("c", "allied-expand", StrategicPosture.Boom, issuedAt: 45), Fx.Features(45));
        Assert.Equal(ArbitrationOutcome.Activated, later.Outcome);
        Assert.Equal("c", arbiter.Active?.IntentId);
    }

    [Fact]
    public void Playbook_without_min_commit_uses_the_default_window()
    {
        Offer(Fx.Intent("a", "generic-defend", StrategicPosture.Defend), Fx.Features(0));
        Assert.Equal(45, arbiter.MinCommitSecondsFor(arbiter.Active!));
        ArbitrationDecision decision = Offer(Fx.Intent("b", "allied-turtle", StrategicPosture.Defend, issuedAt: 30), Fx.Features(30));
        Assert.Equal("commitment", decision.Reason);
    }

    [Fact]
    public void Firing_abort_trigger_overrides_commitment()
    {
        StrategicIntent pressure = Fx.Intent("a", "allied-pressure", StrategicPosture.Pressure,
            abort: [new Condition(ConditionMetric.ArmyValueRatio, Comparison.Lt, 0.5)]);
        Offer(pressure, Fx.Features(0));

        StrategicIntent defend = Fx.Intent("b", "generic-defend", StrategicPosture.Defend, issuedAt: 10, confidence: 0.1);
        Assert.Equal("commitment", Offer(defend, Fx.Features(10, ownArmy: 900, enemyArmy: 1000)).Reason);

        ArbitrationDecision decision = Offer(defend, Fx.Features(10, ownArmy: 300, enemyArmy: 1000));
        Assert.Equal(ArbitrationOutcome.Activated, decision.Outcome);
        Assert.Equal("override:abort", decision.Reason);
    }

    [Fact]
    public void Base_threat_above_the_ratio_overrides_commitment_but_not_at_it()
    {
        Offer(Fx.Intent("a", "allied-boom", StrategicPosture.Boom), Fx.Features(0));
        StrategicIntent defend = Fx.Intent("b", "generic-defend", StrategicPosture.Defend, issuedAt: 5);
        ThreatAssessment AtRatio(double enemy) => new(Fx.R0, enemy, 1000, 1000 / enemy, 10, 5, true, 1);

        Assert.Equal("commitment", Offer(defend, Fx.Features(5, threats: [AtRatio(1500)])).Reason);
        ArbitrationDecision decision = Offer(defend, Fx.Features(5, threats: [AtRatio(1600)]));
        Assert.Equal("override:base_threat", decision.Reason);
        Assert.Equal("b", arbiter.Active?.IntentId);
    }

    [Fact]
    public void Posture_flip_without_confidence_margin_is_refused_after_commitment()
    {
        Offer(Fx.Intent("a", "allied-boom", StrategicPosture.Boom, confidence: 0.6), Fx.Features(0));

        ArbitrationDecision small = Offer(Fx.Intent("b", "allied-turtle", StrategicPosture.Turtle, issuedAt: 60, confidence: 0.74), Fx.Features(60));
        Assert.Equal(ArbitrationOutcome.Refused, small.Outcome);
        Assert.Equal("hysteresis", small.Reason);

        ArbitrationDecision enough = Offer(Fx.Intent("c", "allied-turtle", StrategicPosture.Turtle, issuedAt: 60, confidence: 0.75), Fx.Features(60));
        Assert.Equal(ArbitrationOutcome.Activated, enough.Outcome);
        Assert.Equal(1, metrics.PostureFlips);
    }

    [Fact]
    public void Same_posture_switch_after_commitment_needs_no_margin()
    {
        Offer(Fx.Intent("a", "allied-boom", StrategicPosture.Boom, confidence: 0.9), Fx.Features(0));
        ArbitrationDecision decision = Offer(Fx.Intent("b", "allied-expand", StrategicPosture.Boom, issuedAt: 50, confidence: 0.2), Fx.Features(50));
        Assert.Equal(ArbitrationOutcome.Activated, decision.Outcome);
        Assert.Equal(0, metrics.PostureFlips);
    }

    [Fact]
    public void Renewal_inside_commitment_is_accepted_without_restarting_the_clock()
    {
        Offer(Fx.Intent("a", "allied-boom", StrategicPosture.Boom), Fx.Features(0));
        ArbitrationDecision decision = Offer(Fx.Intent("a2", "allied-boom", StrategicPosture.Boom, issuedAt: 10,
            parameters: new Dictionary<string, double> { ["aggression"] = 0.9 }), Fx.Features(10));

        Assert.Equal(ArbitrationOutcome.Renewed, decision.Outcome);
        Assert.Equal("a2", arbiter.Active?.IntentId);
        Assert.Equal(Fx.T(0), arbiter.ActiveSince);
        Assert.Equal(1, metrics.Activations);
        Assert.Equal(1, metrics.Renewals);
        Assert.Equal("renewed", arbiter.History[0].EndReason);
    }

    [Fact]
    public void Fallback_placeholder_yields_to_a_primary_proposal_inside_commitment()
    {
        Offer(Fx.Intent("f", "generic-defend", StrategicPosture.Defend, confidence: 0.9), Fx.Features(0), ProposalRole.Fallback);
        ArbitrationDecision decision = Offer(Fx.Intent("p", "allied-boom", StrategicPosture.Boom, issuedAt: 3, confidence: 0.1), Fx.Features(3));
        Assert.Equal(ArbitrationOutcome.Activated, decision.Outcome);
        Assert.Equal("yield", decision.Reason);

        // The primary's own intent is a real commitment.
        Assert.Equal("commitment", Offer(Fx.Intent("q", "allied-turtle", StrategicPosture.Turtle, issuedAt: 4, confidence: 1), Fx.Features(4)).Reason);
    }

    [Fact]
    public void Emergency_intent_never_replaces_an_active_intent()
    {
        Offer(Fx.Intent("a", "allied-boom", StrategicPosture.Boom), Fx.Features(0));
        ArbitrationDecision decision = Offer(Fx.Intent("e", "generic-defend", StrategicPosture.Defend), Fx.Features(100), ProposalRole.Emergency);
        Assert.Equal("emergency_only_when_idle", decision.Reason);
    }

    [Fact]
    public void Expiry_ends_the_intent_and_requests_the_fallback()
    {
        Offer(Fx.Intent("a", "allied-boom", StrategicPosture.Boom, lifetime: 30), Fx.Features(0));
        arbiter.Update(Fx.Features(29));
        Assert.NotNull(arbiter.Active);
        Assert.False(arbiter.FallbackRequested);

        arbiter.Update(Fx.Features(30));
        Assert.Null(arbiter.Active);
        Assert.True(arbiter.FallbackRequested);
        Assert.True(arbiter.ReplanRequested);
        Assert.Equal("expired", arbiter.History[0].EndReason);
        Assert.Equal(Fx.T(30), arbiter.History[0].EndedAt);
        Assert.Single(log.OfKind(DecisionRecordKinds.IntentEnded));
    }

    [Fact]
    public void Replan_trigger_requests_a_replan_once_per_rising_edge()
    {
        Condition lowCredits = new(ConditionMetric.Credits, Comparison.Lt, 1000);
        Offer(Fx.Intent("a", "allied-boom", StrategicPosture.Boom, replan: [lowCredits]), Fx.Features(0));

        arbiter.Update(Fx.Features(1, credits: 5000));
        Assert.False(arbiter.ReplanRequested);
        arbiter.Update(Fx.Features(2, credits: 500));
        Assert.True(arbiter.ReplanRequested);
        Assert.Equal("trigger", arbiter.ReplanReason);
        arbiter.AcknowledgeReplan();
        arbiter.Update(Fx.Features(3, credits: 400));
        Assert.False(arbiter.ReplanRequested);
        arbiter.Update(Fx.Features(4, credits: 5000));
        arbiter.Update(Fx.Features(5, credits: 400));
        Assert.True(arbiter.ReplanRequested);
    }

    [Fact]
    public void Abort_trigger_requests_fallback_and_replan()
    {
        Offer(Fx.Intent("a", "allied-pressure", StrategicPosture.Pressure,
            abort: [new Condition(ConditionMetric.ArmyValueRatio, Comparison.Lt, 0.5)]), Fx.Features(0));
        arbiter.Update(Fx.Features(1, ownArmy: 200, enemyArmy: 1000));
        Assert.True(arbiter.FallbackRequested);
        Assert.Equal("abort", arbiter.ReplanReason);
    }

    [Fact]
    public void Preview_does_not_change_state()
    {
        Offer(Fx.Intent("a", "allied-boom", StrategicPosture.Boom), Fx.Features(0));
        ArbitrationDecision preview = arbiter.Preview(Fx.Accepted(Fx.Intent("b", "allied-expand", StrategicPosture.Boom, issuedAt: 50)), Fx.Features(50));
        Assert.Equal(ArbitrationOutcome.Activated, preview.Outcome);
        Assert.Equal("a", arbiter.Active?.IntentId);
    }

    [Fact]
    public void Intent_whose_abort_trigger_already_holds_is_refused()
    {
        StrategicIntent doomed = Fx.Intent("d", "allied-pressure", StrategicPosture.Pressure,
            abort: [new Condition(ConditionMetric.ArmyValueRatio, Comparison.Lt, 0.5)]);
        ArbitrationDecision decision = Offer(doomed, Fx.Features(0, ownArmy: 100, enemyArmy: 1000));
        Assert.Equal("abort_firing", decision.Reason);
        Assert.Null(arbiter.Active);
        Assert.Equal(ArbitrationOutcome.Activated, Offer(doomed, Fx.Features(0, ownArmy: 900, enemyArmy: 1000)).Outcome);
    }
    private static ThreatAssessment BaseThreat(double enemy) => new(Fx.R0, enemy, 1000, 1000 / enemy, 10, 5, true, 1);

    /// <summary>
    /// A playbook's own <see cref="Playbook.MinCommitSeconds"/> decides the window, not the default: a 90 s plan
    /// holds past the 45 s default and a 30 s plan yields before it.
    /// </summary>
    [Fact]
    public void Playbook_min_commit_seconds_sets_the_window()
    {
        Assert.True(Fx.Playbooks.TryGet("allied-turtle", out Playbook turtle));
        Assert.True(Fx.Playbooks.TryGet("allied-expand", out Playbook expand));
        CommitPlaybooks library = new([turtle with { Id = "allied-siege", MinCommitSeconds = 90 }, expand with { Id = "allied-raid", MinCommitSeconds = 30 }]);
        IntentArbiter custom = new(library);

        custom.Offer(Fx.Accepted(Fx.Intent("a", "allied-siege", StrategicPosture.Turtle)), Fx.Features(0));
        Assert.Equal(90, custom.MinCommitSecondsFor(custom.Active!));
        Assert.Equal("commitment", custom.Offer(Fx.Accepted(Fx.Intent("b", "allied-turtle", StrategicPosture.Turtle, issuedAt: 60)), Fx.Features(60)).Reason);
        Assert.Equal("switch", custom.Offer(Fx.Accepted(Fx.Intent("c", "allied-raid", StrategicPosture.Turtle, issuedAt: 91)), Fx.Features(91)).Reason);

        Assert.Equal(30, custom.MinCommitSecondsFor(custom.Active!));
        Assert.Equal("commitment", custom.Offer(Fx.Accepted(Fx.Intent("d", "allied-turtle", StrategicPosture.Turtle, issuedAt: 120)), Fx.Features(120)).Reason);
        Assert.Equal("switch", custom.Offer(Fx.Accepted(Fx.Intent("e", "allied-turtle", StrategicPosture.Turtle, issuedAt: 122)), Fx.Features(122)).Reason);
    }

    /// <summary>
    /// The base-threat override answers a threat once: the plan it installs gets its normal commitment, and a
    /// defensive incumbent is never overridden, so a strategist wavering under attack cannot flip the posture every
    /// cadence. A new threat episode (after the threat cleared) may override again.
    /// </summary>
    [Fact]
    public void Base_threat_overrides_commitment_once_per_threat_episode()
    {
        Offer(Fx.Intent("a", "allied-boom", StrategicPosture.Boom), Fx.Features(0));
        for (int s = 1; s < 5; s++) arbiter.Update(Fx.Features(s));

        StrategicFeatures threat5 = Fx.Features(5, threats: [BaseThreat(2000)]);
        arbiter.Update(threat5);
        Assert.Equal("override:base_threat", Offer(Fx.Intent("d1", "generic-defend", StrategicPosture.Defend, issuedAt: 5, confidence: 0.1), threat5).Reason);
        for (int s = 6; s <= 10; s++)
        {
            StrategicFeatures threat = Fx.Features(s, threats: [BaseThreat(2000)]);
            arbiter.Update(threat);
            StrategicIntent wavering = s % 2 == 0
                ? Fx.Intent($"b{s}", "allied-boom", StrategicPosture.Boom, issuedAt: s, confidence: 0.1)
                : Fx.Intent($"t{s}", "allied-turtle", StrategicPosture.Turtle, issuedAt: s, confidence: 0.1);
            Assert.Equal("commitment", Offer(wavering, threat).Reason);
        }
        Assert.Equal("d1", arbiter.Active?.IntentId);
        Assert.Equal(1, metrics.PostureFlips);

        // The threat clears; after commitment a boom plan takes over, and a fresh threat may override it again.
        arbiter.Update(Fx.Features(40));
        Assert.Equal("switch", Offer(Fx.Intent("b2", "allied-boom", StrategicPosture.Boom, issuedAt: 50, confidence: 0.3), Fx.Features(50)).Reason);
        StrategicFeatures threat51 = Fx.Features(51, threats: [BaseThreat(2000)]);
        arbiter.Update(threat51);
        Assert.Equal("override:base_threat", Offer(Fx.Intent("d2", "generic-defend", StrategicPosture.Defend, issuedAt: 51, confidence: 0.1), threat51).Reason);
    }

    [Fact]
    public void Base_threat_override_does_not_replace_a_defensive_incumbent()
    {
        Offer(Fx.Intent("t", "allied-turtle", StrategicPosture.Turtle), Fx.Features(0));
        StrategicFeatures threat = Fx.Features(5, threats: [BaseThreat(2000)]);
        arbiter.Update(threat);
        Assert.Equal("commitment", Offer(Fx.Intent("d", "generic-defend", StrategicPosture.Defend, issuedAt: 5, confidence: 0.1), threat).Reason);
    }

    /// <summary>
    /// A firing abort trigger ends the plan (like expiry) instead of leaving it running until a different plan
    /// happens to arrive: a re-proposal of the same plan is refused as abort_firing, and without the end the
    /// aborted intent would keep driving the planner until it expired.
    /// </summary>
    [Fact]
    public void Firing_abort_trigger_ends_the_intent()
    {
        StrategicIntent pressure = Fx.Intent("a", "allied-pressure", StrategicPosture.Pressure,
            abort: [new Condition(ConditionMetric.ArmyValueRatio, Comparison.Lt, 0.5)]);
        Offer(pressure, Fx.Features(0));
        arbiter.Update(Fx.Features(40, ownArmy: 300, enemyArmy: 1000));

        Assert.Null(arbiter.Active);
        Assert.Equal("aborted", arbiter.History[0].EndReason);
        Assert.True(arbiter.FallbackRequested);
        Assert.Equal("abort", arbiter.ReplanReason);

        StrategicIntent again = Fx.Intent("a2", "allied-pressure", StrategicPosture.Pressure, issuedAt: 41,
            abort: [new Condition(ConditionMetric.ArmyValueRatio, Comparison.Lt, 0.5)]);
        Assert.Equal("abort_firing", Offer(again, Fx.Features(41, ownArmy: 300, enemyArmy: 1000)).Reason);
        Assert.Null(arbiter.Active);
    }

    [Fact]
    public void Base_threat_requests_a_replan_once_per_rising_edge()
    {
        Offer(Fx.Intent("a", "allied-boom", StrategicPosture.Boom), Fx.Features(0));
        arbiter.Update(Fx.Features(1, threats: [BaseThreat(1500)]));
        Assert.False(arbiter.ReplanRequested);
        arbiter.Update(Fx.Features(2, threats: [BaseThreat(1600)]));
        Assert.True(arbiter.ReplanRequested);
        Assert.Equal("base_threat", arbiter.ReplanReason);
        arbiter.AcknowledgeReplan();
        arbiter.Update(Fx.Features(3, threats: [BaseThreat(1600)]));
        Assert.False(arbiter.ReplanRequested);
        arbiter.Update(Fx.Features(4));
        arbiter.Update(Fx.Features(5, threats: [BaseThreat(1600)]));
        Assert.True(arbiter.ReplanRequested);
    }

    /// <summary>
    /// A proposal made from a snapshot older than a replan request does not answer it: renewing (or activating) it
    /// must leave the request standing, or a trigger that fired while the answer was in flight is lost for good
    /// (triggers are edge-detected).
    /// </summary>
    [Fact]
    public void Proposal_from_before_the_replan_request_does_not_acknowledge_it()
    {
        Condition lowCredits = new(ConditionMetric.Credits, Comparison.Lt, 1000);
        Offer(Fx.Intent("a", "allied-boom", StrategicPosture.Boom, replan: [lowCredits]), Fx.Features(0));
        arbiter.Update(Fx.Features(21, credits: 500));
        Assert.True(arbiter.ReplanRequested);

        Assert.Equal(ArbitrationOutcome.Renewed, Offer(Fx.Intent("a2", "allied-boom", StrategicPosture.Boom, issuedAt: 20, replan: [lowCredits]), Fx.Features(21, credits: 500)).Outcome);
        Assert.True(arbiter.ReplanRequested);
        Assert.Equal("trigger", arbiter.ReplanReason);

        Assert.Equal(ArbitrationOutcome.Renewed, Offer(Fx.Intent("a3", "allied-boom", StrategicPosture.Boom, issuedAt: 21, replan: [lowCredits]), Fx.Features(21, credits: 500)).Outcome);
        Assert.False(arbiter.ReplanRequested);
    }

    private sealed class CommitPlaybooks(IReadOnlyList<Playbook> extra) : IPlaybookLibrary
    {
        public IReadOnlyList<Playbook> All => [.. Fx.Playbooks.All, .. extra];

        public bool TryGet(string id, out Playbook playbook)
        {
            Playbook? found = extra.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            if (found is not null)
            {
                playbook = found;
                return true;
            }
            return Fx.Playbooks.TryGet(id, out playbook);
        }

        public IReadOnlyList<Playbook> For(Faction faction) => All.Where(p => p.Factions.Contains(faction)).ToList();
    }
}
