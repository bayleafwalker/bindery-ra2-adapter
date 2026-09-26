// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Strategy;
using Bindery.Ra2.Bot.Tests.Arbitration;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Strategy;

/// <summary>
/// Every deterministic strategist explains itself: the rationale names the playbook it chose, the rule or model
/// that chose it, and the evidence (the feature values the choice rested on), so a post-game timeline reads as
/// reasons, not just choices.
/// </summary>
public sealed class RationaleTests
{
    private static readonly IRulesDatabase Rules = RulesDatabase.LoadEmbeddedFixture();
    private static readonly IPlaybookLibrary Playbooks = PlaybookLibrary.LoadDefault();

    private static StrategicIntent Propose(IStrategist strategist, StrategicFeatures features) =>
        strategist.ProposeAsync(new StrategistContext(features, Rules, Playbooks, null, [], null)).GetAwaiter().GetResult()!.Intent;

    private static void AssertExplains(StrategicIntent intent, string because)
    {
        Assert.False(string.IsNullOrWhiteSpace(intent.Rationale));
        Assert.Contains(intent.PlaybookId, intent.Rationale!, StringComparison.Ordinal);
        Assert.Contains(because, intent.Rationale!, StringComparison.Ordinal);
        Assert.Contains("evidence:", intent.Rationale!, StringComparison.Ordinal);
        Assert.Contains("army ratio", intent.Rationale!, StringComparison.Ordinal);
    }

    [Fact]
    public void Selector_rationale_names_the_rule_and_its_evidence()
    {
        AssertExplains(Propose(new PlaybookSelector(), Fx.Features(100)), "default mixed army");
        AssertExplains(Propose(new PlaybookSelector(), Fx.Features(100, threats: [new ThreatAssessment(Fx.R0, 3000, 1000, 0.33, 20, 0, true, 1.0)])), "base threat ratio");
    }

    [Fact]
    public void Bandit_rationale_names_the_estimate_and_its_evidence()
    {
        AssertExplains(Propose(new ContextualBanditStrategist(), Fx.Features(100)), "LinUCB");
    }

    [Fact]
    public void Distilled_rationale_names_the_model_and_its_evidence()
    {
        StrategicFeatures features = Fx.Features(100);
        DecisionDataset dataset = new([.. Enumerable.Range(0, 20).Select(i => new DecisionExample(
            FeatureVector.Version, [.. FeatureVector.Encode(Fx.Features(90 + i))], Faction.Allied, i % 2 == 0 ? "allied-ifv-mix" : "allied-boom",
            StrategicPosture.Pressure, IntentSource.Llm, "Primary", false, i, i))]);
        AssertExplains(Propose(new DistilledStrategist(dataset, new PlaybookSelector()), features), "distilled");
    }

    [Fact]
    public void Pinned_rationale_names_the_style_and_its_evidence()
    {
        PinnedPlaybookStrategist pinned = new(new Dictionary<Faction, string> { [Faction.Allied] = "allied-boom" }, "style-tech");
        AssertExplains(Propose(pinned, Fx.Features(100)), "pinned");
    }
}
