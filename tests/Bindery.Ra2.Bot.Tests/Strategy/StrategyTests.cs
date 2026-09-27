// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arbitration;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Runtime;
using Bindery.Ra2.Bot.Strategy;
using Bindery.Ra2.Bot.Tests.Arbitration;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Strategy;

public sealed class StrategyTests
{
    private static readonly IRulesDatabase Rules = RulesDatabase.LoadEmbeddedFixture();
    private static readonly IPlaybookLibrary Playbooks = PlaybookLibrary.LoadDefault();

    private static StrategistContext Context(StrategicFeatures features, StrategicIntent? active = null) =>
        new(features, Rules, Playbooks, active, [], null);

    private static ThreatAssessment BaseThreat(double enemy, double own) =>
        new(Fx.R0, enemy, own, enemy <= 0 ? 10 : own / enemy, 20, 0, IsBase: true, 1.0);

    private static StrategicIntent Propose(IStrategist strategist, StrategicFeatures features, StrategicIntent? active = null) =>
        strategist.ProposeAsync(Context(features, active)).GetAwaiter().GetResult()!.Intent;

    [Fact]
    public void Feature_vector_matches_its_documented_names_and_stays_finite()
    {
        StrategicFeatures features = Fx.Features(120) with
        {
            Enemy = Fx.Features(120).Enemy with { NewestObservationAgeSeconds = double.PositiveInfinity },
        };
        double[] v = FeatureVector.Encode(features);
        Assert.Equal(FeatureVector.Names.Count, v.Length);
        Assert.Equal(FeatureVector.Dimension, v.Length);
        Assert.All(v, static x => Assert.True(double.IsFinite(x)));
        Assert.Equal(1.0, v[0]);
        Assert.Equal(4.0, v[FeatureVector.Names.ToList().IndexOf("enemyObservationAge2m")]);
    }

    [Fact]
    public void Selector_defends_under_base_threat_and_otherwise_plays_the_faction_mix()
    {
        PlaybookSelector selector = new();
        Assert.Equal("generic-defend", Propose(selector, Fx.Features(100, threats: [BaseThreat(3000, 1000)])).PlaybookId);
        Assert.Equal("allied-ifv-mix", Propose(selector, Fx.Features(100)).PlaybookId);
        Assert.Equal("soviet-flak-mix", Propose(selector, Fx.Features(100, faction: Faction.Soviet)).PlaybookId);
    }

    [Fact]
    public void Selector_intents_pass_the_real_validator()
    {
        IntentValidator validator = new();
        foreach (Faction faction in new[] { Faction.Allied, Faction.Soviet })
        {
            StrategicFeatures features = Fx.Features(100, faction: faction);
            StrategicIntent intent = Propose(new PlaybookSelector(), features);
            ValidationResult result = validator.Validate(intent, new ValidationContext(features, Fx.Belief(100, faction: faction), Rules, Playbooks, null, default));
            Assert.True(result.Accepted, string.Join("; ", result.Issues.Select(static i => $"{i.Code}: {i.Message}")));
        }
    }

    [Fact]
    public void Composer_turns_a_known_enemy_region_into_an_attack_objective_for_pressure_and_asks_for_a_scout_otherwise()
    {
        StrategicFeatures unknown = Fx.Features(100, threats: [BaseThreat(0, 1000)]);
        IReadOnlyList<Objective> scouting = IntentComposer.Objectives(StrategicPosture.Pressure, unknown);
        Assert.Contains(scouting, static o => o.Kind == ObjectiveKind.Scout);
        Assert.Contains(scouting, static o => o.Kind == ObjectiveKind.DefendRegion && o.Region == Fx.R0);

        StrategicFeatures known = unknown with
        {
            MapControl = new MapControlFeatures(new Dictionary<RegionId, RegionControl> { [Fx.R0] = RegionControl.Own, [Fx.R3] = RegionControl.Enemy }, [], 0.5),
        };
        Assert.Contains(IntentComposer.Objectives(StrategicPosture.Pressure, known), static o => o.Kind == ObjectiveKind.AttackRegion && o.Region == Fx.R3);
        Assert.Contains(IntentComposer.Objectives(StrategicPosture.Harass, known), static o => o.Kind == ObjectiveKind.Harass && o.Region == Fx.R3);
        Assert.DoesNotContain(IntentComposer.Objectives(StrategicPosture.Turtle, known), static o => o.Kind == ObjectiveKind.AttackRegion);
    }

