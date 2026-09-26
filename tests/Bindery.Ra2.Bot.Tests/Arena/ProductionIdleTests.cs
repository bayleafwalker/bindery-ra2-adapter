// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Rules;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary>
/// Production idle (spec): a factory, nothing queued, and credits for the cheapest item the player can build. The
/// cheapest item anywhere in the rules (a Soviet conscript) says nothing about what an Allied base can afford.
/// </summary>
public sealed class ProductionIdleTests
{
    private static readonly PlayerId Player = new(0);

    private static ObservationFrame Frame(int credits, params string[] buildings) => new(
        GameTime.FromSeconds(60), ObservationMode.Oracle, Player, Faction.Allied, credits, new PowerState(100, 0),
        [.. buildings.Select((t, i) => new ObservedEntity(new EntityId((uint)(i + 1)), Player, t, new Cell(i, 0), 100, 100))],
        [], [], new HashSet<RegionId>(), new MapInfo("m", 10, 10, [], [], []));

    [Fact]
    public void Idle_needs_credits_for_the_cheapest_item_this_player_can_build()
    {
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();

        // A lone Allied yard can build nothing under 500 credits (the barracks needs a power plant first).
        int cheapestAtYard = rules.All.Where(r => r.Cost > 0 && rules.CanBuild(Faction.Allied, new HashSet<string> { "GAYARD" }, r.TypeId)).Min(static r => r.Cost);
        Assert.True(cheapestAtYard > 500);
        Assert.False(MatchRunner.IsProductionIdle(Frame(500, "GAYARD"), Player, rules));
        Assert.True(MatchRunner.IsProductionIdle(Frame(cheapestAtYard, "GAYARD"), Player, rules));

        // With a barracks, 150 credits still buys nothing Allied (a GI costs 200).
        Assert.False(MatchRunner.IsProductionIdle(Frame(150, "GAYARD", "GAPOWR", "GAPILE"), Player, rules));
        Assert.True(MatchRunner.IsProductionIdle(Frame(200, "GAYARD", "GAPOWR", "GAPILE"), Player, rules));
    }
}
