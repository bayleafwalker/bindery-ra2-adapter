// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arbitration;
using Bindery.Ra2.Bot.Belief;
using Bindery.Ra2.Bot.Features;
using Bindery.Ra2.Bot.Tests.Belief;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Features;

/// <summary>
/// Regression tests for defects found by review of the feature compiler: each
/// test reproduces a concrete wrong output the strategist used to receive.
/// </summary>
public sealed class FeatureCompilerReviewTests
{
    private static readonly PlayerId Self = new(0);
    private static readonly PlayerId EnemyPlayer = new(1);

    private static readonly UnitRule Rifleman = FakeRulesDatabase.Rule("rifleman", Faction.Allied, EntityKind.Infantry, UnitRole.AntiInfantry, QueueKind.Infantry, 100);
    private static readonly UnitRule Harvester = FakeRulesDatabase.Rule("harvester", Faction.Allied, EntityKind.Vehicle, UnitRole.Harvester, QueueKind.Vehicle, 1400);
    private static readonly UnitRule Tank = FakeRulesDatabase.Rule("tank", Faction.Allied, EntityKind.Vehicle, UnitRole.AntiArmor, QueueKind.Vehicle, 900, buildSeconds: 10);
    private static readonly UnitRule ConYard = FakeRulesDatabase.Rule("conyard", Faction.Allied, EntityKind.Building, UnitRole.Production, QueueKind.Building, 2500, power: -10);
    private static readonly UnitRule Refinery = FakeRulesDatabase.Rule("refinery", Faction.Allied, EntityKind.Building, UnitRole.Economy, QueueKind.Building, 2000, power: -20);

    private static FakeRulesDatabase Rules() => new([Rifleman, Harvester, Tank, ConYard, Refinery]);

    private static readonly Cell HomeCell = new(10, 10);
    private static readonly Cell MiddleCell = new(50, 50);
    private static readonly Cell EnemyCell = new(90, 90);

    private static ObservationFrame Frame(
        double seconds,
        int credits,
        IReadOnlyList<ObservedEntity> entities,
        IReadOnlySet<RegionId>? visible = null,
        IReadOnlyList<GameEvent>? events = null,
        IReadOnlyList<ProductionQueueState>? queues = null,
        PowerState? power = null,
        MapInfo? map = null) =>
        new(
            GameTime.FromSeconds(seconds), ObservationMode.Belief, Self, Faction.Allied, credits,
            power ?? new PowerState(100, 50), entities, queues ?? [], events ?? [],
            visible ?? new HashSet<RegionId> { TestMaps.Home }, map ?? TestMaps.Simple());

    private static ObservedEntity Own(uint id, string type, Cell at) => new(new EntityId(id), Self, type, at, 100, 100);

    private static ObservedEntity Enemy(uint id, string type, Cell at) => new(new EntityId(id), EnemyPlayer, type, at, 100, 100);

    private static ObservedEntity ConYardAtHome() => Own(1, "conyard", HomeCell);

    private static (BeliefModel Belief, FeatureCompiler Compiler) New(FeatureOptions? options = null)
    {
        FakeRulesDatabase rules = Rules();
        return (new BeliefModel(rules, new BeliefOptions()), new FeatureCompiler(rules, options ?? new FeatureOptions()));
    }

