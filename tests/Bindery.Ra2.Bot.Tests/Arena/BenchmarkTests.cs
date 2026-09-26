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
}
