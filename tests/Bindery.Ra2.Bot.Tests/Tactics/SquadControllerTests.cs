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

    [Fact]
    public void Tick_RetreatHysteresisDoesNotOscillateUnderAlternatingRatios()
    {
        SquadControllerOptions options = new(EngagementRangeCells: 20, MinStateSeconds: 5);
        SquadController controller = new(options);
        FakeRulesDatabase rules = new();
        FakeLeaseManager leases = new();

        OwnEntity a = Fixture.Unit(1, Fixture.Front, new Cell(50, 50), value: 800);
        OwnEntity b = Fixture.Unit(2, Fixture.Front, new Cell(51, 50), value: 800);
        leases.Grant(LeaseKey.Unit(a.Id), "squad:s1", new GameTime(0), 100);
        leases.Grant(LeaseKey.Unit(b.Id), "squad:s1", new GameTime(0), 100);
        SquadOrder order = new("s1", ObjectiveKind.AttackRegion, Fixture.Front, [a.Id, b.Id], Engage: true, RetreatBelowForceRatio: 0.6);

        List<Type> observed = [];
        for (int t = 0; t < 12; t++)
        {
            // Alternate an enemy value that swings the local force ratio (1600 own)
            // between well below (0.3) and well above (1.2) the 0.6/1.0 thresholds every tick.
            int enemyValue = t % 2 == 0 ? 5333 : 1333;
            EnemyContact enemy = new(new EntityId(20), new PlayerId(1), "enemy", UnitRole.AntiArmor, EntityKind.Vehicle,
                new Cell(50, 51), Fixture.Front, new GameTime(t * 15), 1.0, enemyValue, 0.9, false);
            BeliefSnapshot belief = Fixture.Belief(own: [a, b], enemies: [enemy], time: new GameTime(t * 15));

            IReadOnlyList<GameCommand> commands = controller.Tick(belief, [order], leases, rules);
            GameCommand command = Assert.Single(commands);
            observed.Add(command.GetType());
        }

        int transitions = 0;
        for (int i = 1; i < observed.Count; i++)
        {
            if (observed[i] != observed[i - 1]) transitions++;
        }

        // Alternating input every second would flip a naive per-tick check up to 11 times;
        // the 5 s minimum time in state must keep this far below that.
        Assert.True(transitions <= 3, $"expected hysteresis to suppress oscillation, saw {transitions} transitions: {string.Join(",", observed.Select(o => o.Name))}");
    }
}
