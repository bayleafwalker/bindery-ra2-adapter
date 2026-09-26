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
}
