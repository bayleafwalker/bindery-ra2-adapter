// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Sim;

/// <summary>
/// Per-player view of a <see cref="SkirmishSimulation"/> as an
/// <see cref="IObservationSource"/>. The caller drives the shared simulation
/// with <see cref="SkirmishSimulation.Step"/>; each player's source then
/// reads that frame's observation. It never advances the sim itself, so
/// several sources can share one simulation safely.
/// </summary>
public sealed class SimPlayerSource(SkirmishSimulation simulation, PlayerId player, ObservationMode mode = ObservationMode.Belief) : IObservationSource
{
    private readonly SkirmishSimulation simulation = simulation;
    private readonly PlayerId player = player;

    public ObservationMode Mode { get; } = mode;

    public ValueTask<ObservationFrame?> NextAsync(CancellationToken cancellationToken = default)
    {
        if (simulation.MatchEnded) return ValueTask.FromResult<ObservationFrame?>(null);
        return ValueTask.FromResult<ObservationFrame?>(simulation.Observe(player, Mode));
    }
}

/// <summary>Per-player command sink that submits into a shared <see cref="SkirmishSimulation"/>.</summary>
public sealed class SimCommandSink(SkirmishSimulation simulation, PlayerId player) : ICommandSink
{
    private readonly SkirmishSimulation simulation = simulation;
    private readonly PlayerId player = player;

    public void Submit(GameCommand command) => simulation.Submit(player, command);
}
