// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using Bindery.Ra2.Bot.Belief;
using Bindery.Ra2.Bot.Features;
using Bindery.Ra2.Bot.Sim;
using Bindery.Ra2.Bot.Tests.Belief;
using Xunit;
using SimTests = Bindery.Ra2.Bot.Tests.Sim;

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
    /// Required fog invariant test (spec invariant 1). Two simulated matches
    /// with the same seed differ only in hidden enemy state: in match A the
    /// enemy has extra credits, a queued item, and an army and a war factory
    /// silently placed in its never-visible start region; match B has none of
    /// that. Each match's belief-mode frames go through its own belief model
    /// and feature compiler, so a leak anywhere from the simulator's fog
    /// filter through Belief to Features makes the two compiled
    /// <see cref="StrategicFeatures"/> differ. A leak that is the same in
    /// both runs (e.g. features inventing enemy tech from the rules or the
    /// enemy's faction) cannot show up in a diff, so the test also checks
    /// that <see cref="EnemyFeatures.KnownTech"/> only ever names enemy types
    /// that actually appeared in a belief-mode frame.
    /// </summary>
    [Fact]
    public void CompiledFeatures_AreIdentical_WhenHiddenEnemyStateDiffers()
    {
        PlayerId observer = new(0);
        PlayerId enemy = new(1);
        SimTests.TestRules rules = new();
        SkirmishSimulation simA = new(SimTests.TestMaps.TwoPlayerCombat(), rules, SimTests.SimTestHelpers.TwoPlayers(seed: 3, maxSeconds: 60));
        SkirmishSimulation simB = new(SimTests.TestMaps.TwoPlayerCombat(), rules, SimTests.SimTestHelpers.TwoPlayers(seed: 3, maxSeconds: 60));
        simA.Step();
        simB.Step();

        RegionId enemyStart = simA.StartRegionOf(enemy);
        Assert.DoesNotContain(enemyStart, simA.VisibleRegionsForProbe(observer));
        SimLeakageProbe.PerturbHidden(simA, observer);
        simA.DebugSpawnSilently(enemy, SimTests.TestRules.Strong, enemyStart);
        simA.DebugSpawnSilently(enemy, SimTests.TestRules.WarFactory, enemyStart);
        Assert.NotEqual(simA.ComputeStateHash(), simB.ComputeStateHash());

        BeliefModel beliefA = new(rules, new BeliefOptions());
        BeliefModel beliefB = new(rules, new BeliefOptions());
        FeatureCompiler compilerA = new(rules, new FeatureOptions());
        FeatureCompiler compilerB = new(rules, new FeatureOptions());
        HashSet<string> enemyTypesInFrames = new(StringComparer.Ordinal);

        int compared = 0;
        for (int frame = 0; frame < GameTime.FramesPerSecond * 10; frame++)
        {
            simA.Step();
            simB.Step();
            ObservationFrame frameA = simA.Observe(observer, ObservationMode.Belief);
            ObservationFrame frameB = simB.Observe(observer, ObservationMode.Belief);
            foreach (ObservedEntity e in frameA.Entities.Where(e => e.Owner != observer)) enemyTypesInFrames.Add(e.TypeId);

            StrategicFeatures featuresA = compilerA.Compile(beliefA.Apply(frameA));
            StrategicFeatures featuresB = compilerB.Compile(beliefB.Apply(frameB));

            Assert.Equal(
                JsonSerializer.Serialize(featuresB, BotJson.Options),
                JsonSerializer.Serialize(featuresA, BotJson.Options));
            Assert.Subset(enemyTypesInFrames, new HashSet<string>(featuresA.Enemy.KnownTech, StringComparer.Ordinal));
            compared++;
        }

        Assert.Equal(GameTime.FramesPerSecond * 10, compared);
        // The hidden army and war factory were never seen, so the enemy's tech is still unknown.
        Assert.DoesNotContain(SimTests.TestRules.WarFactory, enemyTypesInFrames);
        Assert.DoesNotContain(SimTests.TestRules.Strong, enemyTypesInFrames);
    }

    [Fact]
    public void KnownTech_IsEmptyBeforeAnySighting_AndExactlyTheSeenTypeAfterOne()
    {
        // Enemy-faction types in the rules must not become known tech just by existing.
        UnitRule rhino = FakeRulesDatabase.Rule("rhino", Faction.Soviet, EntityKind.Vehicle, UnitRole.AntiArmor, QueueKind.Vehicle, 900);
        UnitRule sovietFactory = FakeRulesDatabase.Rule("sovietfactory", Faction.Soviet, EntityKind.Building, UnitRole.Production, QueueKind.Vehicle, 2000);
        FakeRulesDatabase rules = new([Rifleman, Harvester, ConYard, War, rhino, sovietFactory]);
        BeliefModel belief = new(rules, new BeliefOptions());
        FeatureCompiler compiler = new(rules, new FeatureOptions());
        ObservedEntity ownBuilding = OwnBuilding(1);
        ObservedEntity enemyFactory = new(new EntityId(50), EnemyPlayer, "warfactory", TestMaps.Simple().Regions[0].Center, 1000, 1000);

        StrategicFeatures before = compiler.Compile(belief.Apply(Frame(0, 5000, [ownBuilding])));
        Assert.Empty(before.Enemy.KnownTech);

        StrategicFeatures seen = compiler.Compile(belief.Apply(Frame(5, 5000, [ownBuilding, enemyFactory])));
        Assert.Equal(["warfactory"], seen.Enemy.KnownTech.OrderBy(static t => t, StringComparer.Ordinal));

        // Memory: out of sight again, the tech stays known and nothing else is added.
        StrategicFeatures later = compiler.Compile(belief.Apply(Frame(10, 5000, [ownBuilding])));
        Assert.Equal(["warfactory"], later.Enemy.KnownTech.OrderBy(static t => t, StringComparer.Ordinal));
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

    /// <summary>
    /// The strategist needs the approach an attack will take (proposal: "likely
    /// attack paths"), not just its ETA, to pick a defensive choke.
    /// </summary>
    [Fact]
    public void ThreatAssessment_CarriesTheGroundPathFromTheNearestThreatToTheRegion()
    {
        FakeRulesDatabase rules = Rules();
        BeliefModel belief = new(rules, new BeliefOptions());
        FeatureCompiler compiler = new(rules, new FeatureOptions(ThreatSearchCells: 100));
        ObservedEntity rifle = new(new EntityId(40), EnemyPlayer, "rifleman", TestMaps.Simple().Regions[2].Center, 100, 100);

        StrategicFeatures quiet = compiler.Compile(belief.Apply(Frame(0, 5000, [OwnBuilding(1)])));
        Assert.Empty(Assert.Single(quiet.Threats).LikelyAttackPath!);

        StrategicFeatures threatened = compiler.Compile(belief.Apply(Frame(
            1, 5000, [OwnBuilding(1), rifle], visible: new HashSet<RegionId> { TestMaps.Home, TestMaps.EnemyStart })));
        ThreatAssessment threat = Assert.Single(threatened.Threats);
        Assert.Equal([TestMaps.EnemyStart, TestMaps.Middle, TestMaps.Home], threat.LikelyAttackPath!);
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

    [Fact]
    public void ScoutingCoverageCap_ClampsReportedCoverage_DefaultLeavesItUnchanged()
    {
        FakeRulesDatabase rules = Rules();
        BeliefModel belief = new(rules, new BeliefOptions());
        FeatureCompiler compilerDefault = new(rules, new FeatureOptions());
        FeatureCompiler compilerCapped = new(rules, new FeatureOptions(ScoutingCoverageCap: 0.39));

        BeliefSnapshot fullyScouted = belief.Apply(Frame(
            0, 5000, [OwnBuilding(1)],
            visible: new HashSet<RegionId> { TestMaps.Home, TestMaps.Middle, TestMaps.EnemyStart }));

        Assert.Equal(1.0, compilerDefault.Compile(fullyScouted).Scouting.CoverageFraction, precision: 6);
        Assert.Equal(0.39, compilerCapped.Compile(fullyScouted).Scouting.CoverageFraction, precision: 6);
    }

    [Fact]
    public void ScoutingCoverageCap_ForcesAScoutObjective_DespiteFullActualCoverage()
    {
        FakeRulesDatabase rules = Rules();
        BeliefModel belief = new(rules, new BeliefOptions());
        FeatureCompiler compilerCapped = new(rules, new FeatureOptions(ScoutingCoverageCap: 0.39));

        BeliefSnapshot fullyScouted = belief.Apply(Frame(
            0, 5000, [OwnBuilding(1)],
            visible: new HashSet<RegionId> { TestMaps.Home, TestMaps.Middle, TestMaps.EnemyStart }));

        StrategicFeatures capped = compilerCapped.Compile(fullyScouted);
        Assert.Equal(0.39, capped.Scouting.CoverageFraction, precision: 6);

        // Turtle is not an aggressive posture, so the only way a Scout objective appears here is the
        // capped coverage falling below the 40% threshold in IntentComposer.Objectives.
        IReadOnlyList<Objective> objectives =
            Bindery.Ra2.Bot.Strategy.IntentComposer.Objectives(StrategicPosture.Turtle, capped);
        Assert.Contains(objectives, static o => o.Kind == ObjectiveKind.Scout);
    }
}
