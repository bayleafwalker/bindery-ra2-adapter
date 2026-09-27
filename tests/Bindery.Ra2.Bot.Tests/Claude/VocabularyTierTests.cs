// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Claude;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Claude;

/// <summary>
/// Build step 6: the LLM's vocabulary widens in named tiers, and a tier above "select and parameterise" is used
/// only once a paired held-out comparison with a live model shows it beats the tier below.
/// </summary>
public sealed class VocabularyTierTests
{
    private static readonly IRulesDatabase Rules = RulesDatabase.LoadEmbeddedFixture();
    private static readonly IPlaybookLibrary Playbooks = PlaybookLibrary.LoadDefault();

    /// <summary>A proposal that sets every field away from the playbook's defaults.</summary>
    private static StrategicIntent Proposed(StrategicFeatures features) => new(
        "llm/1", IntentSource.Llm, features.SnapshotVersion, features.Time, features.Time.Plus(90), StrategicPosture.AllIn, "allied-grizzly-timing",
        new Dictionary<string, double> { ["attackArmyValue"] = 900 },
        [new Objective(ObjectiveKind.AttackRegion, new RegionId(3), null, 1)],
        new BudgetShares(0.1, 0.8, 0.05, 0.05),
        [new CompositionTarget(UnitRole.AntiInfantry, 0.5, 0.9)],
        [new RegionId(4)],
        [new Condition(ConditionMetric.GameSeconds, Comparison.Ge, 200)],
        [new Condition(ConditionMetric.LossesValue15s, Comparison.Ge, 2000)],
        [new Condition(ConditionMetric.Credits, Comparison.Lt, 100)],
        0.8, ["assume"], "because");

    private static Playbook Grizzly() => Playbooks.All.Single(static p => p.Id == "allied-grizzly-timing");

    [Fact]
    public void Playbook_only_keeps_the_choice_and_replaces_everything_else_with_defaults()
    {
        StrategicFeatures features = ClaudeFixtures.Features();
        (StrategicIntent r, IReadOnlyList<string> dropped) = IntentVocabulary.Restrict(Proposed(features), VocabularyTier.PlaybookOnly, Playbooks, features);

        Assert.Equal("allied-grizzly-timing", r.PlaybookId);
        Assert.Equal(Grizzly().Posture, r.Posture);
        Assert.Equal(Grizzly().Parameters.Single().Default, r.PlaybookParameters["attackArmyValue"]);
        Assert.Equal(Grizzly().Budget, r.Budget);
        Assert.Equal(Grizzly().Composition, r.Composition);
        Assert.Empty(r.RegionsOfInterest);
        Assert.Empty(r.ReplanTriggers);
        Assert.Equal(("llm/1", IntentSource.Llm, 0.8, "because"), (r.IntentId, r.Source, r.Confidence, r.Rationale));
        Assert.Equal(features.Time.Plus(90), r.ExpiresAt);
        Assert.Equal(["abortTriggers", "attackConditions", "budget", "composition", "objectives", "parameters", "posture", "regionsOfInterest", "replanTriggers"], dropped);
    }

    [Fact]
    public void Each_tier_adds_its_fields_on_top_of_the_tier_below()
    {
        StrategicFeatures features = ClaudeFixtures.Features();
        StrategicIntent proposed = Proposed(features);

        (StrategicIntent t1, _) = IntentVocabulary.Restrict(proposed, VocabularyTier.Parameters, Playbooks, features);
        Assert.Equal(900, t1.PlaybookParameters["attackArmyValue"]);
        Assert.Empty(t1.RegionsOfInterest);
        Assert.Equal(Grizzly().Budget, t1.Budget);

        (StrategicIntent t2, _) = IntentVocabulary.Restrict(proposed, VocabularyTier.ObjectivesAndRegions, Playbooks, features);
        Assert.Equal(900, t2.PlaybookParameters["attackArmyValue"]);
        Assert.Equal(proposed.Objectives, t2.Objectives);
        Assert.Equal(proposed.RegionsOfInterest, t2.RegionsOfInterest);
        Assert.Equal(Grizzly().Composition, t2.Composition);
        Assert.Equal(Grizzly().Budget, t2.Budget);

        (StrategicIntent t3, IReadOnlyList<string> none) = IntentVocabulary.Restrict(proposed, VocabularyTier.Full, Playbooks, features);
        Assert.Same(proposed, t3);
        Assert.Empty(none);
    }

