// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Claude;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Sim;
using Bindery.Ra2.Bot.Strategy;
using Bindery.Ra2.Bot.Tests.Claude;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary>
/// The benchmark must be able to tell arms apart: a contested setting, a fake LLM policy that is not the selector,
/// and pairing by opponent, map and seed.
/// </summary>
public sealed class BenchmarkTests
{
    private static readonly IRulesDatabase Rules = RulesDatabase.LoadEmbeddedFixture();
    private static readonly IPlaybookLibrary Playbooks = PlaybookLibrary.LoadDefault();

    /// <summary>An opening with nothing known about the enemy and no threat.</summary>
    private static StrategistContext Opening(double seconds)
    {
        StrategicFeatures f = ClaudeFixtures.Features(seconds: seconds, enemyTech: new HashSet<string>(StringComparer.Ordinal));
        f = f with
        {
            Enemy = f.Enemy with { CompositionByRole = new Dictionary<UnitRole, double>(), ArmyValueConfidence = 0, EstimatedArmyValue = Trend.Flat(0) },
            Threats = [],
        };
        return new StrategistContext(f, Rules, Playbooks, null, [], null, new HashSet<string>(StringComparer.Ordinal) { "GAYARD" });
    }

    [Fact]
    public async Task The_fake_llm_policy_is_not_the_selector()
    {
        StrategistContext context = Opening(30);
        ClaudeStrategist llm = new(new Bindery.Ra2.Bot.Arena.FakeMessageClient());

        StrategistProposal? fake = await llm.ProposeAsync(context);
        (Playbook? selected, _, _) = new PlaybookSelector().Choose(context.Features, Playbooks, Rules);

        Assert.NotNull(fake);
        Assert.NotNull(selected);
        Assert.NotEqual(selected!.Id, fake!.Intent.PlaybookId);
        Assert.Equal("allied-grizzly-timing", fake.Intent.PlaybookId);
        Assert.Equal("allied-ifv-mix", selected.Id);
    }

    [Fact]
    public void Contested_benchmark_defaults_to_live_mirror_and_held_out_opponents_with_a_faction_handicap_and_noise()
    {
        CliOptions options = CliOptions.Parse(["run", "--benchmark", "contested"]);

        Assert.Equal(BenchmarkSettings.Contested, options.Benchmark);
        Assert.All(options.Opponents, static o => Assert.True(o.StartsWith(BotAgentFactory.LivePrefix, StringComparison.Ordinal) || OpponentSets.IsHeldOut(o), o));
        Assert.Equal(5, options.Opponents.Count(static o => o.StartsWith(BotAgentFactory.LivePrefix, StringComparison.Ordinal)));
        SimSettings allied = options.Benchmark.ToSimSettings(1, 600, new PlayerId(0), Faction.Allied, new PlayerId(1), Faction.Soviet);
        Assert.Equal(20_000, allied.Players[0].StartingCredits);
        Assert.Null(allied.Players[1].StartingCredits);
        SimSettings soviet = options.Benchmark.ToSimSettings(2, 600, new PlayerId(0), Faction.Soviet, new PlayerId(1), Faction.Allied);
        Assert.Equal(20_000, soviet.Players[1].StartingCredits);
        Assert.Equal(0.25, soviet.CombatNoise);
    }

    [Fact]
    public void Overriding_one_knob_keeps_the_rest_of_the_preset()
    {
        CliOptions options = CliOptions.Parse(["run", "--benchmark", "contested", "--combat-noise", "0.1"]);

        Assert.Equal(0.1, options.Benchmark.CombatNoise);
        Assert.Equal(20_000, options.Benchmark.AlliedStartingCredits);
        Assert.Equal(BenchmarkSettings.Contested.DefaultOpponents, options.Opponents);
    }