    [Fact]
    public async Task Pinned_strategist_keeps_its_playbook_until_the_base_is_threatened()
    {
        PinnedPlaybookStrategist pinned = new(new Dictionary<Faction, string> { [Faction.Soviet] = "soviet-turtle" }, "style-turtle");
        Assert.Equal("soviet-turtle", Propose(pinned, Fx.Features(100, faction: Faction.Soviet)).PlaybookId);
        Assert.Equal(IntentSource.Scripted, Propose(pinned, Fx.Features(100, faction: Faction.Soviet)).Source);
        Assert.Equal("generic-defend", Propose(pinned, Fx.Features(100, faction: Faction.Soviet, threats: [BaseThreat(5000, 1000)])).PlaybookId);
        Assert.Null(await pinned.ProposeAsync(Context(Fx.Features(100, faction: Faction.Allied))));
    }

    [Fact]
    public void Bandit_learns_which_playbook_pays_in_a_context()
    {
        ContextualBanditStrategist bandit = new(new BanditOptions(Alpha: 0.1));
        StrategicFeatures features = Fx.Features(300);
        double[] x = FeatureVector.Encode(features);
        foreach (Playbook p in Playbooks.For(Faction.Allied))
        {
            for (int i = 0; i < 5; i++) bandit.Update(x, p.Id, p.Id == "allied-ifv-mix" ? 1 : -1);
        }
        StrategicIntent first = Propose(bandit, features);
        Assert.Equal("allied-ifv-mix", first.PlaybookId);
        StrategicIntent second = Propose(bandit, features, first);
        Assert.Equal(IntentSource.Bandit, second.Source);

        // Episode crediting: both decisions took effect (the second is the final active intent) and the second renews
        // the first's playbook, so they are one segment, credited once, then forgotten.
        Assert.Equal(first.PlaybookId, second.PlaybookId);
        Assert.Equal(1, bandit.CompleteEpisode(1.0, second));
        Assert.Equal(0, bandit.CompleteEpisode(1.0));
    }

    [Fact]
    public void Bandit_defends_under_base_threat_without_recording_a_decision()
    {
        ContextualBanditStrategist bandit = new();
        Assert.Equal("generic-defend", Propose(bandit, Fx.Features(100, threats: [BaseThreat(3000, 1000)])).PlaybookId);
        Assert.Equal(0, bandit.CompleteEpisode(1.0));
    }

    private static DecisionDataset Separable()
    {
        // Rich (credits high) -> allied-boom, poor -> allied-grizzly-timing.
        List<DecisionExample> examples = [];
        for (int i = 0; i < 40; i++)
        {
            bool rich = i % 2 == 0;
            double[] v = FeatureVector.Encode(Fx.Features(100 + i, credits: rich ? 12000 + 100 * i : 500 + 10 * i));
            examples.Add(new DecisionExample(FeatureVector.Version, v, Faction.Allied, rich ? "allied-boom" : "allied-grizzly-timing",
                rich ? StrategicPosture.Boom : StrategicPosture.Pressure, IntentSource.Llm, "Primary", false, i, i));
        }
        return new DecisionDataset(examples);
    }

