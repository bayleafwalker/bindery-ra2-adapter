// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Sim;

/// <summary>
/// Wraps a <see cref="SkirmishSimulation"/>, recording every frame's
/// submitted commands so a match can be replayed byte-for-byte. This is the
/// bindery region sim's determinism evidence: the same seed and the same
/// recorded commands must reach the same <see cref="StateHash"/>.
/// </summary>
public sealed class SimRecorder
{
    private readonly SimMap map;
    private readonly IRulesDatabase rules;
    private SimSettings settings;
    private readonly List<List<(PlayerId Player, GameCommand Command)>> frames = [];
    private List<(PlayerId Player, GameCommand Command)> currentFrame = [];

    public SimRecorder(SimMap map, IRulesDatabase rules, SimSettings settings)
    {
        this.map = map;
        this.rules = rules;
        this.settings = settings;
        Simulation = new SkirmishSimulation(map, rules, settings);
    }

    public SkirmishSimulation Simulation { get; private set; }

    /// <summary>Number of frames recorded so far (equal to the number of completed <see cref="Step"/> calls).</summary>
    public int FrameCount => frames.Count;

    public string StateHash => Simulation.ComputeStateHash();

    /// <summary>Submits a command for the current, not-yet-stepped frame and forwards it to the live simulation.</summary>
    public void Submit(PlayerId player, GameCommand command)
    {
        Simulation.Submit(player, command);
        currentFrame.Add((player, command));
    }

    /// <summary>Records the current frame's commands, then advances the live simulation by one frame.</summary>
    public void Step()
    {
        frames.Add(currentFrame);
        currentFrame = [];
        Simulation.Step();
    }

    /// <summary>
    /// Rebuilds the simulation from scratch with <paramref name="seed"/> and
    /// replays every recorded frame's commands in original order. Recorded
    /// history is kept, so calling this twice with the same seed reproduces
    /// the same <see cref="StateHash"/> both times.
    /// </summary>
    public void Reset(int seed)
    {
        settings = settings with { Seed = seed };
        Simulation = new SkirmishSimulation(map, rules, settings);
        foreach (List<(PlayerId Player, GameCommand Command)> frame in frames)
        {
            foreach ((PlayerId player, GameCommand command) in frame) Simulation.Submit(player, command);
            Simulation.Step();
        }
    }
}
