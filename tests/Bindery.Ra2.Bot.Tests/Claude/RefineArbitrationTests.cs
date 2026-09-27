// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arbitration;
using Bindery.Ra2.Bot.Claude;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Claude;

/// <summary>
/// Refine mode may only re-parameterise the strategic model's own plan. Offered to the real arbiter, a refinement
/// must not promote a fallback placeholder into a committed plan, raise the incumbent confidence that posture
/// hysteresis measures against, or bring back a plan that expired while the refinement was in flight.
/// </summary>
public sealed class RefineArbitrationTests
{
    private static StrategicFeatures Calm(double seconds, long version = 7) =>
        ClaudeFixtures.Features(version: version, seconds: seconds) with { Threats = [], Events = [] };

    private static StrategicIntent Plan(double issuedAt, double lifetime, IntentSource source, double confidence = 0.6) =>
        ClaudeFixtures.ActiveIntent("allied-boom", issuedAt) with
        {
            IntentId = $"plan/{issuedAt}",
            Source = source,
            ExpiresAt = GameTime.FromSeconds(issuedAt + lifetime),
            AbortTriggers = [],
            Confidence = confidence,
        };

    private static ClaudeStrategist Refiner(double confidence, out FakeMessageClient client)
    {
        IntentDraft draft = ClaudeFixtures.Draft(playbookId: "allied-boom", posture: "Boom", aggression: 0.9) with
        {
            Confidence = confidence,
            AbortTriggers = [],
        };
        client = FakeMessageClient.Replying(ClaudeFixtures.DraftJson(draft), modelId: "claude-haiku-4-5");
        return new ClaudeStrategist(client, ClaudeStrategistOptions.ForRefine());
    }

    private static StrategistContext Context(StrategicFeatures features, IntentArbiter arbiter) =>
        new(features, new FakeRules(), new FakePlaybooks(), arbiter.Active, arbiter.History.ToArray(), null);

    [Fact]
    public async Task Refine_does_not_touch_a_fallback_placeholder_so_the_strategic_model_can_replace_it()
    {
        IntentArbiter arbiter = new(new FakePlaybooks());
        arbiter.Offer(new ValidationResult(true, Plan(0, 120, IntentSource.Selector), []), Calm(0), ProposalRole.Fallback);
        ClaudeStrategist refiner = Refiner(0.95, out FakeMessageClient client);

        StrategistProposal? refined = await refiner.ProposeAsync(Context(Calm(5), arbiter));

        Assert.Null(refined);
        Assert.Equal(ClaudeFailureCodes.RefineNotStrategicPlan, refiner.LastFailure?.Code);
        Assert.Empty(client.Requests);
        Assert.Equal(ProposalRole.Fallback, arbiter.ActiveRole);
        StrategicIntent turtle = Plan(10, 120, IntentSource.Llm, 0.9) with { PlaybookId = "allied-harass", Posture = StrategicPosture.Harass, IntentId = "slow/10" };
        Assert.Equal("yield", arbiter.Offer(new ValidationResult(true, turtle, []), Calm(10)).Reason);
    }

    [Fact]
    public async Task Refine_keeps_the_strategic_models_confidence()
    {
        IntentArbiter arbiter = new(new FakePlaybooks());
        arbiter.Offer(new ValidationResult(true, Plan(0, 120, IntentSource.Llm, 0.6), []), Calm(0));
        ClaudeStrategist refiner = Refiner(0.95, out _);

        StrategistProposal? refined = await refiner.ProposeAsync(Context(Calm(5), arbiter));

        Assert.NotNull(refined);
        Assert.Equal(0.6, refined.Intent.Confidence);
        Assert.Equal(0.9, refined.Intent.PlaybookParameters["aggression"]);
        Assert.Equal(ArbitrationOutcome.Renewed, arbiter.Offer(new ValidationResult(true, refined.Intent, []), Calm(6)).Outcome);
        // A posture change 0.2 above the strategic model's own confidence still clears the 0.15 margin.
        StrategicIntent harass = Plan(100, 120, IntentSource.Llm, 0.8) with { PlaybookId = "allied-harass", Posture = StrategicPosture.Harass, IntentId = "slow/100" };
        Assert.Equal(ArbitrationOutcome.Activated, arbiter.Offer(new ValidationResult(true, harass, []), Calm(100)).Outcome);
    }

    [Fact]
    public async Task A_refinement_of_a_plan_that_expired_in_flight_is_refused()
    {
        IntentArbiter arbiter = new(new FakePlaybooks());
        arbiter.Offer(new ValidationResult(true, Plan(0, 28, IntentSource.Llm), []), Calm(0));
        ClaudeStrategist refiner = Refiner(0.7, out _);

        // Request sent at t=25 while the plan is still active; the reply lands after it expired.
        StrategistProposal? refined = await refiner.ProposeAsync(Context(Calm(25, 25), arbiter));
        Assert.NotNull(refined);
        Assert.True(refined.Intent.ExpiresAt <= GameTime.FromSeconds(28));

        arbiter.Update(Calm(28));
        arbiter.Offer(new ValidationResult(true, Plan(28, 60, IntentSource.Selector) with { PlaybookId = "allied-harass", Posture = StrategicPosture.Harass }, []), Calm(28), ProposalRole.Fallback);
        ArbitrationDecision late = arbiter.Offer(new ValidationResult(true, refined.Intent, []), Calm(30));

        Assert.Equal(ArbitrationOutcome.Refused, late.Outcome);
        Assert.Equal("allied-harass", arbiter.Active?.PlaybookId);
        Assert.Equal(ProposalRole.Fallback, arbiter.ActiveRole);
    }
}
