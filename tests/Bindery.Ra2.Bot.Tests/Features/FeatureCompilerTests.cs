// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using Bindery.Ra2.Bot.Belief;
using Bindery.Ra2.Bot.Features;
using Bindery.Ra2.Bot.Tests.Belief;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Features;

public sealed class FeatureCompilerTests
{
    private static readonly PlayerId Self = new(0);
    private static readonly PlayerId EnemyPlayer = new(1);

    private static readonly UnitRule Rifleman = FakeRulesDatabase.Rule("rifleman", Faction.Allied, EntityKind.Infantry, UnitRole.AntiInfantry, QueueKind.Infantry, 100);
    private static readonly UnitRule Harvester = FakeRulesDatabase.Rule("harvester", Faction.Allied, EntityKind.Vehicle, UnitRole.Harvester, QueueKind.Vehicle, 1400);
    private static readonly UnitRule ConYard = FakeRulesDatabase.Rule("conyard", Faction.Allied, EntityKind.Building, UnitRole.Production, QueueKind.Building, 2500, power: -10);
    private static readonly UnitRule War = FakeRulesDatabase.Rule("warfactory", Faction.Allied, EntityKind.Building, UnitRole.Production, QueueKind.Vehicle, 2000, power: -20);

    private static FakeRulesDatabase Rules() => new([Rifleman, Harvester, ConYard, War]);

    private static ObservationFrame Frame(
        double seconds,
        int credits,
        IReadOnlyList<ObservedEntity> entities,
        IReadOnlySet<RegionId>? visible = null,
        IReadOnlyList<GameEvent>? events = null,
        IReadOnlyList<ProductionQueueState>? queues = null) =>
        new(
            GameTime.FromSeconds(seconds), ObservationMode.Belief, Self, Faction.Allied, credits,
            new PowerState(100, 50), entities, queues ?? [], events ?? [],
            visible ?? new HashSet<RegionId> { TestMaps.Home }, TestMaps.Simple());

    private static ObservedEntity OwnBuilding(uint id) => new(new EntityId(id), Self, "conyard", TestMaps.Simple().Regions[0].Center, 1000, 1000);

    /// <summary>
    /// Required fog invariant test (spec invariant 1): two matches whose
    /// hidden enemy state differs — one has an enemy army camped in
    /// <c>EnemyStart</c>, the other has none there at all — but whose
    /// <see cref="ObservationFrame"/> sequences are, and must be, identical
    /// because that region is never in <see cref="ObservationFrame.VisibleRegions"/>
    /// and the hidden unit is therefore never in <see cref="ObservationFrame.Entities"/>
    /// either (a belief-mode adapter never populates what the player cannot
    /// see). Belief and features can only ever be a pure function of the
    /// frames they are handed, so the two runs must compile to
    /// byte-identical <see cref="StrategicFeatures"/>. This is the concrete
    /// counterpart of the spec's "mutate hidden state, diff the context
    /// hash" arena probe, exercised at the unit level for this package.
    /// </summary>
    [Fact]
    public void CompiledFeatures_AreIdentical_WhenHiddenEnemyStateDiffers()
    {
        FakeRulesDatabase rules = Rules();
        BeliefModel beliefA = new(rules, new BeliefOptions());
        BeliefModel beliefB = new(rules, new BeliefOptions());
        FeatureCompiler compilerA = new(rules, new FeatureOptions());
        FeatureCompiler compilerB = new(rules, new FeatureOptions());

        ObservedEntity ownBuilding = OwnBuilding(1);

        // Both matches feed belief the same fog-correct observation stream:
        // the unseen enemy garrison (whether it exists, in match A, or not,
        // in match B) never appears, since EnemyStart is never visible.
        static ObservationFrame FrameAt(double t, int credits) => new(
            GameTime.FromSeconds(t), ObservationMode.Belief, Self, Faction.Allied, credits,
            new PowerState(100, 50), [OwnBuilding(1)], [], [], new HashSet<RegionId> { TestMaps.Home },
            TestMaps.Simple());

        StrategicFeatures? lastA = null, lastB = null;
        for (int i = 0; i < 5; i++)
        {
            double t = i * 5.0;
            int credits = 5000 + i * 10;
            lastA = compilerA.Compile(beliefA.Apply(FrameAt(t, credits)));
            lastB = compilerB.Compile(beliefB.Apply(FrameAt(t, credits)));
        }

        JsonElement jsonA = JsonSerializer.SerializeToElement(lastA, BotJson.Options);
        JsonElement jsonB = JsonSerializer.SerializeToElement(lastB, BotJson.Options);
        Assert.Equal(jsonA.GetRawText(), jsonB.GetRawText());
    }

