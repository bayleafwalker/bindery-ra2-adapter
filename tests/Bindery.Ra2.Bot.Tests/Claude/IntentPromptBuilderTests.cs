// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Claude;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Claude;

public sealed class IntentPromptBuilderTests
{
    private static List<string> TopLevelKeys(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().Select(p => p.Name).ToList();
    }

    [Fact]
    public void Prompt_contains_only_the_declared_top_level_keys()
    {
        IntentPrompt prompt = new IntentPromptBuilder().Build(ClaudeFixtures.Context(active: ClaudeFixtures.ActiveIntent(), personality: "cautious"), StrategistMode.Strategic);

        Assert.Equal(["catalogue", "faction", "personality", "ruleFacts"], TopLevelKeys(prompt.MatchContext));
        Assert.Equal(["activeIntent", "conditionMetrics", "counters", "features", "history", "techProgress"], TopLevelKeys(prompt.Situation));
        Assert.Equal(IntentPromptBuilder.MatchContextKeys, TopLevelKeys(prompt.MatchContext));
        Assert.Equal(IntentPromptBuilder.SituationKeys, TopLevelKeys(prompt.Situation));

        using JsonDocument situation = JsonDocument.Parse(prompt.Situation);
        List<string> featureKeys = situation.RootElement.GetProperty("features").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(["army", "economy", "enemy", "events", "gameSeconds", "mapControl", "observationMode", "scouting", "superweapons", "threats"], featureKeys);
    }

    [Fact]
    public void Prompt_carries_role_by_location_per_item_ages_and_superweapon_timers()
    {
        StrategicFeatures baseFeatures = ClaudeFixtures.Features();
        StrategicFeatures features = baseFeatures with
        {
            Army = baseFeatures.Army with
            {
                Clusters = [new ForceCluster(new RegionId(2), 8, 5000, 0.9, new Dictionary<UnitRole, double> { [UnitRole.AntiArmor] = 4000, [UnitRole.AntiInfantry] = 1000 })],
            },
            Enemy = baseFeatures.Enemy with { TechLastSeenAgeSeconds = new Dictionary<string, double> { ["HTNK"] = 12.5 } },
            Superweapons = new SuperweaponFeatures(
                [new SuperweaponTimer("GAWEAT", 0.25, 450, false)],
                [new SuperweaponTimer("NAMISL", 0.9, 60, false)]),
        };

        IntentPrompt prompt = new IntentPromptBuilder().Build(ClaudeFixtures.Context(features), StrategistMode.Strategic);

        using JsonDocument situation = JsonDocument.Parse(prompt.Situation);
        JsonElement f = situation.RootElement.GetProperty("features");
        JsonElement cluster = Assert.Single(f.GetProperty("army").GetProperty("clusters").EnumerateArray().ToList());
        Assert.Equal(4000, cluster.GetProperty("valueByRole").GetProperty("AntiArmor").GetDouble());
        Assert.Equal(12.5, f.GetProperty("enemy").GetProperty("techLastSeenAgeSeconds").GetProperty("HTNK").GetDouble());
        JsonElement enemySw = Assert.Single(f.GetProperty("superweapons").GetProperty("enemy").EnumerateArray().ToList());
        Assert.Equal("NAMISL", enemySw.GetProperty("typeId").GetString());
        Assert.Equal(60, enemySw.GetProperty("secondsToReady").GetDouble());
        Assert.Equal(0.25, Assert.Single(f.GetProperty("superweapons").GetProperty("own").EnumerateArray().ToList()).GetProperty("chargeFraction").GetDouble());
    }

    [Fact]
    public void Prompt_threats_carry_the_likely_attack_path()
    {
        IntentPrompt prompt = new IntentPromptBuilder().Build(ClaudeFixtures.Context(), StrategistMode.Strategic);

        using JsonDocument situation = JsonDocument.Parse(prompt.Situation);
        JsonElement threat = Assert.Single(situation.RootElement.GetProperty("features").GetProperty("threats").EnumerateArray().ToList());
        Assert.Equal([3, 2, 1], threat.GetProperty("likelyAttackPath").EnumerateArray().Select(static r => r.GetInt32()));
    }

