// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arbitration;
using Bindery.Ra2.Bot.Belief;
using Bindery.Ra2.Bot.Features;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Tests.Belief;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arbitration;

/// <summary>Enemy-composition and map-control condition metrics.</summary>
public sealed class ExtendedMetricsTests
{
    private static StrategicFeatures With(
        IReadOnlyDictionary<EntityKind, double>? byClass, double confidence = 0.8, IReadOnlyDictionary<RegionId, RegionControl>? control = null)
    {
        StrategicFeatures f = Fx.Features(10);
        return f with
        {
            Enemy = f.Enemy with { ValueByClass = byClass, ArmyValueConfidence = confidence },
            MapControl = f.MapControl with { Control = control ?? f.MapControl.Control },
        };
    }

    private static double Measure(ConditionMetric metric, StrategicFeatures f)
    {
        Assert.True(ConditionEvaluator.TryMeasure(metric, null, f, out double value, out ValidationIssue? issue), issue?.Code);
        return value;
    }

    [Fact]
    public void Shares_are_value_weighted_and_sum_over_all_classes()
    {
        StrategicFeatures f = With(new Dictionary<EntityKind, double>
        {
            [EntityKind.Aircraft] = 300, [EntityKind.Vehicle] = 500, [EntityKind.Infantry] = 100, [EntityKind.Naval] = 100,
        });
        Assert.Equal(0.3, Measure(ConditionMetric.EnemyAirShare, f), 9);
        Assert.Equal(0.5, Measure(ConditionMetric.EnemyVehicleShare, f), 9);
        Assert.Equal(0.1, Measure(ConditionMetric.EnemyInfantryShare, f), 9);
        Assert.Equal(0.8, Measure(ConditionMetric.EnemyArmyConfidence, f), 9);
    }

    [Fact]
    public void A_single_sighting_is_under_the_floor_and_enough_value_gives_the_true_share()
    {
        StrategicFeatures one = With(new Dictionary<EntityKind, double> { [EntityKind.Aircraft] = 400 });
        Assert.Equal(0, Measure(ConditionMetric.EnemyAirShare, one));
        Assert.False(ConditionEvaluator.Holds(new Condition(ConditionMetric.EnemyAirShare, Comparison.Ge, 0.3), one));

        StrategicFeatures enough = With(new Dictionary<EntityKind, double> { [EntityKind.Aircraft] = 400, [EntityKind.Vehicle] = 200 });
        Assert.Equal(400.0 / 600.0, Measure(ConditionMetric.EnemyAirShare, enough), 9);
        Assert.Equal(200.0 / 600.0, Measure(ConditionMetric.EnemyVehicleShare, enough), 9);
    }

    [Fact]
    public void Nothing_seen_gives_zero_shares_without_dividing_by_zero()
    {
        foreach (StrategicFeatures f in new[] { With(new Dictionary<EntityKind, double>(), confidence: 0), With(null, confidence: 0) })
        {
            Assert.Equal(0, Measure(ConditionMetric.EnemyAirShare, f));
            Assert.Equal(0, Measure(ConditionMetric.EnemyVehicleShare, f));
            Assert.Equal(0, Measure(ConditionMetric.EnemyInfantryShare, f));
            Assert.Equal(0, Measure(ConditionMetric.EnemyArmyConfidence, f));
        }
    }

    [Fact]
    public void Owned_regions_counts_only_own_control_and_needs_no_region()
    {
        StrategicFeatures f = With(null, control: new Dictionary<RegionId, RegionControl>
        {
            [Fx.R0] = RegionControl.Own, [Fx.R1] = RegionControl.Contested, [Fx.R2] = RegionControl.Own, [Fx.R3] = RegionControl.Enemy,
        });
        Assert.Equal(2, Measure(ConditionMetric.OwnedRegions, f));
        Assert.Equal(0, Measure(ConditionMetric.OwnedRegions, With(null)));
        foreach (ConditionMetric metric in ConditionMetrics_Extended())
        {
            Assert.False(ConditionEvaluator.RequiresRegion(metric));
        }
    }

    private static IEnumerable<ConditionMetric> ConditionMetrics_Extended() => Bindery.Ra2.Bot.Claude.ConditionMetrics.Extended;

    [Fact]
    public void Owned_regions_needs_a_structure_or_combat_units_and_no_remembered_enemy()
    {
        UnitRule rifle = FakeRulesDatabase.Rule("rifleman", Faction.Allied, EntityKind.Infantry, UnitRole.AntiInfantry, QueueKind.Infantry, 100);
        UnitRule harv = FakeRulesDatabase.Rule("harvester", Faction.Allied, EntityKind.Vehicle, UnitRole.Harvester, QueueKind.Vehicle, 1400);
        UnitRule conYard = FakeRulesDatabase.Rule("conyard", Faction.Allied, EntityKind.Building, UnitRole.Production, QueueKind.Building, 2500);
        FakeRulesDatabase rules = new([rifle, harv, conYard]);
        BeliefModel belief = new(rules, new BeliefOptions());
        FeatureCompiler compiler = new(rules, new FeatureOptions());
        PlayerId self = new(0), enemy = new(1);
        MapInfo map = TestMaps.Simple();
        HashSet<RegionId> visible = [TestMaps.Home, TestMaps.Middle, TestMaps.EnemyStart];

        ObservedEntity yard = new(new EntityId(1), self, "conyard", map.Regions[0].Center, 1000, 1000);
        ObservedEntity harvester = new(new EntityId(2), self, "harvester", map.Regions[1].Center, 1000, 1000);
        ObservedEntity soldier = new(new EntityId(3), self, "rifleman", map.Regions[2].Center, 100, 100);
        ObservedEntity foe = new(new EntityId(40), enemy, "rifleman", map.Regions[2].Center, 100, 100);

        ObservationFrame Frame(double s, params ObservedEntity[] e) => new(
            GameTime.FromSeconds(s), ObservationMode.Belief, self, Faction.Allied, 5000, new PowerState(100, 50), e, [], [], visible, map);

        // Yard holds Home; a harvester alone makes Middle Own-controlled but does not hold it; soldier holds EnemyStart.
        StrategicFeatures f = compiler.Compile(belief.Apply(Frame(1, yard, harvester, soldier)));
        Assert.Equal(RegionControl.Own, f.MapControl.Control[TestMaps.Middle]);
        Assert.Equal(2, Measure(ConditionMetric.OwnedRegions, f));

        // A remembered enemy in EnemyStart contests it, so only the yard's region is held.
        StrategicFeatures g = compiler.Compile(belief.Apply(Frame(2, yard, harvester, soldier, foe)));
        Assert.Equal(1, Measure(ConditionMetric.OwnedRegions, g));
    }

