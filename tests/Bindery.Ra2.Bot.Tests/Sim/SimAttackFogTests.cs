// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Sim;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Sim;

/// <summary>
/// Invariant 1 (fog) on the command path: an attack order may name only an enemy its issuer can see now, and an
/// order never walks units to where a hidden enemy really is. Otherwise a belief-mode bot follows enemies through
/// fog, or wins by guessing ids of objects it never saw.
/// </summary>
public sealed class SimAttackFogTests
{
    private static readonly PlayerId Soviet = new(0);
    private static readonly PlayerId Allied = new(1);

    /// <summary>Two regions whose border no fixture unit sees across: radius 8 plus link 24 is beyond every sight.</summary>
    private static SimMap Map() => new(new MapInfo(
            "fog-attack",
            60,
            20,
            [
                new Region(new RegionId(0), "west", new Cell(0, 10), 8, true, false, false),
                new Region(new RegionId(1), "east", new Cell(24, 10), 8, true, false, false),
            ],
            [new RegionLink(new RegionId(0), new RegionId(1), 24, true, false)],
            []),
        [new RegionId(0), new RegionId(1)]);

    private static SkirmishSimulation NewSim() =>
        new(Map(), RulesDatabase.LoadEmbeddedFixture(),
            new SimSettings(1, 300, [new SimPlayer(Soviet, Faction.Soviet), new SimPlayer(Allied, Faction.Allied)]));

    [Fact]
    public void An_attack_on_an_enemy_the_issuer_cannot_see_is_rejected_and_moves_nothing()
    {
        SkirmishSimulation sim = NewSim();
        sim.DebugSpawnAt(Soviet, "NAPOWR", new Cell(-4, 10));
        EntityId tank = sim.DebugSpawnAt(Soviet, "HTNK", new Cell(0, 10));
        EntityId hidden = sim.DebugSpawnAt(Allied, "GAPOWR", new Cell(28, 10));
        Assert.DoesNotContain(sim.Observe(Soviet).Entities, e => e.Id == hidden);

        sim.Submit(Soviet, new AttackCommand("test", [tank], hidden));
        sim.Advance(10);

        Assert.Equal(1, sim.RejectedCommandCount(Soviet));
        ObservedEntity after = sim.Observe(Soviet).Entities.Single(e => e.Id == tank);
        Assert.Equal(new Cell(0, 10), after.Position);
        Assert.Null(after.Target);
    }

    [Fact]
    public void A_target_that_walks_into_fog_is_dropped_and_not_followed()
    {
        SkirmishSimulation sim = NewSim();
        sim.DebugSpawnAt(Soviet, "NAPOWR", new Cell(-4, 10));
        sim.DebugSpawnAt(Allied, "GAPOWR", new Cell(28, 10));
        EntityId watcher = sim.DebugSpawnAt(Soviet, "E2", new Cell(-2, 10));
        EntityId target = sim.DebugSpawnAt(Allied, "MTNK", new Cell(4, 10));
        Assert.Contains(sim.Observe(Soviet).Entities, e => e.Id == target);

        sim.Submit(Soviet, new AttackCommand("test", [watcher], target));
        sim.Submit(Allied, new MoveCommand("test", [target], new Cell(28, 10)));
        sim.Advance(12);

        Assert.DoesNotContain(sim.Observe(Soviet).Entities, e => e.Id == target);
        ObservedEntity after = sim.Observe(Soviet).Entities.Single(e => e.Id == watcher);
        Assert.Null(after.Target);
        Assert.Equal(new RegionId(0), sim.Map.RegionOf(after.Position)!.Id);

        // Re-issuing the order once the target is hidden is refused too.
        sim.Submit(Soviet, new AttackCommand("test", [watcher], target));
        sim.Advance(10);
        Assert.Equal(1, sim.RejectedCommandCount(Soviet));
        Assert.Equal(new RegionId(0), sim.Map.RegionOf(sim.Observe(Soviet).Entities.Single(e => e.Id == watcher).Position)!.Id);
    }
}
