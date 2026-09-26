// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Tactics;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Tactics;

/// <summary>Harvesters flee only from enemies that can hurt them.</summary>
public sealed class HarvesterSafetyTests
{
    [Fact]
    public void Harvesters_keep_harvesting_next_to_unarmed_enemy_vehicles()
    {
        HarvesterSafetyController controller = new(new HarvesterSafetyOptions());
        FakeRulesDatabase rules = new(unarmedTypes: ["enemyminer"]);
        OwnEntity refinery = new(new EntityId(100), "ref", UnitRole.Economy, EntityKind.Building, new Cell(30, 30), Fixture.Home, 1.0, 2000, false);
        OwnEntity harvester = new(new EntityId(1), "harv", UnitRole.Harvester, EntityKind.Vehicle, new Cell(12, 8), Fixture.Home, 1.0, 1400, false);
        EnemyContact miner = new(new EntityId(20), new PlayerId(1), "enemyminer", UnitRole.Harvester, EntityKind.Vehicle,
            new Cell(14, 9), Fixture.Home, new GameTime(0), 1.0, 1400, 1.0, false);

        GameCommand command = Assert.Single(controller.Tick(Fixture.Belief(own: [refinery, harvester], enemies: [miner]), [], new FakeLeaseManager(), rules));

        Assert.IsType<HarvestCommand>(command);
    }

    [Fact]
    public void Harvesters_still_flee_armed_enemies()
    {
        HarvesterSafetyController controller = new(new HarvesterSafetyOptions());
        FakeRulesDatabase rules = new(armedTypes: ["enemy"]);
        OwnEntity refinery = new(new EntityId(100), "ref", UnitRole.Economy, EntityKind.Building, new Cell(30, 30), Fixture.Home, 1.0, 2000, false);
        OwnEntity harvester = new(new EntityId(1), "harv", UnitRole.Harvester, EntityKind.Vehicle, new Cell(12, 8), Fixture.Home, 1.0, 1400, false);
        EnemyContact tank = Fixture.Enemy(20, new Cell(14, 9), Fixture.Home, new GameTime(0));

        GameCommand command = Assert.Single(controller.Tick(Fixture.Belief(own: [refinery, harvester], enemies: [tank]), [], new FakeLeaseManager(), rules));

        Assert.IsType<MoveCommand>(command);
    }
}
