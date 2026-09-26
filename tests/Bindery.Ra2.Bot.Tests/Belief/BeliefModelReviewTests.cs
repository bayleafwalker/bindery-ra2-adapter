// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Belief;
using Bindery.Ra2.Bot.Features;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Belief;

/// <summary>Regression tests for belief-model defects found by review.</summary>
public sealed class BeliefModelReviewTests
{
    private static readonly PlayerId Self = new(0);
    private static readonly PlayerId EnemyPlayer = new(1);

    private static readonly UnitRule Rifleman = FakeRulesDatabase.Rule("rifleman", Faction.Allied, EntityKind.Infantry, UnitRole.AntiInfantry, QueueKind.Infantry, 100);
    private static readonly UnitRule Tank = FakeRulesDatabase.Rule("tank", Faction.Allied, EntityKind.Vehicle, UnitRole.AntiArmor, QueueKind.Vehicle, 900);
    private static readonly UnitRule ConYard = FakeRulesDatabase.Rule("conyard", Faction.Allied, EntityKind.Building, UnitRole.Production, QueueKind.Building, 2500);
    private static readonly UnitRule Refinery = FakeRulesDatabase.Rule("refinery", Faction.Allied, EntityKind.Building, UnitRole.Economy, QueueKind.Building, 2000);
    private static readonly UnitRule Tower = FakeRulesDatabase.Rule("tower", Faction.Allied, EntityKind.Building, UnitRole.Defense, QueueKind.Building, 800);

    private static FakeRulesDatabase Rules() => new([Rifleman, Tank, ConYard, Refinery, Tower]);

    private static ObservationFrame Frame(double seconds, IReadOnlyList<ObservedEntity> entities, IReadOnlySet<RegionId> visible) =>
        new(
            GameTime.FromSeconds(seconds), ObservationMode.Belief, Self, Faction.Allied, 5000,
            new PowerState(100, 50), entities, [], [], visible, TestMaps.Simple());

    private static ObservedEntity Own(uint id, string type, Cell at) => new(new EntityId(id), Self, type, at, 100, 100);

    private static ObservedEntity Enemy(uint id, string type, Cell at) => new(new EntityId(id), EnemyPlayer, type, at, 100, 100);

    /// <summary>
    /// Looking at the empty region is evidence the unit left; when the region goes dark again that evidence must
    /// stay spent, so confidence never rises without a new sighting.
    /// </summary>
    [Fact]
    public void Confidence_NeverRises_WhenTheVacantRegionLeavesSight()
    {
        BeliefModel model = new(Rules(), new BeliefOptions());
        ObservedEntity conyard = Own(1, "conyard", new Cell(10, 10));
        model.Apply(Frame(0, [conyard, Enemy(40, "tank", new Cell(50, 50))], new HashSet<RegionId> { TestMaps.Home, TestMaps.Middle }));

        double previous = 1.0;
        for (int t = 1; t <= 60; t++)
        {
            HashSet<RegionId> visible = t <= 30 ? [TestMaps.Home, TestMaps.Middle] : [TestMaps.Home];
            BeliefSnapshot s = model.Apply(Frame(t, [conyard], visible));
            if (s.Enemies.SingleOrDefault() is not { } contact) break;
            Assert.True(contact.Confidence <= previous + 1e-12, $"t={t}: {contact.Confidence} after {previous}");
            previous = contact.Confidence;
        }
        Assert.True(previous <= 0.125 + 1e-9);
    }

    /// <summary>
    /// A building does not walk away: out of sight it is remembered where it stood, so an enemy refinery keeps its
    /// ore field enemy-held; only seeing the spot empty (or a destroyed event) removes it.
    /// </summary>
    [Fact]
    public void EnemyBuilding_IsRememberedOutOfSight_AndForgottenOnlyWhenSeenGone()
    {
        FakeRulesDatabase rules = Rules();
        BeliefModel model = new(rules, new BeliefOptions());
        FeatureCompiler compiler = new(rules, new FeatureOptions());
        ObservedEntity conyard = Own(1, "conyard", new Cell(10, 10));
        compiler.Compile(model.Apply(Frame(10, [conyard, Enemy(60, "refinery", new Cell(50, 50))], new HashSet<RegionId> { TestMaps.Home, TestMaps.Middle })));

        StrategicFeatures f = null!;
        for (int t = 20; t <= 300; t += 10) f = compiler.Compile(model.Apply(Frame(t, [conyard], new HashSet<RegionId> { TestMaps.Home })));

        EnemyContact refinery = Assert.Single(model.Current.Enemies);
        Assert.Equal(1.0, refinery.Confidence);
        Assert.Equal(RegionControl.Enemy, f.MapControl.Control[TestMaps.Middle]);
        Assert.DoesNotContain(TestMaps.Middle, f.MapControl.ExpansionCandidates);

        // Seen empty: vacancy evidence removes it.
        for (int t = 301; t <= 400; t++) model.Apply(Frame(t, [conyard], new HashSet<RegionId> { TestMaps.Home, TestMaps.Middle }));
        Assert.Empty(model.Current.Enemies);
    }

    /// <summary>A tower rushed into our base is not the enemy's start, whatever the region ids.</summary>
    [Fact]
    public void SuspectedStart_IsNeverOurOwnStart()
    {
        BeliefModel model = new(Rules(), new BeliefOptions());
        ObservedEntity conyard = Own(1, "conyard", new Cell(10, 10));
        model.Apply(Frame(0, [conyard], new HashSet<RegionId> { TestMaps.Home }));
        model.Apply(Frame(1, [conyard, Enemy(50, "conyard", new Cell(90, 90))], new HashSet<RegionId> { TestMaps.Home, TestMaps.EnemyStart }));
        BeliefSnapshot s = model.Apply(Frame(2, [conyard, Enemy(51, "tower", new Cell(11, 11))], new HashSet<RegionId> { TestMaps.Home }));

        Assert.Equal(TestMaps.EnemyStart, Assert.Single(s.EnemyPlayers).SuspectedStart);
    }

    /// <summary>With only the tower in our base ever seen, the enemy's start is still the other start by elimination.</summary>
    [Fact]
    public void SuspectedStart_FallsBackToEliminationWhenTheOnlyBuildingSeenIsInOurBase()
    {
        BeliefModel model = new(Rules(), new BeliefOptions());
        ObservedEntity conyard = Own(1, "conyard", new Cell(10, 10));
        model.Apply(Frame(0, [conyard], new HashSet<RegionId> { TestMaps.Home }));
        BeliefSnapshot s = model.Apply(Frame(1, [conyard, Enemy(51, "tower", new Cell(11, 11))], new HashSet<RegionId> { TestMaps.Home }));

        Assert.Equal(TestMaps.EnemyStart, Assert.Single(s.EnemyPlayers).SuspectedStart);
    }
}
