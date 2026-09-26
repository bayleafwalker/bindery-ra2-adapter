// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Rules;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary>Trade metrics credit a kill to the player who made it, never an own-goal to the opponent.</summary>
public sealed class TradeTallyTests
{
    private static readonly PlayerId Arm = new(0);
    private static readonly PlayerId Opponent = new(1);

    [Fact]
    public void Units_lost_to_their_own_superweapon_are_losses_not_the_opponents_kills()
    {
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();
        int tank = rules.Get("HTNK").Cost, plant = rules.Get("GAPOWR").Cost;
        Dictionary<PlayerId, int> lost = [], killed = [];
        GameTime t = GameTime.FromSeconds(10);

        // The arm's strike kills an enemy power plant and three of its own tanks.
        MatchRunner.TallyTrades(
        [
            new GameEvent(GameEventKind.EntityDestroyed, t, new EntityId(1), Opponent, "GAPOWR", new Cell(1, 1), "superweapon"),
            new GameEvent(GameEventKind.EntityKilledByUs, t, new EntityId(1), Arm, "GAPOWR", new Cell(1, 1), "superweapon"),
            .. Enumerable.Range(0, 3).Select(i => new GameEvent(GameEventKind.EntityDestroyed, t, new EntityId((uint)(10 + i)), Arm, "HTNK", new Cell(1, 2), "superweapon")),
        ], rules, lost, killed);

        Assert.Equal(3 * tank, lost[Arm]);
        Assert.Equal(plant, lost[Opponent]);
        Assert.Equal(plant, killed[Arm]);
        Assert.Equal(0, killed.GetValueOrDefault(Opponent));
    }
}
