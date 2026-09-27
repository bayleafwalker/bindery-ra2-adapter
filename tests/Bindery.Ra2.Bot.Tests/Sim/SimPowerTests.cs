// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Sim;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Sim;

/// <summary>
/// Low power takes powered base defenses offline, as in RA2: killing or outbuilding power is how a turtle is
/// broken, so a Tesla coil without power must not fire, and the same coil with power must.
/// </summary>
public sealed class SimPowerTests
{
    private static readonly PlayerId Soviet = new(0);
    private static readonly PlayerId Allied = new(1);

    private static SimMap Map() => new(new MapInfo(
            "power",
            60,
            20,
            [
                new Region(new RegionId(0), "west", new Cell(0, 10), 8, true, false, false),
                new Region(new RegionId(1), "east", new Cell(24, 10), 8, true, false, false),
            ],
            [new RegionLink(new RegionId(0), new RegionId(1), 24, true, false)],
            []),
        [new RegionId(0), new RegionId(1)]);

    private static int TankHealthAfterFiveSeconds(bool powered)
    {
        SkirmishSimulation sim = new(Map(), RulesDatabase.LoadEmbeddedFixture(),
            new SimSettings(1, 300, [new SimPlayer(Soviet, Faction.Soviet), new SimPlayer(Allied, Faction.Allied)]));
        sim.DebugSpawnAt(Soviet, "NATSLA", new Cell(0, 10));
        if (powered) sim.DebugSpawnAt(Soviet, "NAPOWR", new Cell(-4, 10));
        sim.DebugSpawnAt(Allied, "GAPOWR", new Cell(28, 10));
        EntityId tank = sim.DebugSpawnAt(Allied, "MTNK", new Cell(3, 10));
        Assert.Equal(!powered, sim.Observe(Soviet).Power.LowPower);
        sim.Advance(5);
        return sim.Observe(Allied, ObservationMode.Oracle).Entities.SingleOrDefault(e => e.Id == tank)?.Health ?? 0;
    }

    [Fact]
    public void A_tesla_coil_on_low_power_does_not_fire_and_one_with_power_does()
    {
        int full = RulesDatabase.LoadEmbeddedFixture().Get("MTNK").Strength;

        Assert.Equal(full, TankHealthAfterFiveSeconds(powered: false));
        Assert.True(TankHealthAfterFiveSeconds(powered: true) < full);
    }
}
