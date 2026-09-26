// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Claude;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Runtime;
using Bindery.Ra2.Bot.Strategy;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Claude;

/// <summary>
/// Rule facts the model must never have to remember: counters against the enemy it has seen (from the
/// effectiveness matrix) and how far each tech goal is from the buildings it actually owns.
/// </summary>
public sealed class PromptRulesFactsTests
{
    private static readonly IRulesDatabase Rules = RulesDatabase.LoadEmbeddedFixture();

    private static StrategistContext Context(IReadOnlySet<string>? enemyTech, IReadOnlySet<string>? owned) =>
        new(
            ClaudeFixtures.Features(enemyTech: enemyTech),
            Rules,
            PlaybookLibrary.LoadDefault(),
            null,
            [],
            null,
            owned);

    private static JsonElement Situation(StrategistContext context)
    {
        IntentPrompt prompt = new IntentPromptBuilder().Build(context, StrategistMode.Strategic);
        using JsonDocument document = JsonDocument.Parse(prompt.Situation);
        return document.RootElement.Clone();
    }

    [Fact]
    public void Counters_list_the_most_effective_own_types_against_each_seen_enemy_type()
    {
        JsonElement situation = Situation(Context(new HashSet<string>(StringComparer.Ordinal) { "HTNK", "E2" }, new HashSet<string>(StringComparer.Ordinal) { "GAYARD" }));

        JsonElement counters = situation.GetProperty("counters");
        Assert.Equal(["E2", "HTNK"], counters.EnumerateArray().Select(static c => c.GetProperty("enemyTypeId").GetString()!).ToList());
        JsonElement vsRhino = counters.EnumerateArray().Single(static c => c.GetProperty("enemyTypeId").GetString() == "HTNK");
        Assert.Equal("AntiArmor", vsRhino.GetProperty("enemyRole").GetString());

        List<JsonElement> best = [.. vsRhino.GetProperty("best").EnumerateArray()];
        Assert.InRange(best.Count, 1, 3);
        double top = Rules.All
            .Where(static r => r.Kind != EntityKind.Building && r.Factions.Contains(Faction.Allied) && r.Damage > 0)
            .Max(static r => Rules.Effectiveness(r.TypeId, "HTNK"));
        Assert.Equal(top, best[0].GetProperty("effectiveness").GetDouble(), 3);
        Assert.All(best, b => Assert.Contains(Faction.Allied, Rules.Get(b.GetProperty("typeId").GetString()!).Factions));
        // Ranked: effectiveness never increases down the list.
        for (int i = 1; i < best.Count; i++) Assert.True(best[i].GetProperty("effectiveness").GetDouble() <= best[i - 1].GetProperty("effectiveness").GetDouble());
    }

    [Fact]
    public void Counters_are_empty_when_no_enemy_unit_has_been_seen()
    {
        JsonElement situation = Situation(Context(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal)));
        Assert.Empty(situation.GetProperty("counters").EnumerateArray());
    }

    [Fact]
    public void Tech_progress_is_measured_from_the_buildings_actually_owned()
    {
        HashSet<string> owned = new(StringComparer.Ordinal) { "GAYARD", "GAPOWR", "GAREFN" };
        JsonElement situation = Situation(Context(null, owned));

        JsonElement factory = situation.GetProperty("techProgress").EnumerateArray().Single(static g => g.GetProperty("typeId").GetString() == "GAWEAP");
        IReadOnlyList<string> expected = Rules.PathTo(Faction.Allied, owned, "GAWEAP")!;
        Assert.Equal(expected, factory.GetProperty("remainingPath").EnumerateArray().Select(static e => e.GetString()!).ToList());
        Assert.True(factory.GetProperty("buildableNow").GetBoolean());
        Assert.NotEqual(Rules.PathTo(Faction.Allied, new HashSet<string>(StringComparer.Ordinal), "GAWEAP")!.Count, expected.Count);
    }

    [Fact]
    public void Tech_progress_is_null_when_the_context_does_not_say_what_is_owned()
    {
        JsonElement situation = Situation(Context(null, null));
        Assert.Equal(JsonValueKind.Null, situation.GetProperty("techProgress").ValueKind);
    }

    [Fact]
    public void The_runtime_gives_strategists_the_buildings_it_owns()
    {
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();
        using BotRuntime runtime = StandardBot.Create(rules, PlaybookLibrary.LoadDefault(), new PlaybookSelector());
        MapInfo map = Bindery.Ra2.Bot.Sim.SimMaps.TwinValley.Map;
        ObservationFrame frame = new(
            new GameTime(15), ObservationMode.Belief, new PlayerId(0), Faction.Allied, 5000, new PowerState(100, 0),
            [new ObservedEntity(new EntityId(1), new PlayerId(0), "GAYARD", map.Regions[0].Center, 1000, 1000), new ObservedEntity(new EntityId(2), new PlayerId(0), "GAPOWR", map.Regions[0].Center, 400, 400)],
            [], [], new HashSet<RegionId> { map.Regions[0].Id }, map);

        runtime.Tick(frame);

        Assert.Equal(["GAPOWR", "GAYARD"], runtime.CurrentStrategistContext!.OwnedBuildingTypes!.OrderBy(static t => t, StringComparer.Ordinal));
    }
}
