// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Sim;
using Bindery.Ra2.Bot.Strategy;

namespace Bindery.Ra2.Bot.Arena;

/// <param name="StatesMatchedBeforePerturbation">The two lockstep simulations had identical state hashes when the probe perturbed one of them (a determinism check; the comparison means nothing otherwise).</param>
/// <param name="FramesCompared">Frames after the perturbation on which the arm's strategist context was compared.</param>
/// <param name="Differences">Frames on which the context hash differed (must be 0).</param>
public sealed record LeakageProbeResult(string Arm, string Map, int Seed, double PerturbedAtSeconds, bool StatesMatchedBeforePerturbation, int FramesCompared, int Differences, string? Note = null);

/// <summary>
/// The spec's hidden-information probe, run per arm on the real bot: two simulations run in
/// lockstep from the same seed with identical (deterministic) bots; at
/// <c>perturbAtSeconds</c> one of them has its hidden state perturbed by
/// <see cref="SimLeakageProbe.PerturbHidden"/> (enemy credits, enemy queue, an enemy unit in a
/// region the arm cannot see). For the next <c>compareSeconds</c> the arm's
/// <see cref="StrategistContextHash"/> must be identical in both. Differences after that window
/// could be legitimate (the perturbed enemy acts differently and is eventually seen), so the
/// window is short.
/// </summary>
public static class LeakageProbe
{
    public static LeakageProbeResult Run(ArmSpec arm, SimMap map, int seed, IRulesDatabase rules, IArenaAgentFactory factory, double perturbAtSeconds = 90, double compareSeconds = 1, string opponent = "balanced")
    {
        if (arm.Oracle)
        {
            return new LeakageProbeResult(arm.ToString(), map.Map.MapId, seed, perturbAtSeconds, false, 0, 0, "not applicable: oracle frames carry hidden state by design");
        }
        Faction armFaction = MatchRunner.ArmFaction(seed);
        Faction opponentFaction = armFaction == Faction.Allied ? Faction.Soviet : Faction.Allied;
        SimSettings settings = new(seed, perturbAtSeconds + compareSeconds + 10,
            [new SimPlayer(MatchRunner.ArmPlayer, armFaction), new SimPlayer(MatchRunner.OpponentPlayer, opponentFaction)]);
        Side a = new(new SkirmishSimulation(map, rules, settings), factory, arm, opponent, armFaction, opponentFaction, seed);
        Side b = new(new SkirmishSimulation(map, rules, settings), factory, arm, opponent, armFaction, opponentFaction, seed);
        try
        {
            long perturbFrame = GameTime.FromSeconds(perturbAtSeconds).Frame;
            while (a.Sim.Time.Frame < perturbFrame && !a.Sim.MatchEnded && !b.Sim.MatchEnded)
            {
                a.Frame();
                b.Frame();
            }
            bool matched = a.Sim.ComputeStateHash() == b.Sim.ComputeStateHash();
            SimLeakageProbe.PerturbHidden(b.Sim, MatchRunner.ArmPlayer);

            int compared = 0, differences = 0;
            long end = perturbFrame + GameTime.FromSeconds(compareSeconds).Frame;
            while (a.Sim.Time.Frame < end && !a.Sim.MatchEnded && !b.Sim.MatchEnded)
            {
                a.Frame();
                b.Frame();
                StrategistContext? ca = a.Arm.Runtime.CurrentStrategistContext, cb = b.Arm.Runtime.CurrentStrategistContext;
                if (ca is null || cb is null) continue;
                compared++;
                if (StrategistContextHash.Compute(ca) != StrategistContextHash.Compute(cb)) differences++;
            }
            return new LeakageProbeResult(arm.ToString(), map.Map.MapId, seed, perturbAtSeconds, matched, compared, differences);
        }
        finally
        {
            a.Dispose();
            b.Dispose();
        }
    }

    private sealed class Side : IDisposable
    {
        public Side(SkirmishSimulation sim, IArenaAgentFactory factory, ArmSpec arm, string opponent, Faction armFaction, Faction opponentFaction, int seed)
        {
            Sim = sim;
            Arm = (BotArenaAgent)factory.Create(arm, MatchRunner.ArmPlayer, armFaction, sim.Map, seed);
            Opponent = factory.Create(new ArmSpec(opponent, false, false), MatchRunner.OpponentPlayer, opponentFaction, sim.Map, seed);
        }

        public SkirmishSimulation Sim { get; }

        public BotArenaAgent Arm { get; }

        public IArenaAgent Opponent { get; }

        public void Frame()
        {
            ObservationFrame armFrame = Sim.Observe(MatchRunner.ArmPlayer, ObservationMode.Belief);
            ObservationFrame opponentFrame = Sim.Observe(MatchRunner.OpponentPlayer, ObservationMode.Belief);
            foreach (GameCommand c in Arm.Tick(armFrame)) Sim.Submit(MatchRunner.ArmPlayer, c);
            foreach (GameCommand c in Opponent.Tick(opponentFrame)) Sim.Submit(MatchRunner.OpponentPlayer, c);
            Sim.Step();
        }

        public void Dispose()
        {
            Arm.Dispose();
            Opponent.Dispose();
        }
    }
}
