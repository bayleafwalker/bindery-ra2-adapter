// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Arena;

/// <summary>
/// One evaluation arm as named on the CLI (<c>selector</c>, <c>bandit</c>,
/// <c>llm-shadow</c>, <c>llm</c>, <c>llm+fast</c>, <c>distilled</c>), or an
/// opponent style (<c>rush</c>, <c>turtle</c>, <c>tech</c>, <c>harass</c>,
/// <c>balanced</c>) when the factory builds the other side.
/// </summary>
/// <param name="Oracle">True for a <c>*-oracle</c> arm: observation frames carry full engine state.</param>
/// <param name="LlmFake">True when <c>--llm-fake</c> substitutes a deterministic client for the Anthropic API.</param>
public sealed record ArmSpec(string Name, bool Oracle, bool LlmFake)
{
    public override string ToString() => Oracle ? $"{Name}-oracle" : Name;

    public bool UsesLlm => Name is "llm" or "llm-shadow" or "llm+fast";
}

/// <summary>Per-agent counters the arena turns into the report's metrics; filled from the bot's metrics and decision log.</summary>
public sealed record ArenaAgentStats
{
    public int Proposals { get; set; }
    public int Rejected { get; set; }
    public int LateDiscarded { get; set; }

    /// <summary>Seconds from each proposal's snapshot to its validation (spec "decision lateness").</summary>
    public List<double> LateSeconds { get; } = [];

    public int Activations { get; set; }
    public int PostureFlips { get; set; }
    public int CommandsDropped { get; set; }
    public Dictionary<string, int> DroppedByReason { get; } = new(StringComparer.Ordinal);
    public int FogRejections { get; set; }
    public int ShadowProposals { get; set; }
    public int ProposalsFailed { get; set; }
    public long TokensIn { get; set; }
    public long TokensOut { get; set; }
    public string? Model { get; set; }
    public double Usd { get; set; }
    public string? DecisionLogHash { get; set; }

    /// <summary>Labels that qualify the results (<c>llm-fake</c>, <c>oracle</c>, <c>distilled-from:...</c>).</summary>
    public List<string> Labels { get; } = [];
}

/// <summary>One decision-making side of a match: builds and returns commands from observation frames.</summary>
public interface IArenaAgent : IDisposable
{
    IReadOnlyList<GameCommand> Tick(ObservationFrame frame);

    ArenaAgentStats Stats { get; }

    /// <summary>The agent's decision log (empty for agents without one).</summary>
    IReadOnlyList<DecisionRecord> DecisionLog { get; }

    /// <summary>Called once when the match ends, before <see cref="Stats"/> is read; <paramref name="won"/> is null on a draw.</summary>
    void Finish(bool? won, double ownAssetValue, double enemyAssetValue);
}

/// <summary>Builds the agent for one side of a match.</summary>
public interface IArenaAgentFactory
{
    IArenaAgent Create(ArmSpec arm, PlayerId player, Faction faction, MapInfo map, int seed);
}
