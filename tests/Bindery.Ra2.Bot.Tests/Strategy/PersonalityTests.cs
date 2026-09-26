// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Claude;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Strategy;
using Bindery.Ra2.Bot.Tests.Arbitration;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Strategy;

/// <summary>Personalities change what every strategist does, not only what the LLM reads.</summary>
public sealed class PersonalityTests
{
    private static readonly IRulesDatabase Rules = RulesDatabase.LoadEmbeddedFixture();
    private static readonly IPlaybookLibrary Playbooks = PlaybookLibrary.LoadDefault();

    private static StrategistContext Context(StrategicFeatures features, string? personality) => new(features, Rules, Playbooks, null, [], personality);

    private static StrategicIntent Propose(IStrategist strategist, StrategicFeatures features, string? personality) =>
        strategist.ProposeAsync(Context(features, personality)).GetAwaiter().GetResult()!.Intent;

    [Fact]
    public void The_authored_set_has_four_styles_with_guidance_and_a_playbook_per_faction()
    {
        Assert.Equal(["aggressive", "turtle", "tech", "harasser"], Personalities.All.Select(static p => p.Id));
        foreach (PersonalityProfile p in Personalities.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.PromptGuidance));
            foreach (Faction faction in new[] { Faction.Allied, Faction.Soviet })
            {
                Assert.True(Playbooks.TryGet(p.PreferredPlaybook[faction], out Playbook playbook));
                Assert.Contains(faction, playbook.Factions);
            }
        }
        Assert.Equal(Personalities.All.Count, Personalities.All.Select(static p => p.PreferredPlaybook[Faction.Allied]).Distinct().Count());
    }

    [Theory]
    [InlineData(null, "allied-ifv-mix")]
    [InlineData("aggressive", "allied-grizzly-timing")]
    [InlineData("turtle", "allied-prism-turtle")]
    [InlineData("tech", "allied-boom")]
    [InlineData("harasser", "allied-harass")]
    public void The_selector_plays_the_personalitys_playbook_when_no_rule_forces_another(string? personality, string expected)
    {
        StrategicIntent intent = Propose(new PlaybookSelector(), Fx.Features(100), personality);

        Assert.Equal(expected, intent.PlaybookId);
        if (personality is not null) Assert.Contains($"personality {personality}", intent.Rationale!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_selector_scales_parameters_and_the_defence_threshold_by_personality()
    {
        StrategicIntent aggressive = Propose(new PlaybookSelector(), Fx.Features(100), "aggressive");
        Playbook grizzly = Playbooks.All.Single(static p => p.Id == "allied-grizzly-timing");
        Assert.Equal(grizzly.Parameters.Single().Default * 0.7, aggressive.PlaybookParameters["attackArmyValue"], 3);

        // Base threat 1.5: the default selector defends at 1.3, the aggressive style only at 1.6, the turtle at 1.0.
        StrategicFeatures threatened = Fx.Features(100, threats: [new ThreatAssessment(Fx.R0, 1500, 1000, 0.67, 20, 0, true, 1.0)]);
        Assert.Equal("generic-defend", Propose(new PlaybookSelector(), threatened, null).PlaybookId);
        Assert.Equal("allied-grizzly-timing", Propose(new PlaybookSelector(), threatened, "aggressive").PlaybookId);
        Assert.Equal("generic-defend", Propose(new PlaybookSelector(), threatened, "turtle").PlaybookId);
    }

    [Fact]
    public void The_bandit_leans_toward_the_personalitys_playbook()
    {
        // An untrained bandit scores every playbook equally, so the bonus decides.
        Assert.NotEqual("allied-prism-turtle", Propose(new ContextualBanditStrategist(), Fx.Features(100), null).PlaybookId);
        Assert.Equal("allied-prism-turtle", Propose(new ContextualBanditStrategist(), Fx.Features(100), "turtle").PlaybookId);
    }

    [Fact]
    public void The_distilled_model_leans_toward_the_personalitys_playbook()
    {
        DecisionDataset dataset = new([.. Enumerable.Range(0, 30).Select(i => new DecisionExample(
            FeatureVector.Version, [.. FeatureVector.Encode(Fx.Features(90 + i))], Faction.Allied, i % 3 == 0 ? "allied-boom" : "allied-ifv-mix",
            StrategicPosture.Pressure, IntentSource.Llm, "Primary", false, i, i))]);
        DistilledStrategist distilled = new(dataset, new PlaybookSelector());

        Assert.Equal("allied-ifv-mix", Propose(distilled, Fx.Features(100), null).PlaybookId);
        Assert.Equal("allied-boom", Propose(distilled, Fx.Features(100), "tech").PlaybookId);
        // The aggressive style's playbook is not a trained class, so there is nothing to lean toward.
        Assert.Equal("allied-ifv-mix", Propose(distilled, Fx.Features(100), "aggressive").PlaybookId);
    }

    [Fact]
    public void The_prompt_carries_the_authored_guidance_not_just_the_name()
    {
        IntentPrompt prompt = new IntentPromptBuilder().Build(Context(Fx.Features(100), "harasser"), StrategistMode.Strategic);

        using JsonDocument match = JsonDocument.Parse(prompt.MatchContext);
        JsonElement personality = match.RootElement.GetProperty("personality");
        Assert.Equal("harasser", personality.GetProperty("id").GetString());
        Assert.Equal(Personalities.Harasser.PromptGuidance, personality.GetProperty("guidance").GetString());
        Assert.Equal("allied-harass", personality.GetProperty("preferredPlaybook").GetString());
    }

    [Fact]
    public void A_free_text_personality_still_reaches_the_prompt_as_text()
    {
        IntentPrompt prompt = new IntentPromptBuilder().Build(Context(Fx.Features(100), "cautious but greedy"), StrategistMode.Strategic);

        using JsonDocument match = JsonDocument.Parse(prompt.MatchContext);
        Assert.Equal("cautious but greedy", match.RootElement.GetProperty("personality").GetString());
    }
}