    [Fact]
    public void Distilled_strategist_reproduces_the_teacher_in_distribution()
    {
        DistilledStrategist distilled = new(Separable(), new PlaybookSelector());
        Assert.Equal(["allied-boom", "allied-grizzly-timing"], distilled.Classes);
        StrategicIntent rich = Propose(distilled, Fx.Features(120, credits: 13000));
        StrategicIntent poor = Propose(distilled, Fx.Features(120, credits: 700));
        Assert.Equal("allied-boom", rich.PlaybookId);
        Assert.Equal(IntentSource.Distilled, rich.Source);
        Assert.Equal("allied-grizzly-timing", poor.PlaybookId);
        Assert.Equal(0, distilled.Escalations);
    }

    [Fact]
    public void Distilled_strategist_escalates_out_of_distribution_and_without_data()
    {
        DistilledStrategist distilled = new(Separable(), new PinnedPlaybookStrategist(new Dictionary<Faction, string> { [Faction.Allied] = "allied-harass" }, "inner"));
        StrategicIntent far = Propose(distilled, Fx.Features(3000, ownArmy: 40000, enemyArmy: 30000, credits: 60000, income: 9000));
        Assert.Equal("allied-harass", far.PlaybookId);
        Assert.Equal(1, distilled.Escalations);
        Assert.StartsWith("out of distribution", distilled.LastEscalationReason);

        DistilledStrategist empty = new(DecisionDataset.Empty, new PlaybookSelector());
        Assert.Equal(IntentSource.Selector, Propose(empty, Fx.Features(100)).Source);
        Assert.Equal(1, empty.Escalations);
    }

    [Fact]
    public async Task Two_speed_strategist_sends_refinements_to_the_fast_strategist_between_slow_turns()
    {
        PinnedPlaybookStrategist slow = new(new Dictionary<Faction, string> { [Faction.Allied] = "allied-boom" }, "slow");
        PinnedPlaybookStrategist fast = new(new Dictionary<Faction, string> { [Faction.Allied] = "allied-boom" }, "fast");
        TwoSpeedStrategist both = new(slow, fast, strategicEverySeconds: 20);
        StrategicIntent active = Propose(slow, Fx.Features(0));
        await both.ProposeAsync(Context(Fx.Features(0), null));
        await both.ProposeAsync(Context(Fx.Features(5), active));
        await both.ProposeAsync(Context(Fx.Features(10), active));
        await both.ProposeAsync(Context(Fx.Features(21), active));
        Assert.Equal(2, both.SlowRequests);
        Assert.Equal(2, both.FastRequests);

        // An event since the previous request goes to the slow strategist.
        StrategicEvent evt = new(StrategicEventKind.BuildingLost, Fx.T(23), 0.8, "lost");
        await both.ProposeAsync(Context(Fx.Features(25, events: [evt]), active));
        Assert.Equal(3, both.SlowRequests);
    }

    [Fact]
    public async Task Simulated_latency_releases_the_answer_only_after_the_game_time_latency()
    {
        SimulatedLatencyStrategist delayed = new(new PlaybookSelector(), fixedLatencySeconds: 3);
        Task<StrategistProposal?> task = delayed.ProposeAsync(Context(Fx.Features(10)));
        delayed.OnFrame(Fx.T(12));
        Assert.False(task.IsCompleted);
        delayed.OnFrame(Fx.T(13));
        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(3, (await task)!.Cost.LatencySeconds);
        Assert.Equal(IntentSource.Selector, delayed.Source);
    }

    [Fact]
    public void Strategist_context_hash_is_stable_and_sensitive_to_features()
    {
        StrategicFeatures features = Fx.Features(100) with
        {
            MapControl = new MapControlFeatures(new Dictionary<RegionId, RegionControl> { [Fx.R1] = RegionControl.Enemy }, [Fx.R2], 0.5),
        };
        string a = StrategistContextHash.Compute(Context(features));
        Assert.Equal(a, StrategistContextHash.Compute(Context(features)));
        Assert.NotEqual(a, StrategistContextHash.Compute(Context(features with { Time = Fx.T(101) })));
        Assert.Equal(64, a.Length);
    }
}
