// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Arbitration;
using Bindery.Ra2.Bot.Claude;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Claude;

/// <summary>
/// The prompt must describe the arbiter the proposal will actually meet: the commitment clock it enforces, the
/// history of real plan changes, the exact acceptance rules and what each condition metric measures. Every test
/// drives the real <see cref="IntentArbiter"/> and reads the prompt the model would receive.
/// </summary>
public sealed class PromptArbitrationTests
{
    /// <summary>Fixture features without the base threat, so commitment is not overridden.</summary>
    private static StrategicFeatures Calm(double seconds) => ClaudeFixtures.Features(seconds: seconds) with { Threats = [] };

    private static StrategicIntent Intent(string id, string playbookId, StrategicPosture posture, double at, double confidence = 0.6) =>
        ClaudeFixtures.ActiveIntent(playbookId, at) with
        {
            IntentId = id,
            Source = IntentSource.Llm,
            Posture = posture,
            Confidence = confidence,
            AbortTriggers = [],
            ExpiresAt = GameTime.FromSeconds(at + 200),
        };

    private static ArbitrationDecision Offer(IntentArbiter arbiter, StrategicIntent intent, double at) =>
        arbiter.Offer(new ValidationResult(true, intent, []), Calm(at));

    private static StrategistContext Context(IntentArbiter arbiter, double at) =>
        new(Calm(at), new FakeRules(), new FakePlaybooks(), arbiter.Active, arbiter.History.ToArray(), null);

    private static JsonElement Situation(StrategistContext context, IntentPromptBuilder? builder = null)
    {
        using JsonDocument document = JsonDocument.Parse((builder ?? new IntentPromptBuilder()).Build(context, StrategistMode.Strategic).Situation);
        return document.RootElement.Clone();
    }

    [Fact]
    public void Commitment_window_counts_from_first_acceptance_across_renewals()
    {
        IntentArbiter arbiter = new(new FakePlaybooks());
        Assert.Equal(ArbitrationOutcome.Activated, Offer(arbiter, Intent("a", "allied-boom", StrategicPosture.Boom, 0), 0).Outcome);
        Assert.Equal(ArbitrationOutcome.Renewed, Offer(arbiter, Intent("a2", "allied-boom", StrategicPosture.Boom, 20), 20).Outcome);

        JsonElement at30 = Situation(Context(arbiter, 30)).GetProperty("activeIntent");
        Assert.Equal(30, at30.GetProperty("secondsActive").GetDouble());
        Assert.Equal(15, at30.GetProperty("minCommitRemainingSeconds").GetDouble());

        JsonElement at60 = Situation(Context(arbiter, 60)).GetProperty("activeIntent");
        Assert.Equal(0, at60.GetProperty("minCommitRemainingSeconds").GetDouble());
        // What the prompt says agrees with what the arbiter does: a different playbook is now accepted.
        Assert.Equal(ArbitrationOutcome.Activated, arbiter.Preview(new ValidationResult(true, Intent("b", "allied-harass", StrategicPosture.Boom, 60), []), Calm(60)).Outcome);
    }

    [Fact]
    public void A_fallback_placeholder_is_shown_as_yielding_at_once_not_as_a_commitment()
    {
        // The arbiter lets any primary proposal replace a fallback's intent immediately ("yield"); the prompt must
        // not show the model a commitment window it would never meet.
        IntentArbiter arbiter = new(new FakePlaybooks());
        Assert.Equal(ArbitrationOutcome.Activated, arbiter.Offer(new ValidationResult(true, Intent("fb", "allied-boom", StrategicPosture.Boom, 0), []), Calm(0), ProposalRole.Fallback).Outcome);
        StrategistContext context = Context(arbiter, 10) with { ActiveRole = arbiter.ActiveRole, ActiveSince = arbiter.ActiveSince };

        JsonElement active = Situation(context).GetProperty("activeIntent");
        Assert.True(active.GetProperty("placeholder").GetBoolean());
        Assert.Equal(0, active.GetProperty("minCommitRemainingSeconds").GetDouble());
        Assert.Contains("activeIntent.placeholder", IntentPromptBuilder.SystemPrompt(StrategistMode.Strategic), StringComparison.Ordinal);
        Assert.Equal(ArbitrationOutcome.Activated, arbiter.Preview(new ValidationResult(true, Intent("b", "allied-harass", StrategicPosture.Pressure, 10), []), Calm(10)).Outcome);

        // A primary's own plan keeps its window, measured on the arbiter's clock.
        IntentArbiter primary = new(new FakePlaybooks());
        Offer(primary, Intent("a", "allied-boom", StrategicPosture.Boom, 0), 0);
        JsonElement committed = Situation(Context(primary, 10) with { ActiveRole = primary.ActiveRole, ActiveSince = primary.ActiveSince }).GetProperty("activeIntent");
        Assert.False(committed.GetProperty("placeholder").GetBoolean());
        Assert.Equal(35, committed.GetProperty("minCommitRemainingSeconds").GetDouble());
    }

