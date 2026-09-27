// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Sim.Opponents;

namespace Bindery.Ra2.Bot.Arena;

/// <summary>
/// Arena side driven by the independent <see cref="ScriptedSkirmishAi"/> (opponents <c>ai-rush</c>,
/// <c>ai-balanced</c>, <c>ai-turtle</c>, <c>ai-air</c>, optionally <c>:easy</c>/<c>:medium</c>/<c>:hard</c>). It has
/// no strategist and no decision log, so its stats stay empty apart from a style label.
/// </summary>
public sealed class ScriptedArenaAgent : IArenaAgent
{
    private readonly ScriptedSkirmishAi ai;

    public ScriptedArenaAgent(ScriptedSkirmishAi ai, string label)
    {
        ArgumentNullException.ThrowIfNull(ai);
        this.ai = ai;
        Stats.Labels.Add(label);
    }

    public ArenaAgentStats Stats { get; } = new();

    public IReadOnlyList<DecisionRecord> DecisionLog => [];

    public IReadOnlyList<GameCommand> Tick(ObservationFrame frame) => ai.Tick(frame);

    public void Finish(bool? won, double ownAssetValue, double enemyAssetValue)
    {
    }

    public void Dispose()
    {
    }
}
