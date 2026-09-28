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
        ObservedEntity enemyC = Enemy(42, "rifleman", new Cell(13, 13));
        compiler.Compile(belief.Apply(Frame(0, 5000, [ConYardAtHome(), rifle, enemyA, enemyB, enemyC], visible)));

        // A kill's owner is its killer (GameEvent.Owner). The enemy's kill of our rifleman (an oracle stream shows
        // it) and its kill of its own unit are not ours; a producer that wrongly names us as the killer of our own
        // unit is caught by the own-victim guard.
        GameTime t1 = GameTime.FromSeconds(1);
        GameEvent[] events =
        [
            new(GameEventKind.EntityDestroyed, t1, rifle.Id, Self, "rifleman", HomeCell),
            new(GameEventKind.EntityKilledByUs, t1, rifle.Id, EnemyPlayer, "rifleman", HomeCell),
            new(GameEventKind.EntityKilledByUs, t1, rifle.Id, Self, "rifleman", HomeCell),
            new(GameEventKind.EntityKilledByUs, t1, enemyA.Id, Self, "rifleman", enemyA.Position),
            new(GameEventKind.EntityKilledByUs, t1, enemyB.Id, Self, "tank", enemyB.Position),
            new(GameEventKind.EntityKilledByUs, t1, enemyC.Id, EnemyPlayer, "rifleman", enemyC.Position),
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

    /// <summary>
    /// Bots enqueue in the opening seconds, before a full income window of history exists. Dividing the credit
    /// change by the short span seen so far turned a 900-credit debit at t=0.2 s into a -270000/min reading; the
    /// warm-up must be bounded exactly like a full window.
    /// </summary>
    [Theory]
    [InlineData(0.2)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void IncomePerMinute_IsBoundedBeforeTheWindowHasFilled(double enqueueAt)
    {
        (BeliefModel belief, FeatureCompiler compiler) = New();
        double worst = 0;
        const double step = 1.0 / 15.0;
        for (int frame = 0; frame <= 30 * 15; frame++)
        {
            double t = frame * step;
            bool building = t >= enqueueAt && t < enqueueAt + 10;
            int credits = t >= enqueueAt ? 4100 : 5000;
            IReadOnlyList<ProductionQueueState> queues = building
                ? [new ProductionQueueState(QueueKind.Vehicle, [new QueueItem("tank", (t - enqueueAt) / 10, false, false)], 1)]
                : [];
            StrategicFeatures f = compiler.Compile(belief.Apply(Frame(t, credits, [ConYardAtHome()], queues: queues)));
            worst = Math.Max(worst, Math.Abs(f.Economy.IncomePerMinute.Current));
        }

        Assert.True(worst <= 3600.0 + 1e-6, $"worst |income| {worst}/min");
    }

    /// <summary>
    /// The spec's trend ring buffer holds per-second samples, not one per compile: at a 15 fps cadence a 65 s
    /// history would otherwise hold ~1000 samples. Income integrates spending per compile regardless, so sampling
    /// once per second loses nothing: a steady spend is still read as zero income.
    /// </summary>
    [Fact]
    public void History_IsSampledOncePerSecond_AndSteadySpendStillReadsAsZeroIncome()
    {
        (BeliefModel belief, FeatureCompiler compiler) = New();
        const double step = 1.0 / 15.0;
        StrategicFeatures f = null!;
        IReadOnlyList<ProductionQueueState> queues = [new ProductionQueueState(QueueKind.Vehicle, [new QueueItem("tank", 0.5, false, false)], 1)];
        for (int frame = 0; frame <= 80 * 15; frame++)
        {
            double t = frame * step;
            // The tank costs 90 credits per second while it builds; the bank drains exactly that.
            f = compiler.Compile(belief.Apply(Frame(t, (int)Math.Round(9000 - 90 * t), [ConYardAtHome()], queues: queues)));
        }

        Assert.InRange(compiler.HistorySampleCount, 60, 67);
        Assert.InRange(f.Economy.IncomePerMinute.Current, -60, 60);
    }

    /// <summary>
    /// Expansion candidates are ore fields we could still take. The home field, already served by our refinery,
    /// used to sort first (distance zero), so the composer's Expand objective named it and the new refinery crowded
    /// the home field instead of taking new ore.
    /// </summary>
    [Fact]
    public void ExpansionCandidates_ExcludeOreRegionsWeAlreadyHoldARefineryIn()
    {
        HashSet<RegionId> visible = [TestMaps.Home, TestMaps.Middle];
        (BeliefModel belief, FeatureCompiler compiler) = New();
        StrategicFeatures without = compiler.Compile(belief.Apply(Frame(0, 5000, [ConYardAtHome()], visible)));
        Assert.Contains(TestMaps.Middle, without.MapControl.ExpansionCandidates);

        StrategicFeatures with = compiler.Compile(belief.Apply(Frame(1, 5000, [ConYardAtHome(), Own(2, "refinery", MiddleCell)], visible)));
        Assert.DoesNotContain(TestMaps.Middle, with.MapControl.ExpansionCandidates);
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

    /// <summary>
    /// RA2 builds one item per queue at a time; extra factories only speed that one item up (here the fixture's
    /// sqrt(factories), since it does not set MultipleFactory=). Two factories with three unheld items must spend
    /// at the sped-up rate of the single active item, not the sum of two items' rates.
    /// </summary>
    [Fact]
    public void Spending_OneItemPerQueue_SpedUpByFactoryCount_NotSummedAcrossItems()
    {
        (BeliefModel belief, FeatureCompiler compiler) = New();
        IReadOnlyList<ProductionQueueState> queues =
        [
            new ProductionQueueState(QueueKind.Vehicle,
            [
                new QueueItem("tank", 0.3, false, false), new QueueItem("tank", 0, false, false),
                new QueueItem("tank", 0, false, false),
            ], 2),
        ];
        compiler.Compile(belief.Apply(Frame(0, 20000, [ConYardAtHome()], queues: queues)));
        StrategicFeatures f = compiler.Compile(belief.Apply(Frame(1, 19000, [ConYardAtHome()], queues: queues)));

        // tank: 900 cost / 10 s = 90/s, times sqrt(2) for the second factory, times 60 for per-minute.
        double expected = 900.0 / 10.0 * Math.Sqrt(2) * 60.0;
        Assert.Equal(expected, f.Economy.SpendingPerMinute.Current, precision: 6);
    }

    /// <summary>A queue's factories are all busy once it has one active item, regardless of how many more are queued.</summary>
    [Fact]
    public void Utilization_OneActiveItem_MakesAllOfThatQueuesFactoriesBusy()
    {
        (BeliefModel belief, FeatureCompiler compiler) = New();
        IReadOnlyList<ProductionQueueState> queues = [new ProductionQueueState(QueueKind.Vehicle, [new QueueItem("tank", 0.3, false, false)], 2)];
        StrategicFeatures f = compiler.Compile(belief.Apply(Frame(0, 20000, [ConYardAtHome()], queues: queues)));

        Assert.Equal(1.0, f.Economy.ProductionUtilization, precision: 6);
    }

    /// <summary>A queue with no factories of its kind is paused (RA2): it spends nothing even with items queued.</summary>
    [Fact]
    public void Spending_QueueWithNoFactories_IsZero()
    {
        (BeliefModel belief, FeatureCompiler compiler) = New();
        IReadOnlyList<ProductionQueueState> queues = [new ProductionQueueState(QueueKind.Vehicle, [new QueueItem("tank", 0.3, false, false)], 0)];
        compiler.Compile(belief.Apply(Frame(0, 20000, [ConYardAtHome()], queues: queues)));
        StrategicFeatures f = compiler.Compile(belief.Apply(Frame(1, 20000, [ConYardAtHome()], queues: queues)));

        Assert.Equal(0, f.Economy.SpendingPerMinute.Current, precision: 6);
    }

    /// <summary>
    /// A finished building waiting for placement blocks its queue (RA2, and the simulator's AwaitingPlacement): the
    /// item behind it is not being built, even when a producer does not mark it on hold.
    /// </summary>
    [Fact]
    public void Spending_AndUtilization_AreZeroWhileAFinishedBuildingBlocksTheQueue()
    {
        (BeliefModel belief, FeatureCompiler compiler) = New();
        IReadOnlyList<ProductionQueueState> queues = [new ProductionQueueState(QueueKind.Vehicle, [new QueueItem("tank", 1.0, true, false), new QueueItem("tank", 0.0, false, false)], 1)];
        compiler.Compile(belief.Apply(Frame(0, 20000, [ConYardAtHome()], queues: queues)));
        StrategicFeatures f = compiler.Compile(belief.Apply(Frame(1, 20000, [ConYardAtHome()], queues: queues)));

        Assert.Equal(0, f.Economy.SpendingPerMinute.Current, precision: 6);
        Assert.Equal(0, f.Economy.ProductionUtilization, precision: 6);
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

    /// <summary>
    /// Growth from an empty army is our own production arriving, not a swing: a first 900-credit tank used to read
    /// as +90% (severity 0.9) and a first tank-and-escort as +100%, above the scheduler's major-event severity, so
    /// every opening and every rebuild woke the strategist and made its pending result late.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ArmyValueSwing_DoesNotFireWhenTheArmyGrowsFromNothing(int tanks)
    {
        (BeliefModel belief, FeatureCompiler compiler) = New();
        List<StrategicEvent> events = [];
        for (int t = 0; t <= 40; t++)
        {
            List<ObservedEntity> entities = [ConYardAtHome()];
            if (t >= 20) entities.AddRange(Enumerable.Range(0, tanks).Select(i => Own((uint)(3 + i), "tank", HomeCell)));
            events.AddRange(compiler.Compile(belief.Apply(Frame(t, 5000, entities))).Events);
        }
        Assert.DoesNotContain(events, static e => e.Kind == StrategicEventKind.ArmyValueSwing);
    }

    /// <summary>A sudden loss is still the swing the event exists for, at a severity proportional to it.</summary>
    [Fact]
    public void ArmyValueSwing_StillFiresWhenTheArmyIsWipedOut()
    {
        (BeliefModel belief, FeatureCompiler compiler) = New();
        List<StrategicEvent> events = [];
        for (int t = 0; t <= 40; t++)
        {
            List<ObservedEntity> entities = [ConYardAtHome()];
            if (t < 30) entities.AddRange(Enumerable.Range(0, 3).Select(i => Own((uint)(3 + i), "tank", HomeCell)));
            events.AddRange(compiler.Compile(belief.Apply(Frame(t, 5000, entities))).Events);
        }
        StrategicEvent swing = Assert.Single(events, static e => e.Kind == StrategicEventKind.ArmyValueSwing);
        Assert.Equal(30, swing.Time.SecondsSince(GameTime.FromSeconds(0)), precision: 6);
        Assert.Equal(1.0, swing.Severity, precision: 6);
        Assert.StartsWith("-", swing.Detail, StringComparison.Ordinal);
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

    /// <summary>An army at the far end of the map cannot defend the base; the base's own value is what is near it.</summary>
    [Fact]
    public void BaseThreat_ComparesTheEnemyWithOwnForcesThatCanRespond()
    {
        (BeliefModel belief, FeatureCompiler compiler) = New();
        List<ObservedEntity> entities = [ConYardAtHome()];
        entities.AddRange(Enumerable.Range(0, 40).Select(i => Own((uint)(100 + i), "rifleman", EnemyCell)));
        entities.AddRange(Enumerable.Range(0, 20).Select(i => Enemy((uint)(300 + i), "rifleman", new Cell(11, 11))));

        StrategicFeatures f = compiler.Compile(belief.Apply(Frame(0, 5000, entities, new HashSet<RegionId> { TestMaps.Home, TestMaps.EnemyStart })));

        ThreatAssessment home = Assert.Single(f.Threats, static t => t.IsBase);
        Assert.Equal(2000, home.EnemyValue);
        Assert.Equal(0, home.OwnValue);
        Assert.True(home.LocalForceRatio < 1.0);
        Assert.True(ConditionEvaluator.BaseThreatRatio(f) >= 1.0);
        Assert.Equal(4000, f.Army.ArmyValue.Current);
    }

    /// <summary>A tank on the river bank maps to the water region by nearest centre; it still threatens the base.</summary>
    [Fact]
    public void BaseThreat_CountsAnEnemyWhoseNearestRegionIsWater()
    {
        Region bank = new(new RegionId(0), "bank", new Cell(42, 50), 8, IsStartLocation: true, HasOre: false, Water: false);
        Region river = new(new RegionId(1), "river", new Cell(50, 50), 8, IsStartLocation: false, HasOre: false, Water: true);
        MapInfo map = new("shore", 100, 100, [bank, river], [new RegionLink(bank.Id, river.Id, 10, Ground: false, Naval: true)], []);
        (BeliefModel belief, FeatureCompiler compiler) = New();

        StrategicFeatures f = compiler.Compile(belief.Apply(Frame(0, 5000,
            [Own(1, "conyard", new Cell(40, 50)), Enemy(40, "tank", new Cell(47, 50))],
            new HashSet<RegionId> { bank.Id, river.Id }, map: map)));

        Assert.Equal(river.Id, Assert.Single(belief.Current.Enemies).LastSeenRegion);
        ThreatAssessment home = Assert.Single(f.Threats, static t => t.IsBase);
        Assert.Equal(900, home.EnemyValue);
        Assert.True(home.EnemyEtaSeconds < FeatureCompiler.UnknownSeconds);
    }
}