    [Fact]
    public void The_commitment_clock_is_the_arbiters_even_past_its_bounded_history()
    {
        // ActiveSince is the arbiter's own clock; a history walk can only see the entries the arbiter kept.
        IntentArbiter arbiter = new(new FakePlaybooks());
        Offer(arbiter, Intent("a", "allied-boom", StrategicPosture.Boom, 0), 0);
        StrategistContext context = Context(arbiter, 30) with { History = [], ActiveRole = arbiter.ActiveRole, ActiveSince = arbiter.ActiveSince };

        Assert.Equal(30, Situation(context).GetProperty("activeIntent").GetProperty("secondsActive").GetDouble());
    }

    [Fact]
    public void History_collapses_renewals_so_earlier_plan_changes_stay_visible()
    {
        IntentArbiter arbiter = new(new FakePlaybooks());
        Offer(arbiter, Intent("harass", "allied-harass", StrategicPosture.Harass, 0, 0.5), 0);
        Assert.Equal(ArbitrationOutcome.Activated, Offer(arbiter, Intent("boom", "allied-boom", StrategicPosture.Boom, 50, 0.9), 50).Outcome);
        for (int i = 1; i <= 12; i++)
        {
            Assert.Equal(ArbitrationOutcome.Renewed, Offer(arbiter, Intent($"boom{i}", "allied-boom", StrategicPosture.Boom, 50 + (5 * i), 0.9), 50 + (5 * i)).Outcome);
        }

        List<JsonElement> history = [.. Situation(Context(arbiter, 120)).GetProperty("history").EnumerateArray()];

        Assert.Equal(["allied-harass", "allied-boom"], history.Select(static h => h.GetProperty("playbookId").GetString()!).ToList());
        Assert.Equal("replaced:switch", history[0].GetProperty("endReason").GetString());
        Assert.Equal(0, history[0].GetProperty("renewals").GetInt32());
        Assert.Equal(50, history[1].GetProperty("acceptedAtSeconds").GetDouble());
        Assert.Equal(12, history[1].GetProperty("renewals").GetInt32());
        Assert.Equal(JsonValueKind.Null, history[1].GetProperty("endedAtSeconds").ValueKind);
    }

    /// <summary>
    /// The arbiter's own history used to keep one raw entry per renewal and trim to 32, so after 32 renewals
    /// (about 160 s at a 5 s cadence) the earlier plan switch was gone from the arbiter and therefore from the
    /// prompt, however the prompt folded what was left. The arbiter folds renewal chains itself.
    /// </summary>
    [Fact]
    public void An_earlier_plan_switch_survives_more_renewals_than_the_history_capacity()
    {
        IntentArbiter arbiter = new(new FakePlaybooks());
        Offer(arbiter, Intent("harass", "allied-harass", StrategicPosture.Harass, 0, 0.5), 0);
        Assert.Equal(ArbitrationOutcome.Activated, Offer(arbiter, Intent("boom", "allied-boom", StrategicPosture.Boom, 50, 0.9), 50).Outcome);
        const int renewals = 100;
        for (int i = 1; i <= renewals; i++)
        {
            Assert.Equal(ArbitrationOutcome.Renewed, Offer(arbiter, Intent($"boom{i}", "allied-boom", StrategicPosture.Boom, 50 + (5 * i), 0.9), 50 + (5 * i)).Outcome);
        }

        Assert.True(arbiter.History.Count <= arbiter.Options.HistoryCapacity);
        List<JsonElement> history = [.. Situation(Context(arbiter, 50 + (5 * renewals))).GetProperty("history").EnumerateArray()];

        Assert.Equal(["allied-harass", "allied-boom"], history.Select(static h => h.GetProperty("playbookId").GetString()!).ToList());
        Assert.Equal("replaced:switch", history[0].GetProperty("endReason").GetString());
        Assert.Equal(50, history[1].GetProperty("acceptedAtSeconds").GetDouble());
        Assert.Equal(renewals, history[1].GetProperty("renewals").GetInt32());
        Assert.Equal(JsonValueKind.Null, history[1].GetProperty("endedAtSeconds").ValueKind);
    }

