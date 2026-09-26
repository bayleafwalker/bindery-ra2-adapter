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
    /// <summary>An authored play style (<see cref="Bindery.Ra2.Bot.Strategy.Personalities"/>) the arm's strategists play, or null.</summary>
    public string? Personality { get; init; }

    public override string ToString() => (Oracle ? $"{Name}-oracle" : Name) + (Personality is null ? string.Empty : $"@{Personality}");

    /// <summary>Arms that call the LLM: the LLM arms, and <c>distilled</c>, which escalates unusual states to it.</summary>
    public bool UsesLlm => Name is "llm" or "llm-shadow" or "llm+fast" or "distilled" || BotAgentFactory.TierArms.ContainsKey(Name);
}

/// <summary>Per-agent counters the arena turns into the report's metrics; filled from the bot's metrics and decision log.</summary>
public sealed record ArenaAgentStats
{
    /// <summary>Proposals of the arm's primary strategist (fallback and emergency proposals are in <see cref="FallbackProposals"/>).</summary>
    public int Proposals { get; set; }

    /// <summary>Primary proposals the validator rejected as invalid (stale ones count as late instead).</summary>
    public int Rejected { get; set; }

    /// <summary>Primary proposals discarded as late.</summary>
    public int LateDiscarded { get; set; }

    /// <summary>Seconds from each primary proposal's snapshot to its validation (spec "decision lateness").</summary>
    public List<double> LateSeconds { get; } = [];

    /// <summary>Proposals of the selector fallback and the emergency path, which answer at once and are kept out of the arm's figures.</summary>
    public int FallbackProposals { get; set; }

    /// <summary>Seconds from each shadow proposal's snapshot to its arrival.</summary>
    public List<double> ShadowLateSeconds { get; } = [];

    /// <summary>Shadow proposals the validator would have rejected, and their <c>fog.*</c> rejections.</summary>
    public int ShadowRejected { get; set; }

    public int ShadowFogRejections { get; set; }

    /// <summary>Part of <see cref="Usd"/> billed by requests that failed (refusal, truncation, unparseable or unmappable reply).</summary>
    public double FailedRequestUsd { get; set; }

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

    /// <summary>Seconds each playbook and each posture was the active intent's (from the decision log).</summary>
    public Dictionary<string, double> PlaybookSeconds { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, double> PostureSeconds { get; } = new(StringComparer.Ordinal);

    /// <summary>Shadow proposals compared with the primary's answer to the same request, and how many named the same playbook.</summary>
    public int ShadowCompared { get; set; }

    public int ShadowAgreed { get; set; }

    /// <summary>Primary requests the distilled strategist answered (its own model or by escalation).</summary>
    public int DistilledDecisions { get; set; }

    /// <summary>Of those, requests escalated to the inner (LLM) strategist.</summary>
    public int DistilledEscalations { get; set; }

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