    [Fact]
    public async Task The_strategist_applies_its_tier()
    {
        StrategicFeatures features = ClaudeFixtures.Features(seconds: 30);
        StrategistContext context = new(features, Rules, Playbooks, null, [], null);
        ClaudeStrategist narrow = new(new Bindery.Ra2.Bot.Arena.FakeMessageClient(), new ClaudeStrategistOptions { Vocabulary = VocabularyTier.PlaybookOnly });
        ClaudeStrategist wide = new(new Bindery.Ra2.Bot.Arena.FakeMessageClient(), new ClaudeStrategistOptions { Vocabulary = VocabularyTier.Full });

        StrategicIntent a = (await narrow.ProposeAsync(context))!.Intent;
        StrategicIntent b = (await wide.ProposeAsync(context))!.Intent;

        Assert.Equal(a.PlaybookId, b.PlaybookId);
        Playbook playbook = Playbooks.All.Single(p => p.Id == a.PlaybookId);
        Assert.All(playbook.Parameters, p => Assert.Equal(p.Default, a.PlaybookParameters[p.Name]));
        Assert.NotEqual(a.AbortTriggers.Count, b.AbortTriggers.Count);
        Assert.Contains(VocabularyTier.PlaybookOnly.ToString(), narrow.BuildRequest(context).SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void System_prompt_names_the_tier_and_stays_byte_stable()
    {
        foreach (VocabularyTier tier in Enum.GetValues<VocabularyTier>())
        {
            string strategic = IntentPromptBuilder.SystemPrompt(StrategistMode.Strategic, tier);
            Assert.Equal(strategic, IntentPromptBuilder.SystemPrompt(StrategistMode.Strategic, tier));
            Assert.Contains($"Vocabulary tier: {tier}", strategic, StringComparison.Ordinal);
            Assert.StartsWith(strategic, IntentPromptBuilder.SystemPrompt(StrategistMode.Refine, tier), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Without_adopted_evidence_the_strategist_selects_and_parameterises()
    {
        Assert.Equal(VocabularyTier.Parameters, VocabularyAdoption.Embedded.AdoptedTier);
        Assert.Equal(VocabularyTier.Parameters, new ClaudeStrategistOptions().Vocabulary);
    }

    private static TierEvidence Evidence(VocabularyTier tier, bool live = true, int better = 30, int worse = 10, double low = 0.05, double high = 0.4) =>
        new(tier, (VocabularyTier)((int)tier - 1), "heldout", 60, 0.5, 0.7, 0.2, low, high, better, worse, 20, 0.002, live);

    [Fact]
    public void A_tier_is_adopted_only_on_a_live_significant_held_out_win_over_the_tier_below()
    {
        Assert.Equal(VocabularyTier.ObjectivesAndRegions, VocabularyAdoption.Decide([Evidence(VocabularyTier.ObjectivesAndRegions)]).AdoptedTier);
        Assert.Equal(VocabularyTier.Full, VocabularyAdoption.Decide([Evidence(VocabularyTier.ObjectivesAndRegions), Evidence(VocabularyTier.Full)]).AdoptedTier);
        // Full beating ObjectivesAndRegions means nothing while ObjectivesAndRegions itself is not adopted.
        Assert.Equal(VocabularyTier.Parameters, VocabularyAdoption.Decide([Evidence(VocabularyTier.Full)]).AdoptedTier);
        // Fake-client evidence is never evidence.
        Assert.Equal(VocabularyTier.Parameters, VocabularyAdoption.Decide([Evidence(VocabularyTier.ObjectivesAndRegions, live: false)]).AdoptedTier);
        // An interval touching 0, or more losses than wins, is not a win.
        Assert.Equal(VocabularyTier.Parameters, VocabularyAdoption.Decide([Evidence(VocabularyTier.ObjectivesAndRegions, low: -0.01)]).AdoptedTier);
        Assert.Equal(VocabularyTier.Parameters, VocabularyAdoption.Decide([Evidence(VocabularyTier.ObjectivesAndRegions, better: 10, worse: 30)]).AdoptedTier);
        // Evidence from training maps does not count.
        Assert.Equal(VocabularyTier.Parameters, VocabularyAdoption.Decide([Evidence(VocabularyTier.ObjectivesAndRegions) with { Split = "training" }]).AdoptedTier);
    }
}
