// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bindery.Ra2.Bot.Sim;
using Bindery.Ra2.Bot.Strategy;

namespace Bindery.Ra2.Bot.Arena;

/// <param name="StatesMatchedBeforePerturbation">The two lockstep simulations had identical state hashes when the probe perturbed one of them (a determinism check; the comparison means nothing otherwise).</param>
/// <param name="FramesCompared">Frames after the perturbation on which the arm's strategist context was compared.</param>
/// <param name="Differences">Frames on which the context hash differed while everything the arm could see was still identical (must be 0).</param>
/// <param name="ComparedSeconds">How long the comparison ran before the window closed or the arm legitimately saw a difference.</param>
/// <param name="FogViolations">Arm frames, over the whole run of both simulations, that carried an enemy object or event from a region the arm did not see (<see cref="SimLeakageProbe.FogViolations"/>; must be 0). The differential comparison only finds leaks its perturbation exercises; this per-frame check finds any.</param>
public sealed record LeakageProbeResult(string Arm, string Map, int Seed, double PerturbedAtSeconds, bool StatesMatchedBeforePerturbation, int FramesCompared, int Differences, string? Note = null, double ComparedSeconds = 0, int FogViolations = 0);

/// <summary>
/// The spec's hidden-information probe, run per arm on the real bot: two simulations run in
/// lockstep from the same seed with identical (deterministic) bots; at
/// <c>perturbAtSeconds</c> one of them has its hidden state perturbed by
/// <see cref="SimLeakageProbe.PerturbHidden"/> (enemy credits, enemy queue, wounded hidden
/// enemies, an enemy unit in a region the arm cannot see and one just across a border inside
/// its weapon reach). The arm's <see cref="StrategistContextHash"/> is then compared on every
/// frame for up to <c>compareSeconds</c>, and the comparison stops early only when the objects
/// the arm can see differ between the two simulations: from then on a difference can be
/// legitimate (the perturbed enemy acted differently and was seen). A leak through combat or
/// events takes seconds to show, so the window is long, and a context difference while the
/// visible objects still agree is a leak.
/// </summary>
public static class LeakageProbe
{
    public static LeakageProbeResult Run(ArmSpec arm, SimMap map, int seed, IRulesDatabase rules, IArenaAgentFactory factory, double perturbAtSeconds = 90, double compareSeconds = 60, string opponent = "balanced")
    {
        if (arm.Oracle)
        {
            return new LeakageProbeResult(arm.ToString(), map.Map.MapId, seed, perturbAtSeconds, false, 0, 0, "not applicable: oracle frames carry hidden state by design");
        }
        Faction armFaction = MatchRunner.ArmFaction(seed);
        Faction opponentFaction = armFaction == Faction.Allied ? Faction.Soviet : Faction.Allied;
        SimSettings settings = new(seed, perturbAtSeconds + compareSeconds + 10,
            [new SimPlayer(MatchRunner.ArmPlayer, armFaction), new SimPlayer(MatchRunner.OpponentPlayer, opponentFaction)]);
        map = MatchRunner.Oriented(map, seed);
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
            string? note = null;
            long end = perturbFrame + GameTime.FromSeconds(compareSeconds).Frame;
            while (a.Sim.Time.Frame < end && !a.Sim.MatchEnded && !b.Sim.MatchEnded)
            {
                if (VisibleObjects(a.Sim) != VisibleObjects(b.Sim))
                {
                    note = string.Create(CultureInfo.InvariantCulture, $"stopped at {a.Sim.Time.Seconds:0} s: the arm saw a legitimate difference");
                    break;
                }
                a.Frame();
                b.Frame();
                StrategistContext? ca = a.Arm.Runtime.CurrentStrategistContext, cb = b.Arm.Runtime.CurrentStrategistContext;
                if (ca is null || cb is null) continue;
                compared++;
                if (StrategistContextHash.Compute(ca) != StrategistContextHash.Compute(cb)) differences++;
            }
            double seconds = (a.Sim.Time.Frame - perturbFrame) / (double)GameTime.FramesPerSecond;
            return new LeakageProbeResult(arm.ToString(), map.Map.MapId, seed, perturbAtSeconds, matched, compared, differences, note, seconds, a.FogViolations + b.FogViolations);
        }
        finally
        {
            a.Dispose();
            b.Dispose();
        }
    }

    /// <summary>The objects the arm sees (own and visible enemy, with health and position), as one comparable string.</summary>
    private static string VisibleObjects(SkirmishSimulation sim) =>
        string.Join(';', sim.Observe(MatchRunner.ArmPlayer, ObservationMode.Belief).Entities.Select(static e => e.ToString()));

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

        /// <summary>Arm frames so far that carried a hidden enemy object or event.</summary>
        public int FogViolations { get; private set; }

        public void Frame()
        {
            ObservationFrame armFrame = Sim.Observe(MatchRunner.ArmPlayer, ObservationMode.Belief);
            ObservationFrame opponentFrame = Sim.Observe(MatchRunner.OpponentPlayer, ObservationMode.Belief);
            if (SimLeakageProbe.FogViolations(armFrame).Count > 0) FogViolations++;
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
