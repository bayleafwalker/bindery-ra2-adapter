// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arbitration;
using Bindery.Ra2.Bot.Claude;
using Bindery.Ra2.Bot.Playbooks;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Claude;

/// <summary>
/// The planner attacks only when every attack condition holds and army value reaches <c>attackArmyValue</c>, and
/// the timing playbooks also carry that threshold as an <c>OwnArmyValue</c> attack condition. When the LLM may set
/// the parameter but not the conditions (Parameters tier, Refine mode), lowering the parameter must lower the
/// matching condition too, or the change silently does nothing.
/// </summary>
public sealed class AttackThresholdTests
{
    private static readonly IPlaybookLibrary Playbooks = PlaybookLibrary.LoadDefault();

    private static StrategicIntent Grizzly(StrategicFeatures features, double attackArmyValue, IntentSource source = IntentSource.Llm)
    {
        Playbook playbook = Playbooks.All.Single(static p => p.Id == "allied-grizzly-timing");
        StrategicIntent intent = PlaybookIntents.FromPlaybook(playbook, features, "grizzly/1", source, 120, 0.7);
        return intent with { PlaybookParameters = new Dictionary<string, double> { ["attackArmyValue"] = attackArmyValue } };
    }

    [Fact]
    public void Parameters_tier_moves_the_playbooks_army_value_attack_condition_with_the_parameter()
    {
        StrategicFeatures features = ClaudeFixtures.Features();

        (StrategicIntent restricted, _) = IntentVocabulary.Restrict(Grizzly(features, 900), VocabularyTier.Parameters, Playbooks, features);

        Assert.Equal(900, restricted.PlaybookParameters["attackArmyValue"]);
        Condition army = Assert.Single(restricted.AttackConditions, static c => c.Metric == ConditionMetric.OwnArmyValue);
        Assert.Equal(900, army.Threshold);
    }

    [Fact]
    public async Task Refine_moves_the_active_intents_army_value_attack_condition_with_the_parameter()
    {
        StrategicFeatures features = ClaudeFixtures.Features(seconds: 300) with { Threats = [] };
        StrategicIntent active = Grizzly(features, 1500);
        Assert.Equal(1500, Assert.Single(active.AttackConditions, static c => c.Metric == ConditionMetric.OwnArmyValue).Threshold);
        IntentDraft draft = ClaudeFixtures.Draft(playbookId: "allied-grizzly-timing", posture: "Pressure") with
        {
            Parameters = [new DraftParameter { Name = "attackArmyValue", Value = 900 }],
            AbortTriggers = [],
        };
        ClaudeStrategist refiner = new(FakeMessageClient.Replying(ClaudeFixtures.DraftJson(draft)), ClaudeStrategistOptions.ForRefine());
        StrategistContext context = new(features, new FakeRules(), Playbooks, active, [], null);

        StrategistProposal? refined = await refiner.ProposeAsync(context);

        Assert.NotNull(refined);
        Assert.Equal(900, refined.Intent.PlaybookParameters["attackArmyValue"]);
        Assert.Equal(900, Assert.Single(refined.Intent.AttackConditions, static c => c.Metric == ConditionMetric.OwnArmyValue).Threshold);
    }

    [Fact]
    public void A_condition_the_strategist_set_apart_from_the_parameter_is_left_alone()
    {
        IReadOnlyList<Condition> conditions = [new Condition(ConditionMetric.OwnArmyValue, Comparison.Ge, 1200), new Condition(ConditionMetric.ArmyValueRatio, Comparison.Gt, 1.2)];

        Assert.Equal(conditions, AttackArmyThreshold.Retarget(conditions, 1500, 900));
        Assert.Equal(conditions, AttackArmyThreshold.Retarget(conditions, null, 900));
    }
}
