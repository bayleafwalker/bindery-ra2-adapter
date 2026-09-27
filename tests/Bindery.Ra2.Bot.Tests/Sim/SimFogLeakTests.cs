// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Sim;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Sim;

/// <summary>
/// Invariant 1 (fog) at the simulator's event boundary. Cross-border fire and superweapons can reach an enemy the
/// owner cannot see; neither may tell the owner anything about it. The lockstep test is the strongest form: two
/// simulations that differ only in a hidden enemy tank inside an own weapon's reach must give the observer
/// byte-identical belief frames for as long as the tank stays unseen.
/// </summary>
public sealed class SimFogLeakTests
{
    private static readonly PlayerId Soviet = new(0);
    private static readonly PlayerId Allied = new(1);

    /// <summary>Two regions whose border no fixture unit sees across: radius 8 plus link 24 is beyond every sight.</summary>
    private static SimMap Map() => new(new MapInfo(
            "fog",
            60,
            20,
            [
                new Region(new RegionId(0), "west", new Cell(0, 10), 8, true, false, false),
                new Region(new RegionId(1), "east", new Cell(24, 10), 8, true, false, false),
            ],
            [new RegionLink(new RegionId(0), new RegionId(1), 24, true, false)],
            []),
        [new RegionId(0), new RegionId(1)]);

    private static SkirmishSimulation NewSim(double superweaponChargeSeconds = 600) =>
        new(Map(), RulesDatabase.LoadEmbeddedFixture(),
            new SimSettings(1, 300, [new SimPlayer(Soviet, Faction.Soviet), new SimPlayer(Allied, Faction.Allied)], SuperweaponChargeSeconds: superweaponChargeSeconds));

    private static bool EastVisibleToSoviet(SkirmishSimulation sim) => sim.VisibleRegionsForProbe(Soviet).Contains(new RegionId(1));

    [Fact]
    public void Artillery_does_not_fire_into_a_region_its_owner_cannot_see_and_learns_nothing_of_it()
    {
        SkirmishSimulation sim = NewSim();
        sim.DebugSpawnAt(Soviet, "NAPOWR", new Cell(-4, 10));
        sim.DebugSpawnAt(Allied, "GAPOWR", new Cell(28, 10));
        EntityId hidden = sim.DebugSpawnAt(Allied, "E1", new Cell(17, 10)); // 8 cells from the V3: inside its range 10
        sim.DebugSpawnAt(Soviet, "V3", new Cell(9, 10));
        int before = sim.Observe(Allied, ObservationMode.Oracle).Entities.Single(e => e.Id == hidden).Health;

        for (int i = 0; i < GameTime.FramesPerSecond * 5; i++)
        {
            sim.Step();
            Assert.False(EastVisibleToSoviet(sim));
            ObservationFrame frame = sim.Observe(Soviet);
            Assert.DoesNotContain(frame.Events, e => e.Entity == hidden);
            Assert.DoesNotContain(frame.Events, e => e.Kind == GameEventKind.EntityKilledByUs);
        }

        Assert.Equal(before, sim.Observe(Allied, ObservationMode.Oracle).Entities.Single(e => e.Id == hidden).Health);
    }

    [Fact]
    public void A_superweapon_kill_in_fog_is_not_reported_to_the_launcher_and_the_launch_names_no_building_to_others()
    {
        SkirmishSimulation sim = NewSim(superweaponChargeSeconds: 2);
        sim.DebugSpawnAt(Soviet, "NAPOWR", new Cell(-3, 10));
        sim.DebugSpawnAt(Soviet, "NAPOWR", new Cell(-3, 13));
        EntityId silo = sim.DebugSpawnAt(Soviet, "NAMISL", new Cell(0, 13));
        sim.DebugSpawnAt(Allied, "GAPOWR", new Cell(30, 10));
        EntityId hidden = sim.DebugSpawnAt(Allied, "E1", new Cell(20, 10));
        sim.Advance(3);
        Assert.False(EastVisibleToSoviet(sim));

        sim.Submit(Soviet, new LaunchSuperweaponCommand("test", silo, new Cell(20, 10)));
        sim.Step();

        Assert.DoesNotContain(sim.Observe(Allied, ObservationMode.Oracle).Entities, e => e.Id == hidden); // the strike killed it
        ObservationFrame launcher = sim.Observe(Soviet);
        Assert.DoesNotContain(launcher.Events, e => e.Kind == GameEventKind.EntityKilledByUs);
        Assert.DoesNotContain(launcher.Events, e => e.Entity == hidden);
        Assert.Equal(silo, Assert.Single(launcher.Events, static e => e.Kind == GameEventKind.SuperweaponLaunched).Entity);

        GameEvent announced = Assert.Single(sim.Observe(Allied).Events, static e => e.Kind == GameEventKind.SuperweaponLaunched);
        Assert.Equal(Soviet, announced.Owner);
        Assert.Equal("NAMISL", announced.TypeId);
        Assert.Null(announced.Entity);
    }

    [Fact]
    public void A_hidden_enemy_in_weapon_reach_leaves_every_belief_frame_of_the_observer_unchanged()
    {
        SkirmishSimulation control = NewSim(), probed = NewSim();
        foreach (SkirmishSimulation sim in new[] { control, probed })
        {
            sim.DebugSpawnAt(Soviet, "NAPOWR", new Cell(-4, 10));
            sim.DebugSpawnAt(Soviet, "V3", new Cell(9, 10));
            sim.DebugSpawnAt(Soviet, "HTNK", new Cell(6, 10));
            sim.DebugSpawnAt(Allied, "GAPOWR", new Cell(30, 10));
        }
        probed.DebugSpawnAt(Allied, "MTNK", new Cell(18, 10)); // 9 cells from the V3, idle, never seen

        for (int i = 0; i < GameTime.FramesPerSecond * 60; i++)
        {
            control.Step();
            probed.Step();
            Assert.False(EastVisibleToSoviet(probed));
            Assert.Equal(JsonSerializer.Serialize(control.Observe(Soviet)), JsonSerializer.Serialize(probed.Observe(Soviet)));
        }
    }

    [Fact]
    public void The_leakage_probe_places_a_hidden_enemy_inside_the_observer_weapon_reach_and_wounds_hidden_enemies()
    {
        SkirmishSimulation sim = NewSim();
        sim.DebugSpawnAt(Soviet, "NAPOWR", new Cell(-4, 10));
        sim.DebugSpawnAt(Soviet, "V3", new Cell(9, 10));
        EntityId hiddenPlant = sim.DebugSpawnAt(Allied, "GAPOWR", new Cell(30, 10));
        List<ObservedEntity> before = [.. sim.Observe(Soviet, ObservationMode.Oracle).Entities];

        SimLeakageProbe.PerturbHidden(sim, Soviet);

        List<ObservedEntity> after = [.. sim.Observe(Soviet, ObservationMode.Oracle).Entities];
        Assert.False(EastVisibleToSoviet(sim));
        Assert.Contains(after, e => e.Owner == Allied && before.All(b => b.Id != e.Id)
            && sim.Map.RegionOf(e.Position)!.Id == new RegionId(1) && e.Position.DistanceTo(new Cell(9, 10)) <= 10);
        Assert.True(after.Single(e => e.Id == hiddenPlant).Health < before.Single(e => e.Id == hiddenPlant).Health);
        Assert.Equal(before.Where(e => e.Owner == Soviet), after.Where(e => e.Owner == Soviet));
    }
}
