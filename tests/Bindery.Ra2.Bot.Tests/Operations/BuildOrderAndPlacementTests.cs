// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Operations;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Operations;

/// <summary>
/// The building queue grows the economy past one refinery, adds the factory the army composition needs, and
/// placement never stacks two buildings on one cell, never retries a rejected cell forever, and puts refineries
/// by the ore the strategist wants taken.
/// </summary>
public sealed partial class BuildOrderAndPlacementTests
{
    private static readonly RegionId Mid = new(3);
    private static readonly RegionId FarOre = new(4);

    private static UnitRule Yard => Fixture.Building("cy", UnitRole.Production, cost: 0);
    private static UnitRule Power => Fixture.Building("power", UnitRole.Power, power: 100, cost: 800);
    private static UnitRule Refinery => Fixture.Building("ref", UnitRole.Economy, cost: 2000) with { Prerequisites = [["power"]] };
    private static UnitRule Barracks => Fixture.Building("pile", UnitRole.Production, cost: 500) with { Prerequisites = [["power"]] };
    private static UnitRule Factory => Fixture.Building("weap", UnitRole.Production, cost: 2000) with { Prerequisites = [["ref"]] };
    private static UnitRule Pillbox => Fixture.Building("pbox", UnitRole.Defense, cost: 500) with { Queue = QueueKind.Defense, Prerequisites = [["pile"]] };
    private static UnitRule Gi => Fixture.Combat("gi", UnitRole.AntiInfantry, QueueKind.Infantry, 200, 5) with { Prerequisites = [["pile"]] };
    private static UnitRule Tank => Fixture.Combat("tank", UnitRole.AntiArmor, QueueKind.Vehicle, 900, 10) with { Prerequisites = [["weap"]] };
    private static UnitRule Harvester => Fixture.Combat("harv", UnitRole.Harvester, QueueKind.Vehicle, 1400, 20) with { Prerequisites = [["weap"]] };

    private static OperationalPlanner Planner(OperationalOptions? options = null) =>
        new(new FakeRulesDatabase([Yard, Power, Refinery, Barracks, Factory, Pillbox, Gi, Tank, Harvester]), new FakePlaybookLibrary([]), options ?? new OperationalOptions());

    /// <summary>Three ore regions: Home, Mid and a far field.</summary>
    private static MapInfo OreMap() => new(
        "ore-map",
        100,
        100,
        [
            new Region(Fixture.Home, "Home", new Cell(10, 10), 8, true, true, false),
            new Region(Fixture.Front, "Front", new Cell(50, 50), 8, true, false, false),
            new Region(Mid, "Mid", new Cell(30, 30), 8, false, true, false),
            new Region(FarOre, "FarOre", new Cell(10, 40), 8, false, true, false),
        ],
        [new RegionLink(Fixture.Home, Mid, 28, true, false), new RegionLink(Mid, Fixture.Front, 28, true, false), new RegionLink(Fixture.Home, FarOre, 30, true, false)],
        [
            new OreField(Fixture.Home, new Cell(12, 6), 10000, false),
            new OreField(Mid, new Cell(30, 30), 10000, false),
            new OreField(FarOre, new Cell(10, 40), 10000, false),
        ]);

    private static OwnEntity Own(uint id, string type, UnitRole role, Cell cell) =>
        new(new EntityId(id), type, role, EntityKind.Building, cell, Fixture.Home, 1.0, 1000, false);

    private static BeliefSnapshot Base(double seconds, int credits, IReadOnlyList<ProductionQueueState> queues, params OwnEntity[] extra) =>
        Fixture.Belief(
            credits: credits,
            own: [Own(100, "cy", UnitRole.Production, new Cell(10, 10)), Own(101, "power", UnitRole.Power, new Cell(13, 10)),
                  Own(102, "ref", UnitRole.Economy, new Cell(13, 7)), Own(103, "weap", UnitRole.Production, new Cell(7, 10)), .. extra],
            queues: queues,
            time: GameTime.FromSeconds(seconds)) with { Map = OreMap() };

    private static StrategicFeatures Economy(BeliefSnapshot belief, int refineries, int harvesters)
    {
        StrategicFeatures features = Fixture.Features(belief);
        return features with { Economy = features.Economy with { Refineries = refineries, Harvesters = harvesters } };
    }

    private static readonly ProductionQueueState EmptyBuildingQueue = new(QueueKind.Building, [], Factories: 1);

    [Fact]
    public void A_second_refinery_is_built_once_the_economy_calls_for_it()
    {
        BeliefSnapshot belief = Base(600, 6000, [EmptyBuildingQueue], Own(104, "pile", UnitRole.Production, new Cell(7, 7)));
        StrategicIntent intent = Fixture.Intent(budget: new BudgetShares(0.5, 0.3, 0.1, 0.1));

        OperationalPlan plan = Planner().Plan(belief, Economy(belief, refineries: 1, harvesters: 3), intent, new FakeLeaseManager());

        Assert.Contains(plan.ProductionCommands.OfType<ProduceCommand>(), static c => c.TypeId == "ref");
    }

    [Fact]
    public void Extra_production_adds_the_factory_the_composition_needs_and_does_not_count_the_yard()
    {
        BeliefSnapshot belief = Base(0, 6000, [EmptyBuildingQueue], Own(104, "pile", UnitRole.Production, new Cell(7, 7)));
        StrategicIntent intent = Fixture.Intent(composition: [new CompositionTarget(UnitRole.AntiArmor, 0.6, 1.0)]);

        OperationalPlan plan = Planner(new OperationalOptions(MaxProductionBuildings: 3)).Plan(belief, Economy(belief, refineries: 1, harvesters: 3), intent, new FakeLeaseManager());

        ProduceCommand produce = Assert.Single(plan.ProductionCommands.OfType<ProduceCommand>(), static c => c.Queue == QueueKind.Building);
        Assert.Equal("weap", produce.TypeId);
    }
}
