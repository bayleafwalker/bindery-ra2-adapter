// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Claude;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary>An arena arm per vocabulary tier, and the adoption rule applied to their paired held-out results.</summary>
public sealed class TierArmTests
{
    private static PlayerMatchMetrics P() => new(
        Faction.Allied, 10, 0, 0, [], 1, 0, 0, new Dictionary<string, int>(), 0, 0, 0, 0, 0.1, 1000, 500, 500, 0, 0, null, 0, 5000, 10, 3, 2000, null, []);

    private static MatchRecord M(string arm, string split, int seed, int? winner) =>
        new(arm, "live-rush", split == "heldout" ? "open-steppe" : "twin-valley", split, seed, winner, "elimination", 300,
            new Dictionary<string, PlayerMatchMetrics> { ["arm"] = P(), ["opponent"] = P() });

    /// <summary>Tier 2 wins 30 held-out pairs tier 1 loses, ties 10.</summary>
    private static List<MatchRecord> Matches() =>
    [
        .. Enumerable.Range(1, 40).Select(static s => M("llm-t1", "heldout", s, s <= 30 ? 1 : 0)),
        .. Enumerable.Range(1, 40).Select(static s => M("llm-t2", "heldout", s, 0)),
    ];

    [Fact]
    public void Tier_arms_are_arms_and_tiers_expands_to_all_four()
    {
        CliOptions options = CliOptions.Parse(["run", "--arms", "tiers"]);
        Assert.Equal(["llm-t0", "llm-t1", "llm-t2", "llm-t3"], options.Arms);
        Assert.All(options.ArmSpecs(), static a => Assert.Contains(a.Name, BotAgentFactory.TierArms.Keys));
        Assert.Equal(VocabularyTier.ObjectivesAndRegions, BotAgentFactory.TierArms["llm-t2"]);
    }

    [Fact]
    public void Each_tier_arm_runs_a_claude_strategist_at_its_tier()
    {
        BotAgentFactory factory = new(RulesDatabase.LoadEmbeddedFixture(), PlaybookLibrary.LoadDefault(), new ArenaRunContext(llmFake: true, llmLatencySeconds: null));
        using IArenaAgent agent = factory.Create(new ArmSpec("llm-t0", false, true), new PlayerId(0), Faction.Allied, Bindery.Ra2.Bot.Sim.SimMaps.TwinValley.Map, 1);

        ClaudeStrategist claude = Assert.Single(((BotArenaAgent)agent).ClaudeStrategists);
        Assert.Equal(VocabularyTier.PlaybookOnly, claude.Options.Vocabulary);
        Assert.Contains("vocabulary:PlaybookOnly", agent.Stats.Labels);
    }

    [Fact]
    public void Shadow_tier_arms_play_the_selector_and_shadow_a_claude_strategist_at_their_tier()
    {
        CliOptions options = CliOptions.Parse(["run", "--arms", "llm-shadow-t3"]);
        ArmSpec spec = Assert.Single(options.ArmSpecs());
        Assert.True(spec.UsesLlm);

        BotAgentFactory factory = new(RulesDatabase.LoadEmbeddedFixture(), PlaybookLibrary.LoadDefault(), new ArenaRunContext(llmFake: true, llmLatencySeconds: null));
        using IArenaAgent agent = factory.Create(spec, new PlayerId(0), Faction.Allied, Bindery.Ra2.Bot.Sim.SimMaps.TwinValley.Map, 1);

        ClaudeStrategist claude = Assert.Single(((BotArenaAgent)agent).ClaudeStrategists);
        Assert.Equal(VocabularyTier.Full, claude.Options.Vocabulary);
        Assert.Contains("shadow-vocabulary:Full", agent.Stats.Labels);
    }

    [Fact]
    public void Fake_evidence_never_adopts_a_tier_even_when_it_wins()
    {
        VocabularyAdoption adoption = Program.TierAdoption(Matches(), live: false, "2026-09-26");

        TierEvidence evidence = Assert.Single(adoption.Evidence);
        Assert.Equal((VocabularyTier.ObjectivesAndRegions, VocabularyTier.Parameters, 40, 30, 0), (evidence.Tier, evidence.AgainstTier, evidence.Pairs, evidence.Better, evidence.Worse));
        Assert.False(evidence.Live);
        Assert.Equal(VocabularyTier.Parameters, adoption.AdoptedTier);
    }

    [Fact]
    public void Live_evidence_of_a_significant_held_out_win_adopts_the_tier()
    {
        Assert.Equal(VocabularyTier.ObjectivesAndRegions, Program.TierAdoption(Matches(), live: true, "2026-09-26").AdoptedTier);
    }

    [Fact]
    public void The_report_has_a_tier_section_with_the_verdict()
    {
        string report = ReportBuilder.Build(Matches(), [], [], CliOptions.Parse(["run", "--arms", "llm-t1,llm-t2", "--llm-fake"]), "test");

        Assert.Contains("## Vocabulary tiers (build step 6)", report, StringComparison.Ordinal);
        Assert.Contains("### llm-t2 vs llm-t1", report, StringComparison.Ordinal);
        Assert.Contains("Adopted tier: Parameters", report, StringComparison.Ordinal);
        Assert.Contains("fake-client evidence is not evidence", report, StringComparison.Ordinal);
    }
}