    /// <summary>
    /// An enemy's kill of our unit (an <see cref="GameEventKind.EntityKilledByUs"/> naming our own entity, as
    /// oracle frames deliver the opponent's kill events) is a loss, never a kill. A kill of an enemy counts under
    /// both producer conventions: the simulator names the killer as owner, the RA2 assembler the victim.
    /// </summary>
    [Fact]
    public void KillsValue_CountsOnlyEnemyVictims_NotOurOwnLosses()
    {
        (BeliefModel belief, FeatureCompiler compiler) = New();
        HashSet<RegionId> visible = [TestMaps.Home];
        ObservedEntity rifle = Own(3, "rifleman", HomeCell);
        ObservedEntity enemyA = Enemy(40, "rifleman", new Cell(11, 11));
        ObservedEntity enemyB = Enemy(41, "tank", new Cell(12, 12));
        compiler.Compile(belief.Apply(Frame(0, 5000, [ConYardAtHome(), rifle, enemyA, enemyB], visible)));

        GameTime t1 = GameTime.FromSeconds(1);
        GameEvent[] events =
        [
            new(GameEventKind.EntityDestroyed, t1, rifle.Id, Self, "rifleman", HomeCell),
            new(GameEventKind.EntityKilledByUs, t1, rifle.Id, EnemyPlayer, "rifleman", HomeCell),
            new(GameEventKind.EntityKilledByUs, t1, enemyA.Id, Self, "rifleman", enemyA.Position),
            new(GameEventKind.EntityKilledByUs, t1, enemyB.Id, EnemyPlayer, "tank", enemyB.Position),
        ];
        StrategicFeatures features = compiler.Compile(belief.Apply(Frame(1, 5000, [ConYardAtHome()], visible, events)));

        Assert.Equal(100, features.Army.LossesValue.Current);
        Assert.Equal(1000, features.Army.KillsValue.Current);
    }

    /// <summary>
    /// The simulator debits a unit's full cost when it is queued; a one-frame credit derivative then reads as a
    /// huge negative income on the enqueue frame and as phantom income while the item builds. The windowed
    /// estimate keeps the error within the item's cost over the window, whatever the source's debit timing.
    /// </summary>
    [Fact]
    public void IncomePerMinute_IsWindowed_SoAnUpFrontDebitIsNotAMultiThousandSpike()
    {
        (BeliefModel belief, FeatureCompiler compiler) = New();
        double worst = 0;
        const double step = 1.0 / 15.0;
        for (int frame = 0; frame <= 45 * 15; frame++)
        {
            double t = frame * step;
            bool building = t >= 20 && t < 30;
            int credits = t >= 20 ? 4100 : 5000;
            IReadOnlyList<ProductionQueueState> queues = building
                ? [new ProductionQueueState(QueueKind.Vehicle, [new QueueItem("tank", (t - 20) / 10, false, false)], 1)]
                : [];
            StrategicFeatures f = compiler.Compile(belief.Apply(Frame(t, credits, [ConYardAtHome()], queues: queues)));
            worst = Math.Max(worst, Math.Abs(f.Economy.IncomePerMinute.Current));
        }

        // True income is zero throughout; 900 credits over a 15 s window is 3600/min.
        Assert.True(worst <= 3600.0 + 1e-6, $"worst |income| {worst}/min");
    }

    /// <summary>A queue with one factory builds one item at a time; the items waiting behind it cost nothing yet.</summary>
    [Fact]
    public void Spending_CountsOnlyAsManyItemsAsTheQueueHasFactories()
    {
        (BeliefModel belief, FeatureCompiler compiler) = New();
        IReadOnlyList<ProductionQueueState> queues =
        [
            new ProductionQueueState(QueueKind.Vehicle,
            [
                new QueueItem("tank", 0.3, false, false), new QueueItem("tank", 0, false, false),
                new QueueItem("tank", 0, false, false), new QueueItem("tank", 0, false, false),
            ], 1),
        ];
        compiler.Compile(belief.Apply(Frame(0, 2000, [ConYardAtHome()], queues: queues)));
        StrategicFeatures f = compiler.Compile(belief.Apply(Frame(1, 1910, [ConYardAtHome()], queues: queues)));

        Assert.Equal(5400, f.Economy.SpendingPerMinute.Current, precision: 6);
        Assert.Equal(0, f.Economy.IncomePerMinute.Current, precision: 6);
    }

    /// <summary>Runway is how long the bank lasts at the net burn; with credits rising it never runs out.</summary>
    [Fact]
    public void CashRunway_UsesNetBurn_AndIsUnboundedWhileCreditsRise()
    {
        (BeliefModel belief, FeatureCompiler compiler) = New();
        IReadOnlyList<ProductionQueueState> queues = [new ProductionQueueState(QueueKind.Infantry, [new QueueItem("rifleman", 0.5, false, false)], 1)];
        StrategicFeatures f = null!;
        for (int t = 0; t <= 30; t++) f = compiler.Compile(belief.Apply(Frame(t, 1000 + 50 * t, [ConYardAtHome()], queues: queues)));
        Assert.Equal(FeatureCompiler.UnknownSeconds, f.Economy.CashRunwaySeconds);

        // Spending 10/s with no income: the bank drains at 10/s, so 200 credits last 20 s.
        (belief, compiler) = New();
        for (int t = 0; t <= 30; t++) f = compiler.Compile(belief.Apply(Frame(t, 500 - 10 * t, [ConYardAtHome()], queues: queues)));
        Assert.Equal(20.0, f.Economy.CashRunwaySeconds, precision: 6);
    }