    [Fact]
    public void Match_context_states_the_arbiters_acceptance_numbers()
    {
        IntentPrompt prompt = new IntentPromptBuilder().Build(ClaudeFixtures.Context(), StrategistMode.Strategic);

        using JsonDocument match = JsonDocument.Parse(prompt.MatchContext);
        Assert.True(match.RootElement.TryGetProperty("arbitration", out JsonElement arbitration));
        Assert.Equal(ArbiterOptions.Default.PostureConfidenceMargin, arbitration.GetProperty("postureConfidenceMargin").GetDouble());
        Assert.Equal(ArbiterOptions.Default.BaseThreatOverrideRatio, arbitration.GetProperty("baseThreatOverrideRatio").GetDouble());
        Assert.Equal(ArbiterOptions.Default.DefaultMinCommitSeconds, arbitration.GetProperty("defaultMinCommitSeconds").GetDouble());
        Assert.Contains("same playbookId and the same posture", prompt.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("arbitration.postureConfidenceMargin", prompt.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("arbitration.baseThreatOverrideRatio", prompt.SystemPrompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// The base-threat override is conditional: it never replaces a Defend or Turtle plan and answers one threat
    /// episode once. A prompt stating it unconditionally made the model propose switch after switch during a
    /// sustained threat, each refused as "commitment"; the prompt states the conditions and whether it is available.
    /// </summary>
    [Theory]
    [InlineData(StrategicPosture.Boom, false, true)]
    [InlineData(StrategicPosture.Boom, true, false)]
    [InlineData(StrategicPosture.Defend, false, false)]
    [InlineData(StrategicPosture.Turtle, false, false)]
    public void Prompt_says_when_the_base_threat_override_is_available(StrategicPosture posture, bool spent, bool available)
    {
        IntentArbiter arbiter = new(new FakePlaybooks());
        Offer(arbiter, Intent("a", "allied-boom", posture, 0), 0);
        StrategistContext context = Context(arbiter, 10) with
        {
            ActiveRole = arbiter.ActiveRole, ActiveSince = arbiter.ActiveSince, BaseThreatOverrideSpent = spent,
        };

        IntentPrompt prompt = new IntentPromptBuilder().Build(context, StrategistMode.Strategic);
        using JsonDocument situation = JsonDocument.Parse(prompt.Situation);
        Assert.Equal(available, situation.RootElement.GetProperty("activeIntent").GetProperty("baseThreatOverrideAvailable").GetBoolean());
        Assert.Contains("neither Defend nor Turtle", prompt.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("activeIntent.baseThreatOverrideAvailable", prompt.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Arbitration_numbers_come_from_the_options_the_runtime_uses()
    {
        ArbiterOptions options = new(DefaultMinCommitSeconds: 30, PostureConfidenceMargin: 0.2, BaseThreatOverrideRatio: 1.8);

        IntentPrompt prompt = new IntentPromptBuilder(arbiterOptions: options).Build(ClaudeFixtures.Context(), StrategistMode.Strategic);

        using JsonDocument match = JsonDocument.Parse(prompt.MatchContext);
        JsonElement arbitration = match.RootElement.GetProperty("arbitration");
        Assert.Equal(0.2, arbitration.GetProperty("postureConfidenceMargin").GetDouble());
        Assert.Equal(1.8, arbitration.GetProperty("baseThreatOverrideRatio").GetDouble());
        Assert.Equal(30, arbitration.GetProperty("defaultMinCommitSeconds").GetDouble());
    }

    [Fact]
    public void Every_condition_metric_is_defined_and_its_current_value_given()
    {
        StrategistContext context = ClaudeFixtures.Context();
        IntentPrompt prompt = new IntentPromptBuilder().Build(context, StrategistMode.Strategic, extendedMetrics: true);

        foreach (ConditionMetric metric in Enum.GetValues<ConditionMetric>())
        {
            Assert.Contains($"- {metric}", prompt.SystemPrompt, StringComparison.Ordinal);
        }
        Assert.Contains("0 when nothing threatens", prompt.SystemPrompt, StringComparison.Ordinal);

        JsonElement metrics = Situation(context).GetProperty("conditionMetrics");
        Assert.Equal(ConditionEvaluator.BaseThreatRatio(context.Features), metrics.GetProperty("BaseThreatRatio").GetDouble(), 3);
        Assert.Equal(ConditionEvaluator.ArmyValueRatio(context.Features), metrics.GetProperty("ArmyValueRatio").GetDouble(), 3);
        Assert.False(metrics.TryGetProperty("LocalForceRatio", out _));
    }
}