    [Fact]
    public void Threshold_comparison_uses_the_share()
    {
        StrategicFeatures f = With(new Dictionary<EntityKind, double> { [EntityKind.Aircraft] = 300, [EntityKind.Vehicle] = 700 });
        Assert.True(ConditionEvaluator.Holds(new Condition(ConditionMetric.EnemyAirShare, Comparison.Ge, 0.3), f));
        Assert.False(ConditionEvaluator.Holds(new Condition(ConditionMetric.EnemyAirShare, Comparison.Gt, 0.3), f));
    }

    [Fact]
    public void Phase_tracker_enters_a_phase_whose_enter_condition_is_an_air_share()
    {
        Playbook playbook = PhaseTests.ExpandTechAttack() with
        {
            Id = "test-flak",
            Phases =
            [
                new PlaybookPhase("start", []),
                new PlaybookPhase("flak", [new Condition(ConditionMetric.EnemyAirShare, Comparison.Ge, 0.3)]),
            ],
        };
        PlaybookLibrary library = new([playbook]);
        StrategicIntent intent = Fx.Intent("a", "test-flak", StrategicPosture.Expand, issuedAt: 0, lifetime: 1000,
            budget: new BudgetShares(0.7, 0.1, 0.1, 0.1), confidence: 0.5);
        PhaseTracker tracker = new();
        tracker.Reset(intent, library);

        Assert.Null(tracker.Advance(With(new Dictionary<EntityKind, double> { [EntityKind.Aircraft] = 200, [EntityKind.Vehicle] = 800 })));
        Assert.Equal("start", tracker.Name);
        PhaseChange? change = tracker.Advance(With(new Dictionary<EntityKind, double> { [EntityKind.Aircraft] = 400, [EntityKind.Vehicle] = 600 }));
        Assert.Equal("flak", change?.ToName);
        Assert.Equal("flak", tracker.Name);
    }

    [Fact]
    public void Fog_a_unit_not_in_belief_does_not_count_and_a_seen_one_does()
    {
        UnitRule rifle = FakeRulesDatabase.Rule("rifleman", Faction.Allied, EntityKind.Infantry, UnitRole.AntiInfantry, QueueKind.Infantry, 100);
        UnitRule yak = FakeRulesDatabase.Rule("yak", Faction.Soviet, EntityKind.Aircraft, UnitRole.AntiArmor, QueueKind.Aircraft, 1000);
        UnitRule conYard = FakeRulesDatabase.Rule("conyard", Faction.Allied, EntityKind.Building, UnitRole.Production, QueueKind.Building, 2500);
        FakeRulesDatabase rules = new([rifle, yak, conYard]);
        BeliefModel belief = new(rules, new BeliefOptions());
        FeatureCompiler compiler = new(rules, new FeatureOptions());
        PlayerId self = new(0), enemy = new(1);
        MapInfo map = TestMaps.Simple();
        ObservedEntity own = new(new EntityId(1), self, "conyard", map.Regions[0].Center, 1000, 1000);
        ObservedEntity seenRifle = new(new EntityId(40), enemy, "rifleman", map.Regions[0].Center, 100, 100);
        ObservedEntity hiddenYak = new(new EntityId(41), enemy, "yak", map.Regions[2].Center, 100, 100);
        HashSet<RegionId> homeOnly = [TestMaps.Home];

        ObservationFrame Frame(double seconds, params ObservedEntity[] entities) => new(
            GameTime.FromSeconds(seconds), ObservationMode.Belief, self, Faction.Allied, 5000,
            new PowerState(100, 50), entities, [], [], homeOnly, map);

        // The enemy has an aircraft in a region we cannot see; only the infantry is in belief.
        StrategicFeatures f = compiler.Compile(belief.Apply(Frame(1, own, seenRifle)));
        Assert.Equal(0, ConditionEvaluator.ClassShare(f, EntityKind.Aircraft));
        Assert.Equal(0, ConditionEvaluator.ClassShare(f, EntityKind.Infantry)); // one rifleman: under the floor
        Assert.False(f.Enemy.ValueByClass!.ContainsKey(EntityKind.Aircraft));

        // Once the aircraft is in a visible region it is in belief and counts.
        HashSet<RegionId> both = [TestMaps.Home, TestMaps.EnemyStart];
        ObservationFrame seen = new(GameTime.FromSeconds(2), ObservationMode.Belief, self, Faction.Allied, 5000,
            new PowerState(100, 50), [own, seenRifle, hiddenYak], [], [], both, map);
        StrategicFeatures g = compiler.Compile(belief.Apply(seen));
        Assert.True(ConditionEvaluator.ClassShare(g, EntityKind.Aircraft) > 0.5);
    }
}