    /// <summary>
    /// A lone enemy unit walking through an ore field is not an enemy expansion, and a harvester coming and going
    /// is not an expansion taken; neither hides the field from the expansion candidates.
    /// </summary>
    [Fact]
    public void ExpansionEvents_NeedBuildings_NotPassingUnits()
    {
        (BeliefModel belief, FeatureCompiler compiler) = New();
        HashSet<RegionId> homeAndMiddle = [TestMaps.Home, TestMaps.Middle];
        List<StrategicEvent> events = [];
        StrategicFeatures f;

        compiler.Compile(belief.Apply(Frame(0, 5000, [ConYardAtHome(), Own(2, "harvester", MiddleCell)], homeAndMiddle)));
        compiler.Compile(belief.Apply(Frame(1, 5000, [ConYardAtHome(), Own(2, "harvester", MiddleCell)], homeAndMiddle)));
        f = compiler.Compile(belief.Apply(Frame(2, 5000, [ConYardAtHome(), Own(2, "harvester", HomeCell), Enemy(40, "rifleman", MiddleCell)], homeAndMiddle)));
        events.AddRange(f.Events);
        Assert.Contains(TestMaps.Middle, f.MapControl.ExpansionCandidates);
        for (int t = 3; t <= 31; t++)
        {
            Cell harvesterAt = t >= 31 ? MiddleCell : HomeCell;
            f = compiler.Compile(belief.Apply(Frame(t, 5000, [ConYardAtHome(), Own(2, "harvester", harvesterAt)], new HashSet<RegionId> { TestMaps.Home })));
            events.AddRange(f.Events);
        }
        Assert.DoesNotContain(events, static e => e.Kind is StrategicEventKind.EnemyExpansionSeen or StrategicEventKind.ExpansionTaken);

        // An enemy refinery is an enemy expansion (once), and the field stops being a candidate.
        events.Clear();
        for (int t = 32; t <= 36; t++)
        {
            f = compiler.Compile(belief.Apply(Frame(t, 5000, [ConYardAtHome(), Enemy(60, "refinery", MiddleCell)], homeAndMiddle)));
            events.AddRange(f.Events);
        }
        Assert.Single(events, static e => e.Kind == StrategicEventKind.EnemyExpansionSeen && e.Region == TestMaps.Middle);
        Assert.DoesNotContain(TestMaps.Middle, f.MapControl.ExpansionCandidates);
    }

    [Fact]
    public void ExpansionTaken_FiresOnceForAnOwnBuildingInAnOreRegion()
    {
        (BeliefModel belief, FeatureCompiler compiler) = New();
        List<StrategicEvent> events = [];
        compiler.Compile(belief.Apply(Frame(0, 5000, [ConYardAtHome()])));
        for (int t = 1; t <= 10; t++)
        {
            // A harvester flickering in and out beside the refinery does not re-fire the event.
            List<ObservedEntity> entities = [ConYardAtHome(), Own(5, "refinery", MiddleCell)];
            if (t % 2 == 0) entities.Add(Own(6, "harvester", MiddleCell));
            events.AddRange(compiler.Compile(belief.Apply(Frame(t, 5000, entities, new HashSet<RegionId> { TestMaps.Home, TestMaps.Middle }))).Events);
        }
        Assert.Single(events, static e => e.Kind == StrategicEventKind.ExpansionTaken && e.Region == TestMaps.Middle);
    }

