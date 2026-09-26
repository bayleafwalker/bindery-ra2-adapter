// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Tactics;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Tactics;

public sealed class SquadControllerTests
{
    [Fact]
    public void Tick_DoesNotCommandUnitsItHasNoLeaseFor()
    {
        SquadController controller = new(new SquadControllerOptions());
        FakeRulesDatabase rules = new();
        FakeLeaseManager leases = new(); // no leases granted at all

        OwnEntity tank = Fixture.Unit(1, Fixture.Home, new Cell(10, 10));
        BeliefSnapshot belief = Fixture.Belief(own: [tank]);
        SquadOrder order = new("s1", ObjectiveKind.AttackRegion, Fixture.Front, [tank.Id], Engage: true, RetreatBelowForceRatio: 0.6);

        IReadOnlyList<GameCommand> commands = controller.Tick(belief, [order], leases, rules);

        Assert.Empty(commands);
    }

    [Fact]
    public void Tick_FocusFiresExactlyOneBestTarget()
    {
        SquadController controller = new(new SquadControllerOptions());
        FakeRulesDatabase rules = new(new Dictionary<(string, string), double>
        {
            [("tank", "weak")] = 0.2,
            [("tank", "juicy")] = 3.0,
        });
        FakeLeaseManager leases = new();

        OwnEntity tank = Fixture.Unit(1, Fixture.Front, new Cell(50, 50));
        leases.Grant(LeaseKey.Unit(tank.Id), "squad:s1", new GameTime(0), 10);

        EnemyContact weak = new(new EntityId(10), new PlayerId(1), "weak", UnitRole.AntiArmor, EntityKind.Vehicle,
            new Cell(51, 50), Fixture.Front, new GameTime(0), 0.9, 800, 0.9, false);
        EnemyContact juicy = new(new EntityId(11), new PlayerId(1), "juicy", UnitRole.AntiArmor, EntityKind.Vehicle,
            new Cell(52, 50), Fixture.Front, new GameTime(0), 0.5, 800, 0.9, false);

        BeliefSnapshot belief = Fixture.Belief(own: [tank], enemies: [weak, juicy]);
        SquadOrder order = new("s1", ObjectiveKind.AttackRegion, Fixture.Front, [tank.Id], Engage: true, RetreatBelowForceRatio: 0.0);

        IReadOnlyList<GameCommand> commands = controller.Tick(belief, [order], leases, rules);

        AttackCommand attack = Assert.IsType<AttackCommand>(Assert.Single(commands));
        Assert.Equal(juicy.Id, attack.Target); // higher effectiveness * (1 - health) * value
        Assert.Equal([tank.Id], attack.Units);
    }

    // Own squad value is 1600 (two 800 tanks); these enemy values give the local force ratios named.
    private const int RatioPoint3 = 5333;   // 0.30: below the 0.6 retreat threshold
    private const int RatioPoint8 = 2000;   // 0.80: inside the 0.6/1.0 hysteresis band
    private const int Ratio1Point01 = 1584; // 1.01: just above the 1.0 re-engage threshold
    private const int Ratio1Point2 = 1333;  // 1.20: well above the re-engage threshold

    [Fact]
    public void Tick_RetreatHysteresisDoesNotOscillateUnderAlternatingRatios()
    {
        // Ratio flips across both thresholds every second after t=0. A naive per-tick
        // check would flip 16 times; the 5 s dwell must allow exactly one flip per 5 s.
        int[] enemyValues = Enumerable.Range(0, 17)
            .Select(static t => t == 0 || t % 2 == 0 ? Ratio1Point2 : RatioPoint3)
            .ToArray();

        List<(int Time, Type Command)> observed = RunSquad(enemyValues);

        List<int> transitions = Transitions(observed);
        Assert.Equal([5, 10, 15], transitions);
        Assert.Equal(typeof(AttackCommand), observed[0].Command);
        Assert.Equal(typeof(MoveCommand), observed[5].Command);
        Assert.Equal(typeof(AttackCommand), observed[10].Command);
        Assert.Equal(typeof(MoveCommand), observed[15].Command);
    }

    [Fact]
    public void Tick_RatioInsideHysteresisBandHoldsCurrentState()
    {
        // 0-9 s in band while engaged: stays engaged. 10 s: ratio 0.3 retreats.
        // 11-29 s in band while retreating: stays retreating. 30 s: ratio 1.01 re-engages.
        int[] enemyValues = Enumerable.Range(0, 31)
            .Select(static t => t switch
            {
                < 10 => RatioPoint8,
                10 => RatioPoint3,
                < 30 => RatioPoint8,
                _ => Ratio1Point01,
            })
            .ToArray();

        List<(int Time, Type Command)> observed = RunSquad(enemyValues);

        Assert.Equal([10, 30], Transitions(observed));
        Assert.All(observed.Take(10), static o => Assert.Equal(typeof(AttackCommand), o.Command));
        Assert.All(observed.Skip(10).Take(20), static o => Assert.Equal(typeof(MoveCommand), o.Command));
        Assert.Equal(typeof(AttackCommand), observed[30].Command);
    }

    /// <summary>
    /// Ticks a fresh two-tank squad once per second against one armed enemy whose
    /// value at second t is <paramref name="enemyValueAtSecond"/>[t].
    /// </summary>
    private static List<(int Time, Type Command)> RunSquad(int[] enemyValueAtSecond)
    {
        SquadController controller = new(new SquadControllerOptions(EngagementRangeCells: 20, MinStateSeconds: 5));
        FakeRulesDatabase rules = new(armedTypes: ["enemy"]);
        FakeLeaseManager leases = new();

        OwnEntity a = Fixture.Unit(1, Fixture.Front, new Cell(50, 50), value: 800);
        OwnEntity b = Fixture.Unit(2, Fixture.Front, new Cell(51, 50), value: 800);
        leases.Grant(LeaseKey.Unit(a.Id), "squad:s1", new GameTime(0), 1000);
        leases.Grant(LeaseKey.Unit(b.Id), "squad:s1", new GameTime(0), 1000);
        SquadOrder order = new("s1", ObjectiveKind.AttackRegion, Fixture.Front, [a.Id, b.Id], Engage: true, RetreatBelowForceRatio: 0.6);

        List<(int, Type)> observed = [];
        for (int t = 0; t < enemyValueAtSecond.Length; t++)
        {
            EnemyContact enemy = new(new EntityId(20), new PlayerId(1), "enemy", UnitRole.AntiArmor, EntityKind.Vehicle,
                new Cell(50, 51), Fixture.Front, GameTime.FromSeconds(t), 1.0, enemyValueAtSecond[t], 0.9, false);
            BeliefSnapshot belief = Fixture.Belief(own: [a, b], enemies: [enemy], time: GameTime.FromSeconds(t));
            GameCommand command = Assert.Single(controller.Tick(belief, [order], leases, rules));
            observed.Add((t, command.GetType()));
        }
        return observed;
    }

    private static List<int> Transitions(List<(int Time, Type Command)> observed) =>
        Enumerable.Range(1, observed.Count - 1)
            .Where(i => observed[i].Command != observed[i - 1].Command)
            .Select(i => observed[i].Time)
            .ToList();
}
