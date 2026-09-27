// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Sim;

namespace Bindery.Ra2.Bot.Tests.Sim;

internal static class SimTestHelpers
{
    public static SimSettings TwoPlayers(int seed, double maxSeconds = 600, int startingCredits = 10_000) => new(
        seed, maxSeconds,
        [new SimPlayer(new PlayerId(0), Faction.Allied), new SimPlayer(new PlayerId(1), Faction.Soviet)],
        startingCredits);

    public static void Advance(this SkirmishSimulation sim, double seconds)
    {
        int frames = (int)Math.Round(seconds * GameTime.FramesPerSecond);
        for (int i = 0; i < frames && !sim.MatchEnded; i++) sim.Step();
    }

    /// <summary>Deploys the player's single starting MCV and steps once so the resulting building exists.</summary>
    public static void DeployStartingMcv(SkirmishSimulation sim, PlayerId player)
    {
        ObservationFrame frame = sim.Observe(player, ObservationMode.Oracle);
        ObservedEntity mcv = frame.Entities.Single(e => e.Owner == player && e.TypeId == TestRules.Mcv);
        sim.Submit(player, new DeployCommand("test", mcv.Id));
        sim.Step();
    }

    /// <summary>Produces and places a building of <paramref name="typeId"/> next to the player's construction yard.</summary>
    public static void BuildBuilding(SkirmishSimulation sim, TestRules rules, PlayerId player, string typeId, double buildSecondsBudget = 30)
    {
        sim.Submit(player, new ProduceCommand("test", typeId, rules.Get(typeId).Queue));
        sim.Step();
        sim.Advance(buildSecondsBudget);
        ObservationFrame frame = sim.Observe(player, ObservationMode.Oracle);
        ObservedEntity yard = frame.Entities.Single(e => e.Owner == player && e.TypeId == TestRules.ConYard);
        // The first cell east of the yard clear of every building: the sim refuses overlapping footprints.
        List<Cell> buildings = [.. frame.Entities.Where(e => rules.TryGet(e.TypeId, out UnitRule r) && r.Kind == EntityKind.Building).Select(static e => e.Position)];
        Cell cell = Enumerable.Range(1, 10).Select(i => new Cell(yard.Position.X + (2 * i), yard.Position.Y))
            .First(c => buildings.All(b => b.DistanceTo(c) >= 2));
        sim.Submit(player, new PlaceBuildingCommand("test", typeId, cell));
        sim.Step();
    }
}
