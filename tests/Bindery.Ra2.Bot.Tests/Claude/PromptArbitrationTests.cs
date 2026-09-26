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

}
