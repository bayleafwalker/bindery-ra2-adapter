// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Sim;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Sim;

/// <summary>
/// Weapon range across a region border: a unit may hit an enemy in a neighbouring region when the enemy is within
/// its range in cells, and not otherwise. This is what makes artillery stand-off (and a defense's reach) mean
/// something in a region-level simulator. Uses the approximate fixture: V3 range 10, Prism tower range 8. Neither
/// side can see across this border (sight is far short of radius plus link), and a target in fog cannot be
/// acquired, so the firing side keeps a tank in the other's region as a spotter.
/// </summary>
public sealed class SkirmishSimulationRangeTests
{
    private static readonly PlayerId Soviet = new(0);
    private static readonly PlayerId Allied = new(1);

    private static SimMap Map()
    {
        MapInfo map = new(
            "range",
            60,
            20,
            [
                new Region(new RegionId(0), "west", new Cell(0, 10), 8, true, false, false),
                new Region(new RegionId(1), "east", new Cell(24, 10), 8, true, false, false),
            ],
            [new RegionLink(new RegionId(0), new RegionId(1), 24, true, false)],
            []);
        return new SimMap(map, [new RegionId(0), new RegionId(1)]);
    }

    private static (SkirmishSimulation Sim, EntityId V3, EntityId Tower) Setup(Cell v3At, PlayerId spotter)
    {
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();
        SkirmishSimulation sim = new(Map(), rules, new SimSettings(1, 300, [new SimPlayer(Soviet, Faction.Soviet), new SimPlayer(Allied, Faction.Allied)]));
        EntityId tower = sim.DebugSpawnAt(Allied, "GAPRIS", new Cell(18, 10));
        EntityId v3 = sim.DebugSpawnAt(Soviet, "V3", v3At);
        if (spotter == Soviet) sim.DebugSpawnAt(Soviet, "HTNK", new Cell(30, 10)); // spots the east region for the V3
        else sim.DebugSpawnAt(Allied, "MTNK", new Cell(0, 14)); // spots the west region for the tower
        return (sim, v3, tower);
    }

    private static int Health(SkirmishSimulation sim, EntityId id) =>
        sim.Observe(Soviet, ObservationMode.Oracle).Entities.SingleOrDefault(e => e.Id == id)?.Health ?? 0;

    [Fact]
    public void Artillery_outside_a_defense_range_hits_it_across_the_border_unanswered()
    {
        (SkirmishSimulation sim, EntityId v3, EntityId tower) = Setup(new Cell(9, 10), Soviet); // 9 cells: inside V3 range 10, outside tower range 8
        int towerBefore = Health(sim, tower), v3Before = Health(sim, v3);

        sim.Advance(5);

        Assert.True(Health(sim, tower) < towerBefore, "the V3 must hit the tower from the neighbouring region");
        Assert.Equal(v3Before, Health(sim, v3));
    }

    [Fact]
    public void A_defense_hits_a_unit_across_the_border_within_its_range()
    {
        (SkirmishSimulation sim, EntityId v3, _) = Setup(new Cell(11, 10), Allied); // 7 cells: inside tower range 8, still the west region
        Assert.Equal(new RegionId(0), sim.Map.RegionOf(new Cell(11, 10))!.Id);
        int v3Before = Health(sim, v3);

        sim.Advance(3);

        Assert.True(Health(sim, v3) < v3Before);
    }

    [Fact]
    public void Without_a_spotter_neither_side_fires_across_the_border()
    {
        (SkirmishSimulation sim, EntityId v3, EntityId tower) = Setup(new Cell(9, 10), Allied);
        int towerBefore = Health(sim, tower);

        sim.Advance(5);

        Assert.Equal(towerBefore, Health(sim, tower));
    }

    [Fact]
    public void An_attack_order_on_a_target_in_range_does_not_walk_into_its_region()
    {
        (SkirmishSimulation sim, EntityId v3, EntityId tower) = Setup(new Cell(9, 10), Soviet);
        sim.Submit(Soviet, new AttackCommand("test", [v3], tower));

        sim.Advance(2);

        ObservedEntity unit = sim.Observe(Soviet, ObservationMode.Oracle).Entities.Single(e => e.Id == v3);
        Assert.Equal(new Cell(9, 10), unit.Position);
    }
}