    [Fact]
    public void Prompt_does_not_leak_rules_for_enemy_tech_the_player_has_not_seen()
    {
        // The rules database knows the Soviet Iron Curtain, but the Allied player
        // has not seen it: nothing in the prompt may mention it.
        StrategicFeatures features = ClaudeFixtures.Features(enemyTech: new HashSet<string>(StringComparer.Ordinal));
        IntentPrompt prompt = new IntentPromptBuilder().Build(ClaudeFixtures.Context(features), StrategistMode.Strategic);

        string all = prompt.SystemPrompt + prompt.MatchContext + prompt.Situation;
        Assert.DoesNotContain("NAIRON", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Iron Curtain", all, StringComparison.Ordinal);
        Assert.DoesNotContain("soviet-rhino-rush", all, StringComparison.Ordinal);
    }

    [Fact]
    public void Seen_enemy_tech_appears_only_through_features()
    {
        StrategicFeatures features = ClaudeFixtures.Features(enemyTech: new HashSet<string>(StringComparer.Ordinal) { "NAIRON" });
        IntentPrompt prompt = new IntentPromptBuilder().Build(ClaudeFixtures.Context(features), StrategistMode.Strategic);

        Assert.Contains("NAIRON", prompt.Situation, StringComparison.Ordinal);
        Assert.DoesNotContain("NAIRON", prompt.MatchContext, StringComparison.Ordinal);
    }

    [Fact]
    public void Identical_contexts_give_identical_bytes_regardless_of_collection_order()
    {
        StrategicFeatures a = ClaudeFixtures.Features();
        StrategicFeatures b = a with
        {
            MapControl = a.MapControl with
            {
                Control = a.MapControl.Control.Reverse().ToDictionary(kv => kv.Key, kv => kv.Value),
            },
            Enemy = a.Enemy with
            {
                CompositionByRole = a.Enemy.CompositionByRole.Reverse().ToDictionary(kv => kv.Key, kv => kv.Value),
            },
        };
        IntentPromptBuilder builder = new();

        IntentPrompt first = builder.Build(ClaudeFixtures.Context(a, ClaudeFixtures.ActiveIntent()), StrategistMode.Strategic);
        IntentPrompt second = builder.Build(ClaudeFixtures.Context(b, ClaudeFixtures.ActiveIntent()), StrategistMode.Strategic);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Situation_changes_when_features_change()
    {
        IntentPromptBuilder builder = new();
        IntentPrompt first = builder.Build(ClaudeFixtures.Context(ClaudeFixtures.Features(credits: 2500)), StrategistMode.Strategic);
        IntentPrompt second = builder.Build(ClaudeFixtures.Context(ClaudeFixtures.Features(credits: 900)), StrategistMode.Strategic);

        Assert.NotEqual(first.Situation, second.Situation);
        Assert.Equal(first.MatchContext, second.MatchContext);
    }

    [Fact]
    public void System_prompt_is_byte_stable_and_carries_no_per_call_data()
    {
        IntentPromptBuilder builder = new();
        IntentPrompt early = builder.Build(ClaudeFixtures.Context(ClaudeFixtures.Features(version: 1, seconds: 30)), StrategistMode.Strategic);
        IntentPrompt late = builder.Build(ClaudeFixtures.Context(ClaudeFixtures.Features(version: 99, seconds: 900), ClaudeFixtures.ActiveIntent(), "greedy"), StrategistMode.Strategic);

        Assert.Equal(early.SystemPrompt, late.SystemPrompt);
        Assert.Equal(IntentPromptBuilder.SystemPrompt(StrategistMode.Strategic), early.SystemPrompt);
        Assert.NotEqual(IntentPromptBuilder.SystemPrompt(StrategistMode.Strategic), IntentPromptBuilder.SystemPrompt(StrategistMode.Refine));
        Assert.DoesNotContain("greedy", late.SystemPrompt, StringComparison.Ordinal);
        Assert.StartsWith(IntentPromptBuilder.SystemPrompt(StrategistMode.Strategic), IntentPromptBuilder.SystemPrompt(StrategistMode.Refine), StringComparison.Ordinal);
    }

    [Fact]
    public void User_blocks_put_the_match_context_before_a_cache_breakpoint()
    {
        IntentPrompt prompt = new IntentPromptBuilder().Build(ClaudeFixtures.Context(), StrategistMode.Strategic);

        Assert.Collection(
            prompt.UserBlocks,
            first =>
            {
                Assert.Equal(prompt.MatchContext, first.Text);
                Assert.True(first.CacheBreakpoint);
            },
            second =>
            {
                Assert.Equal(prompt.Situation, second.Text);
                Assert.False(second.CacheBreakpoint);
            });
    }

    [Fact]
    public void Rule_facts_carry_prerequisite_paths_for_playbook_tech_goals()
    {
        IntentPrompt prompt = new IntentPromptBuilder().Build(ClaudeFixtures.Context(), StrategistMode.Strategic);
        using JsonDocument match = JsonDocument.Parse(prompt.MatchContext);

        JsonElement goals = match.RootElement.GetProperty("ruleFacts").GetProperty("techGoals");
        JsonElement lab = goals.EnumerateArray().Single(g => g.GetProperty("typeId").GetString() == "GATECH");
        Assert.Equal(["GAPOWR", "GAREFN", "GAWEAP"], lab.GetProperty("prerequisitePath").EnumerateArray().Select(e => e.GetString()!).ToList());
        Assert.Equal(3, lab.GetProperty("pathLength").GetInt32());
        Assert.Equal(800 + 2000 + 2000 + 2000, lab.GetProperty("totalCostFromEmptyBase").GetDouble());

        List<string> catalogue = match.RootElement.GetProperty("catalogue").EnumerateArray().Select(p => p.GetProperty("id").GetString()!).ToList();
        Assert.Equal(["allied-boom", "allied-harass"], catalogue);
    }

    [Fact]
    public void Active_intent_reports_time_active_and_remaining_commitment()
    {
        StrategistContext context = ClaudeFixtures.Context(ClaudeFixtures.Features(seconds: 300), ClaudeFixtures.ActiveIntent(issuedAt: 280));
        IntentPrompt prompt = new IntentPromptBuilder().Build(context, StrategistMode.Strategic);
        using JsonDocument situation = JsonDocument.Parse(prompt.Situation);

        JsonElement active = situation.RootElement.GetProperty("activeIntent");
        Assert.Equal(20, active.GetProperty("secondsActive").GetDouble());
        Assert.Equal(25, active.GetProperty("minCommitRemainingSeconds").GetDouble());
        Assert.Equal(100, active.GetProperty("expiresInSeconds").GetDouble());
    }

    [Fact]
    public void Non_finite_features_serialise_as_null()
    {
        IntentPrompt prompt = new IntentPromptBuilder().Build(ClaudeFixtures.Context(), StrategistMode.Strategic);
        using JsonDocument situation = JsonDocument.Parse(prompt.Situation);

        Assert.Equal(JsonValueKind.Null, situation.RootElement.GetProperty("features").GetProperty("economy").GetProperty("cashRunwaySeconds").ValueKind);
    }
}