    /// <summary>Watching an enemy building says nothing about where its army is.</summary>
    [Fact]
    public void ArmyNotSeen_IsReportedWhileOnlyEnemyBuildingsAreWatched()
    {
        (BeliefModel belief, FeatureCompiler compiler) = New();
        HashSet<RegionId> visible = [TestMaps.Home, TestMaps.EnemyStart];
        StrategicFeatures f = null!;
        for (int t = 0; t <= 200; t += 5)
        {
            f = compiler.Compile(belief.Apply(Frame(t, 5000,
                [ConYardAtHome(), Own(2, "rifleman", EnemyCell), Enemy(50, "conyard", EnemyCell)], visible)));
        }
        Assert.Contains(f.Scouting.ImportantUnknowns, static u => u.Contains("army not seen", StringComparison.Ordinal));
        Assert.DoesNotContain(f.Scouting.ImportantUnknowns, static u => u.Contains("tech unknown", StringComparison.Ordinal));
    }

    /// <summary>A match in which no enemy was ever seen is the least scouted there is, not one with nothing unknown.</summary>
    [Fact]
    public void ImportantUnknowns_AreReportedBeforeAnyEnemyIsSeen()
    {
        (BeliefModel belief, FeatureCompiler compiler) = New();
        StrategicFeatures f = null!;
        for (int t = 0; t <= 300; t += 10) f = compiler.Compile(belief.Apply(Frame(t, 5000, [ConYardAtHome()])));

        Assert.Contains(f.Scouting.ImportantUnknowns, static u => u.Contains("start unscouted", StringComparison.Ordinal));
        Assert.Contains(f.Scouting.ImportantUnknowns, static u => u.Contains("army not seen", StringComparison.Ordinal));
        Assert.Contains(f.Scouting.ImportantUnknowns, static u => u.Contains("tech unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void ArmyValueSwing_DoesNotFireForTheFirstCheapUnit()
    {
        (BeliefModel belief, FeatureCompiler compiler) = New();
        List<StrategicEvent> events = [];
        for (int t = 0; t <= 25; t++)
        {
            List<ObservedEntity> entities = [ConYardAtHome()];
            if (t >= 20) entities.Add(Own(3, "rifleman", HomeCell));
            events.AddRange(compiler.Compile(belief.Apply(Frame(t, 5000, entities))).Events);
        }
        Assert.DoesNotContain(events, static e => e.Kind == StrategicEventKind.ArmyValueSwing);
    }

    private static List<StrategicEvent> SlowDecline(FeatureOptions options)
    {
        (BeliefModel belief, FeatureCompiler compiler) = New(options);
        List<StrategicEvent> events = [];
        for (int t = 0; t <= 30; t++)
        {
            int alive = t < 10 ? 40 : Math.Max(20, 40 - (t - 10));
            List<ObservedEntity> entities = [ConYardAtHome(), .. Enumerable.Range(0, alive).Select(i => Own((uint)(100 + i), "rifleman", HomeCell))];
            events.AddRange(compiler.Compile(belief.Apply(Frame(t, 5000, entities))).Events);
        }
        return events;
    }

    [Fact]
    public void ArmyValueSwingWindowSeconds_ChangesTheWindowTheSwingIsMeasuredOver()
    {
        // Losing 100 of 4000 per second: 25% goes by within 15 s, never within 5 s.
        Assert.Contains(SlowDecline(new FeatureOptions()), static e => e.Kind == StrategicEventKind.ArmyValueSwing);
        Assert.DoesNotContain(SlowDecline(new FeatureOptions(ArmyValueSwingWindowSeconds: 5)), static e => e.Kind == StrategicEventKind.ArmyValueSwing);
    }

    [Fact]
    public void LowPower_IsGatedByLowPowerGraceSeconds()
    {
        (BeliefModel belief, FeatureCompiler compiler) = New(new FeatureOptions(EventDedupWindowSeconds: 20, LowPowerGraceSeconds: 60));
        List<StrategicEvent> events = [];
        for (int t = 0; t <= 50; t++)
            events.AddRange(compiler.Compile(belief.Apply(Frame(t, 5000, [ConYardAtHome()], power: new PowerState(50, 100)))).Events);
        Assert.Single(events, static e => e.Kind == StrategicEventKind.LowPower);
    }
}
