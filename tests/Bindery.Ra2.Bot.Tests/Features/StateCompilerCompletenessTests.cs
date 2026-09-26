// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Belief;
using Bindery.Ra2.Bot.Features;
using Bindery.Ra2.Bot.Tests.Belief;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Features;

/// <summary>
/// The proposal's state-compiler list: army value by role <em>and</em> location, a last-seen age per known
/// enemy tech and production item, and own and enemy superweapon charge.
/// </summary>
public sealed class StateCompilerCompletenessTests
{
    private static readonly PlayerId Self = new(0);
    private static readonly PlayerId Enemy = new(1);

    private static readonly UnitRule Tank = FakeRulesDatabase.Rule("tank", Faction.Allied, EntityKind.Vehicle, UnitRole.AntiArmor, QueueKind.Vehicle, 700);
    private static readonly UnitRule Gun = FakeRulesDatabase.Rule("gun", Faction.Allied, EntityKind.Vehicle, UnitRole.AntiInfantry, QueueKind.Vehicle, 500);
    private static readonly UnitRule Yard = FakeRulesDatabase.Rule("yard", Faction.Allied, EntityKind.Building, UnitRole.Production, QueueKind.Building, 0);
    private static readonly UnitRule Rhino = FakeRulesDatabase.Rule("rhino", Faction.Soviet, EntityKind.Vehicle, UnitRole.AntiArmor, QueueKind.Vehicle, 900);
    private static readonly UnitRule Factory = FakeRulesDatabase.Rule("factory", Faction.Soviet, EntityKind.Building, UnitRole.Production, QueueKind.Vehicle, 2000);
    private static readonly UnitRule Silo = FakeRulesDatabase.Rule("silo", Faction.Soviet, EntityKind.Building, UnitRole.Superweapon, QueueKind.Building, 5000);
    private static readonly UnitRule Storm = FakeRulesDatabase.Rule("storm", Faction.Allied, EntityKind.Building, UnitRole.Superweapon, QueueKind.Building, 5000);

    private static FakeRulesDatabase Rules() => new([Tank, Gun, Yard, Rhino, Factory, Silo, Storm]);

    private static ObservationFrame Frame(double seconds, IReadOnlyList<ObservedEntity> entities, IReadOnlyList<SuperweaponStatus>? superweapons = null) =>
        new(
            GameTime.FromSeconds(seconds), ObservationMode.Belief, Self, Faction.Allied, 5000, new PowerState(100, 50),
            entities, [], [], new HashSet<RegionId> { TestMaps.Home, TestMaps.Middle }, TestMaps.Simple(), Superweapons: superweapons);

    private static ObservedEntity Own(uint id, string type, Cell cell) => new(new EntityId(id), Self, type, cell, 100, 100);

    private static ObservedEntity Theirs(uint id, string type, Cell cell) => new(new EntityId(id), Enemy, type, cell, 100, 100);

    [Fact]
    public void Each_force_cluster_carries_its_value_by_role()
    {
        FakeRulesDatabase rules = Rules();
        BeliefModel belief = new(rules, new BeliefOptions());
        FeatureCompiler compiler = new(rules, new FeatureOptions());
        Cell home = TestMaps.Simple().Regions[0].Center;
        Cell middle = TestMaps.Simple().Regions[1].Center;

        StrategicFeatures features = compiler.Compile(belief.Apply(Frame(1,
        [
            Own(1, "yard", home), Own(2, "tank", home), Own(3, "gun", home), Own(4, "tank", middle),
        ])));

        ForceCluster atHome = features.Army.Clusters.Single(c => c.Region == TestMaps.Home);
        ForceCluster atMiddle = features.Army.Clusters.Single(c => c.Region == TestMaps.Middle);
        Assert.NotNull(atHome.ValueByRole);
        Assert.Equal(700, atHome.ValueByRole![UnitRole.AntiArmor]);
        Assert.Equal(500, atHome.ValueByRole[UnitRole.AntiInfantry]);
        Assert.Equal(700, Assert.Single(atMiddle.ValueByRole!).Value);
        // The per-location split adds up to the per-role totals.
        foreach ((UnitRole role, double value) in features.Army.ValueByRole)
        {
            Assert.Equal(value, features.Army.Clusters.Sum(c => c.ValueByRole!.GetValueOrDefault(role)));
        }
    }

