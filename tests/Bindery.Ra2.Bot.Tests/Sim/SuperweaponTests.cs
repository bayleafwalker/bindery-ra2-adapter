// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Runtime;
using Bindery.Ra2.Bot.Sim;
using Bindery.Ra2.Bot.Strategy;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Sim;

/// <summary>
/// Superweapons in the region simulator: a powered superweapon building charges, every player sees every timer
/// (as RA2 shows them), a ready one can be launched, and the launch reaches every player as
/// <see cref="GameEventKind.SuperweaponLaunched"/>. The last test runs it end to end through two bots.
/// </summary>
public sealed class SuperweaponTests
{
    private static readonly PlayerId Soviet = new(0);
    private static readonly PlayerId Allied = new(1);

    private static (SkirmishSimulation Sim, EntityId Silo) Setup(double chargeSeconds, SimMap? map = null, int damage = 1500)
    {
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();
        map ??= SimMaps.TwinValley;
        SkirmishSimulation sim = new(map, rules, new SimSettings(1, 600,
            [new SimPlayer(Soviet, Faction.Soviet), new SimPlayer(Allied, Faction.Allied)], SuperweaponChargeSeconds: chargeSeconds, SuperweaponDamage: damage));
        Cell home = map.Map.Regions.Single(r => r.Id == map.StartRegions[0]).Center;
        sim.DebugSpawnAt(Soviet, "NAPOWR", new Cell(home.X - 3, home.Y));
        sim.DebugSpawnAt(Soviet, "NAPOWR", new Cell(home.X - 3, home.Y + 3));
        EntityId silo = sim.DebugSpawnAt(Soviet, "NAMISL", new Cell(home.X, home.Y + 3));
        return (sim, silo);
    }

    [Fact]
    public void A_powered_superweapon_charges_and_every_player_sees_its_timer()
    {
        (SkirmishSimulation sim, EntityId silo) = Setup(chargeSeconds: 30);
        sim.Advance(10);

        SuperweaponStatus own = Assert.Single(sim.Observe(Soviet).Superweapons!);
        Assert.Equal(silo, own.Building);
        Assert.Equal("NAMISL", own.TypeId);
        Assert.InRange(own.SecondsToReady, 19, 21);
        Assert.False(own.Ready);

        // The enemy sees the timer (not where the silo is) without any vision of the Soviet base.
        ObservationFrame enemyView = sim.Observe(Allied);
        Assert.DoesNotContain(enemyView.Entities, e => e.Id == silo);
        SuperweaponStatus seen = Assert.Single(enemyView.Superweapons!);
        Assert.Null(seen.Building);
        Assert.Equal(Soviet, seen.Owner);
        Assert.Equal(own.SecondsToReady, seen.SecondsToReady);

        sim.Advance(21);
        Assert.True(Assert.Single(sim.Observe(Soviet).Superweapons!).Ready);
    }

    [Fact]
    public void Low_power_pauses_the_charge()
    {
        (SkirmishSimulation sim, _) = Setup(chargeSeconds: 30);
        Cell home = SimMaps.TwinValley.Map.Regions.Single(r => r.Id == SimMaps.TwinValley.StartRegions[0]).Center;
        // Two Tesla coils drain more than two power plants produce.
        sim.DebugSpawnAt(Soviet, "NATSLA", new Cell(home.X + 3, home.Y));
        sim.DebugSpawnAt(Soviet, "NATSLA", new Cell(home.X + 3, home.Y + 3));
        sim.DebugSpawnAt(Soviet, "NATSLA", new Cell(home.X + 3, home.Y - 3));
        Assert.True(sim.Observe(Soviet).Power.LowPower);

        sim.Advance(10);

        Assert.Equal(30, Assert.Single(sim.Observe(Soviet).Superweapons!).SecondsToReady, 6);
    }

    [Fact]
    public void Launching_before_ready_is_rejected_and_a_ready_launch_strikes_and_is_announced_to_everyone()
    {
        (SkirmishSimulation sim, EntityId silo) = Setup(chargeSeconds: 5);
        Cell enemyHome = SimMaps.TwinValley.Map.Regions.Single(r => r.Id == SimMaps.TwinValley.StartRegions[1]).Center;
        EntityId target = sim.DebugSpawnAt(Allied, "GAPOWR", enemyHome);

        sim.Submit(Soviet, new LaunchSuperweaponCommand("test", silo, enemyHome));
        sim.Step();
        Assert.Equal(1, sim.RejectedCommandCount(Soviet));

        sim.Advance(6);
        sim.Submit(Soviet, new LaunchSuperweaponCommand("test", silo, enemyHome));
        sim.Step();

        Assert.Equal(1, sim.RejectedCommandCount(Soviet));
        Assert.DoesNotContain(sim.Observe(Allied, ObservationMode.Oracle).Entities, e => e.Id == target);
        GameEvent launch = Assert.Single(sim.Observe(Allied).Events, static e => e.Kind == GameEventKind.SuperweaponLaunched);
        Assert.Equal(Soviet, launch.Owner);
        Assert.Equal("NAMISL", launch.TypeId);
        Assert.False(Assert.Single(sim.Observe(Soviet).Superweapons!).Ready);
    }

    [Fact]
    public void End_to_end_a_bot_launches_its_superweapon_and_the_other_bot_detects_it()
    {
        // A strike that does not end the match, so the target's bot keeps playing and reads the announcement.
        (SkirmishSimulation sim, _) = Setup(chargeSeconds: 20, damage: 300);
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();
        IPlaybookLibrary playbooks = PlaybookLibrary.LoadDefault();
        using BotRuntime launcher = StandardBot.Create(rules, playbooks, new PlaybookSelector());
        using BotRuntime target = StandardBot.Create(rules, playbooks, new PlaybookSelector());
        bool targetSawTimer = false, targetDetectedLaunch = false;

        for (int frame = 0; frame < GameTime.FramesPerSecond * 40 && !sim.MatchEnded; frame++)
        {
            // The launcher needs to know where the enemy base is; oracle frames give it that without a scouting game.
            foreach (GameCommand c in launcher.Tick(sim.Observe(Soviet, ObservationMode.Oracle))) sim.Submit(Soviet, c);
            foreach (GameCommand c in target.Tick(sim.Observe(Allied))) sim.Submit(Allied, c);
            sim.Step();
            StrategicFeatures seen = target.CurrentFeatures!;
            targetSawTimer |= seen.Superweapons?.Enemy.Count > 0;
            targetDetectedLaunch |= seen.Events.Any(static e => e.Kind == StrategicEventKind.SuperweaponDetected && e.Detail.StartsWith("launched:", StringComparison.Ordinal));
        }

        Assert.True(targetSawTimer, "the target bot's features must show the enemy superweapon timer");
        Assert.Contains(launcher.Log.Records, static r => r.Kind == DecisionRecordKinds.Plan && r.Data.GetRawText().Contains("LaunchSuperweaponCommand", StringComparison.Ordinal));
        Assert.True(targetDetectedLaunch, "the launch must reach the target bot's features as SuperweaponDetected");
    }
}