    [Fact]
    public void Pairs_match_opponent_map_and_seed_only()
    {
        static MatchRecord M(string arm, string opponent, string map, int seed, int? winner) =>
            new(arm, opponent, map, "training", seed, winner, "elimination", 100, new Dictionary<string, PlayerMatchMetrics>());
        List<MatchRecord> matches =
        [
            M("selector", "live-rush", "twin-valley", 1, 0), M("selector", "live-rush", "twin-valley", 2, 1), M("selector", "live-tech", "twin-valley", 1, 0),
            M("llm", "live-rush", "twin-valley", 2, 0), M("llm", "live-rush", "twin-valley", 1, 1), M("llm", "live-tech", "river-crossing", 1, 1),
        ];

        IReadOnlyList<(MatchRecord Baseline, MatchRecord Arm)> pairs = PairedReport.Pairs(matches, "selector", "llm");

        Assert.Equal(2, pairs.Count);
        Assert.All(pairs, static p => Assert.Equal((p.Baseline.Opponent, p.Baseline.Map, p.Baseline.Seed), (p.Arm.Opponent, p.Arm.Map, p.Arm.Seed)));
    }

    private static PlayerMatchMetrics Metrics(string? hash, int finalAssets) => new(
        Faction.Allied, 1, 0, 0, [], 1, 0, 0, new Dictionary<string, int>(), 0, 0, 0, 0, 0.1, 1000, 500, 500, 0, 0, null, 0, finalAssets, 10, 3, 2000, hash, []);

    private static MatchRecord Game(string arm, string opponent, int seed, string hash, int? winner = 0, int finalAssets = 5000) =>
        new(arm, opponent, "twin-valley", "training", seed, winner, "elimination", 250,
            new Dictionary<string, PlayerMatchMetrics> { ["arm"] = Metrics(hash, finalAssets), ["opponent"] = Metrics("opp", 0) });

    [Fact]
    public void Identical_games_against_differently_named_opponents_count_once_in_pairs_and_win_rates()
    {
        // ai-rush and ai-balanced play the same game on seed 1 (same arm log and outcome); seed 2 differs.
        List<MatchRecord> matches =
        [
            Game("selector", "ai-rush", 1, "s1"), Game("selector", "ai-balanced", 1, "s1"),
            Game("llm", "ai-rush", 1, "l1"), Game("llm", "ai-balanced", 1, "l1"),
            Game("selector", "ai-rush", 2, "s2"), Game("selector", "ai-balanced", 2, "s2b", winner: 1),
            Game("llm", "ai-rush", 2, "l2"), Game("llm", "ai-balanced", 2, "l2b"),
        ];

        IReadOnlyList<(MatchRecord Baseline, MatchRecord Arm)> pairs = PairedReport.Pairs(matches, "selector", "llm", null, out int duplicates);
        Assert.Equal(3, pairs.Count);
        Assert.Equal(1, duplicates);
        Assert.Equal(3, PairedReport.DistinctGames(matches.Where(static m => m.Arm == "selector")).Count);

        string report = ReportBuilder.Build(matches, [], [], CliOptions.Parse(["run", "--arms", "selector,llm", "--llm-fake", "--seeds", "2"]), "test");
        Assert.Contains("| selector | training | 3 | 1 | 0 | 4 | 0.750 |", report, StringComparison.Ordinal);
        Assert.Contains("| 3 | 2 |", report, StringComparison.Ordinal);
        Assert.Contains("- selector: ai-balanced = ai-rush on 1 map × seed cell", report, StringComparison.Ordinal);
        Assert.Contains("(3 pairs; 1 identical to another opponent's pair collapsed)", report, StringComparison.Ordinal);
    }

    [Fact]
    public void An_odd_seed_count_is_flagged_as_an_unbalanced_faction_mix()
    {
        List<MatchRecord> matches = [Game("selector", "ai-rush", 1, "a"), Game("selector", "ai-rush", 2, "b"), Game("selector", "ai-rush", 3, "c")];

        string report = ReportBuilder.Build(matches, [], [], CliOptions.Parse(["run", "--arms", "selector", "--seeds", "3"]), "test");

        Assert.Contains("| 2/2 | 1/1 |", report, StringComparison.Ordinal);
        Assert.Contains("Warning: the faction mix is unbalanced for selector/training (2 Allied, 1 Soviet)", report, StringComparison.Ordinal);
    }
}
