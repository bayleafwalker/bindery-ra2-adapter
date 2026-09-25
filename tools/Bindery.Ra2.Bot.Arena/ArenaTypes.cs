// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Arena;

/// <summary>
/// One evaluation arm as named on the CLI (`selector`, `bandit`, `llm`, ...).
/// The arena only ever asks an <see cref="IArenaAgentFactory"/> to build an
/// agent for it; until <c>BotRuntime</c> (package C) exists, every arm name
/// resolves to the same placeholder agent, and results are labelled with the
/// requested name regardless so downstream tooling does not have to change.
/// </summary>
/// <param name="Oracle">True for a <c>*-oracle</c> arm: observation frames carry full engine state.</param>
/// <param name="LlmFake">True when <c>--llm-fake</c> substitutes a scripted client for arms that would otherwise need <c>ANTHROPIC_API_KEY</c>.</param>
public sealed record ArmSpec(string Name, bool Oracle, bool LlmFake)
{
    public override string ToString() => Oracle ? $"{Name}-oracle" : Name;
}

/// <summary>
/// Per-agent counters the arena turns into the report's metrics. An agent
/// that never proposes strategy (the placeholder) reports zeros for the
/// strategy-layer fields; they exist on every agent so the arena's metric
/// computation does not need to special-case which layers are wired up yet.
/// </summary>
public sealed record ArenaAgentStats
{
    public int Proposals { get; set; }
    public int Rejected { get; set; }
    public List<double> LateSeconds { get; } = [];
    public int Activations { get; set; }
    public int PostureFlips { get; set; }
    public int CommandsDropped { get; set; }
    public long TokensIn { get; set; }
    public long TokensOut { get; set; }
    public string? Model { get; set; }
    public double Usd { get; set; }
}

/// <summary>One decision-making side of a match: builds and returns commands from observation frames.</summary>
public interface IArenaAgent
{
    IReadOnlyList<GameCommand> Tick(ObservationFrame frame);

    ArenaAgentStats Stats { get; }
}

/// <summary>
/// Builds the agent for one side of a match. The real implementation (bound
/// to <c>BotRuntime</c>) lives with package C/E/F's integration; this
/// interface is the seam so the arena can be built, run and tested without it.
/// </summary>
public interface IArenaAgentFactory
{
    IArenaAgent Create(ArmSpec arm, PlayerId player, Faction faction, MapInfo map, int seed);
}