    [Fact]
    public void ArmyValue_SumsCombatUnitsOnly_ExcludingHarvestersAndBuildings()
    {
        FakeRulesDatabase rules = Rules();
        BeliefModel belief = new(rules, new BeliefOptions());
        FeatureCompiler compiler = new(rules, new FeatureOptions());

        ObservedEntity building = OwnBuilding(1);
        ObservedEntity harvester = new(new EntityId(2), Self, "harvester", TestMaps.Simple().Regions[0].Center, 100, 100);
        ObservedEntity rifle = new(new EntityId(3), Self, "rifleman", TestMaps.Simple().Regions[0].Center, 100, 100);

        BeliefSnapshot snapshot = belief.Apply(Frame(0, 5000, [building, harvester, rifle]));
        StrategicFeatures features = compiler.Compile(snapshot);

        Assert.Equal(100, features.Army.ArmyValue.Current);
        Assert.Equal(100, features.Army.ValueByRole[UnitRole.AntiInfantry]);
        Assert.False(features.Army.ValueByRole.ContainsKey(UnitRole.Harvester));
    }

    /// <summary>
    /// Forces the failure case for event de-duplication: the same ongoing
    /// attack must not spawn a fresh <see cref="StrategicEventKind.BaseUnderAttack"/>
    /// every single compile while the underlying condition persists.
    /// </summary>
    [Fact]
    public void BaseUnderAttack_IsNotReEmittedEveryFrameForTheSameOngoingAttack()
    {
        FakeRulesDatabase rules = Rules();
        BeliefModel belief = new(rules, new BeliefOptions(RecentEventsWindowSeconds: 30));
        FeatureCompiler compiler = new(rules, new FeatureOptions(EventDedupWindowSeconds: 20));

        ObservedEntity building = OwnBuilding(1);
        Cell buildingCell = TestMaps.Simple().Regions[0].Center;

        List<StrategicEvent> seen = [];
        for (int i = 0; i < 4; i++)
        {
            double t = i * 1.0;
            GameEvent attack = new(GameEventKind.UnderAttack, GameTime.FromSeconds(t), new EntityId(1), Self, "conyard", buildingCell);
            BeliefSnapshot snapshot = belief.Apply(Frame(t, 5000, [building], events: [attack]));
            StrategicFeatures features = compiler.Compile(snapshot);
            seen.AddRange(features.Events.Where(static e => e.Kind == StrategicEventKind.BaseUnderAttack));
        }

        Assert.Single(seen);
    }

    [Fact]
    public void CashRunway_IsCappedAtNineNineNineNine_WhenNotSpending()
    {
        FakeRulesDatabase rules = Rules();
        BeliefModel belief = new(rules, new BeliefOptions());
        FeatureCompiler compiler = new(rules, new FeatureOptions());

        BeliefSnapshot snapshot = belief.Apply(Frame(0, 5000, [OwnBuilding(1)]));
        StrategicFeatures features = compiler.Compile(snapshot);

        Assert.Equal(9999.0, features.Economy.CashRunwaySeconds);
    }

    [Fact]
    public void ThreatAssessment_ForBaseRegion_UsesLargeCapWhenNoEnemyIsVisible()
    {
        FakeRulesDatabase rules = Rules();
        BeliefModel belief = new(rules, new BeliefOptions());
        FeatureCompiler compiler = new(rules, new FeatureOptions());

        BeliefSnapshot snapshot = belief.Apply(Frame(0, 5000, [OwnBuilding(1)]));
        StrategicFeatures features = compiler.Compile(snapshot);

        ThreatAssessment threat = Assert.Single(features.Threats);
        Assert.True(threat.IsBase);
        Assert.Equal(10.0, threat.LocalForceRatio);
        Assert.Equal(FeatureCompiler.UnknownSeconds, threat.EnemyEtaSeconds);
    }

    [Fact]
    public void ScoutingCoverage_CountsOnlyRegionsSeenWithinTheWindow()
    {
        FakeRulesDatabase rules = Rules();
        BeliefModel belief = new(rules, new BeliefOptions());
        FeatureCompiler compiler = new(rules, new FeatureOptions(ScoutingWindowSeconds: 10));

        belief.Apply(Frame(0, 5000, [OwnBuilding(1)], visible: new HashSet<RegionId> { TestMaps.Home, TestMaps.Middle }));
        BeliefSnapshot later = belief.Apply(Frame(20, 5000, [OwnBuilding(1)], visible: new HashSet<RegionId> { TestMaps.Home }));
        StrategicFeatures features = compiler.Compile(later);

        // Home was just seen (age 0), Middle was seen 20s ago (stale beyond the 10s window), EnemyStart never seen.
        Assert.Equal(1.0 / 3.0, features.Scouting.CoverageFraction, precision: 6);
    }
}