    [Fact]
    public void Every_known_enemy_tech_and_production_item_has_its_own_last_seen_age()
    {
        FakeRulesDatabase rules = Rules();
        BeliefModel belief = new(rules, new BeliefOptions());
        FeatureCompiler compiler = new(rules, new FeatureOptions());
        Cell middle = TestMaps.Simple().Regions[1].Center;
        ObservedEntity yard = Own(1, "yard", TestMaps.Simple().Regions[0].Center);

        compiler.Compile(belief.Apply(Frame(0, [yard, Theirs(50, "factory", middle)])));
        compiler.Compile(belief.Apply(Frame(30, [yard, Theirs(51, "rhino", middle)])));
        StrategicFeatures features = compiler.Compile(belief.Apply(Frame(40, [yard])));

        IReadOnlyDictionary<string, double>? ages = features.Enemy.TechLastSeenAgeSeconds;
        Assert.NotNull(ages);
        Assert.Equal(40, ages!["factory"], 3);
        Assert.Equal(10, ages["rhino"], 3);
        Assert.Equal(features.Enemy.KnownTech.OrderBy(static t => t, StringComparer.Ordinal), ages.Keys.OrderBy(static t => t, StringComparer.Ordinal));
        Assert.All(features.Enemy.KnownProduction, p => Assert.True(ages.ContainsKey(p)));
    }

    [Fact]
    public void Own_and_enemy_superweapon_charge_are_features_and_a_new_enemy_superweapon_is_an_event()
    {
        FakeRulesDatabase rules = Rules();
        BeliefModel belief = new(rules, new BeliefOptions());
        FeatureCompiler compiler = new(rules, new FeatureOptions());
        ObservedEntity yard = Own(1, "yard", TestMaps.Simple().Regions[0].Center);

        StrategicFeatures none = compiler.Compile(belief.Apply(Frame(0, [yard], [])));
        Assert.NotNull(none.Superweapons);
        Assert.Empty(none.Superweapons!.Own);
        Assert.Empty(none.Superweapons.Enemy);
        Assert.False(none.Enemy.SuperweaponKnown);

        // RA2 shows every player's superweapon timer to everyone, so the enemy's silo is known without sighting it.
        SuperweaponStatus own = new(Self, "storm", new EntityId(7), 600, 300, false);
        SuperweaponStatus theirs = new(Enemy, "silo", null, 600, 120, false);
        StrategicFeatures charging = compiler.Compile(belief.Apply(Frame(1, [yard], [own, theirs])));

        SuperweaponTimer ownTimer = Assert.Single(charging.Superweapons!.Own);
        Assert.Equal("storm", ownTimer.TypeId);
        Assert.Equal(0.5, ownTimer.ChargeFraction, 6);
        Assert.Equal(300, ownTimer.SecondsToReady, 6);
        SuperweaponTimer enemyTimer = Assert.Single(charging.Superweapons.Enemy);
        Assert.Equal(120, enemyTimer.SecondsToReady, 6);
        Assert.False(enemyTimer.Ready);
        Assert.True(charging.Enemy.SuperweaponKnown);
        StrategicEvent detected = Assert.Single(charging.Events, static e => e.Kind == StrategicEventKind.SuperweaponDetected);
        Assert.Contains("silo", detected.Detail, StringComparison.Ordinal);

        StrategicFeatures still = compiler.Compile(belief.Apply(Frame(2, [yard], [own, theirs with { SecondsToReady = 119 }])));
        Assert.DoesNotContain(still.Events, static e => e.Kind == StrategicEventKind.SuperweaponDetected);
    }

    [Fact]
    public void An_own_superweapon_launch_is_not_reported_as_an_enemy_superweapon()
    {
        FakeRulesDatabase rules = Rules();
        BeliefModel belief = new(rules, new BeliefOptions());
        FeatureCompiler compiler = new(rules, new FeatureOptions());
        ObservedEntity yard = Own(1, "yard", TestMaps.Simple().Regions[0].Center);
        GameEvent ownLaunch = new(GameEventKind.SuperweaponLaunched, GameTime.FromSeconds(1), new EntityId(7), Self, "storm", new Cell(90, 90));

        StrategicFeatures features = compiler.Compile(belief.Apply(Frame(1, [yard]) with { Events = [ownLaunch] }));

        Assert.DoesNotContain(features.Events, static e => e.Kind == StrategicEventKind.SuperweaponDetected);
        Assert.False(features.Enemy.SuperweaponKnown);
    }
}
