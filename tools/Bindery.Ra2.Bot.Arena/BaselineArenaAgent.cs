// SPDX-License-Identifier: GPL-3.0-or-later
using Baseline = Bindery.Ra2.Bot.Baseline;

namespace Bindery.Ra2.Bot.Arena;

/// <summary>
/// A pinned-playbook opponent running on the frozen baseline stack (<c>Bindery.Ra2.Bot.Baseline</c>, a copy of the
/// bot's layers as of commit 7f3e2c7), so the pinned styles stay a stationary benchmark while the live planner,
/// tactics and selector change. Only the counters the report shows for the opponent side are filled.
/// </summary>
public sealed class BaselineArenaAgent : IArenaAgent
{
    private readonly Baseline.Runtime.BotRuntime runtime;
    private readonly Baseline.Runtime.DecisionLog log;

    public BaselineArenaAgent(Baseline.Runtime.BotRuntime runtime, Baseline.Runtime.DecisionLog log, string label)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(log);
        this.runtime = runtime;
        this.log = log;
        Stats.Labels.Add(label);
    }

    public ArenaAgentStats Stats { get; } = new();

    public IReadOnlyList<DecisionRecord> DecisionLog => log.Records;

    public IReadOnlyList<GameCommand> Tick(ObservationFrame frame) => runtime.Tick(frame);

    public void Finish(bool? won, double ownAssetValue, double enemyAssetValue)
    {
        Stats.Proposals = (int)runtime.Metrics.Proposals;
        Stats.Rejected = (int)runtime.Metrics.Rejected;
        Stats.Activations = (int)runtime.Metrics.Activations;
        Stats.PostureFlips = (int)runtime.Metrics.PostureFlips;
        Stats.CommandsDropped = (int)runtime.Metrics.CommandsDropped;
    }

    public void Dispose() => runtime.Dispose();
}
