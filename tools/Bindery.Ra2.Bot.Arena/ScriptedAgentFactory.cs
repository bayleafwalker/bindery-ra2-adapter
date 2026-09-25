// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Arena;

/// <summary>
/// Builds <see cref="ScriptedAgent"/> instances for both the arm under test
/// and its opponent. An <see cref="ArmSpec"/> whose name matches one of the
/// five opponent styles gets that style; every other name — including every
/// real strategist arm, none of which exist yet — falls back to
/// <see cref="OpponentStyle.Placeholder"/>. Swap this factory out once a
/// <c>BotRuntime</c>-backed one exists for the strategist arms.
/// </summary>
public sealed class ScriptedAgentFactory(ArenaRuleset rules) : IArenaAgentFactory
{
    public IArenaAgent Create(ArmSpec arm, PlayerId player, Faction faction, MapInfo map, int seed) =>
        new ScriptedAgent(ParseStyle(arm.Name), rules, player);

    private static OpponentStyle ParseStyle(string name) => name.ToLowerInvariant() switch
    {
        "rush" => OpponentStyle.Rush,
        "turtle" => OpponentStyle.Turtle,
        "tech" => OpponentStyle.Tech,
        "harass" => OpponentStyle.Harass,
        "balanced" => OpponentStyle.Balanced,
        _ => OpponentStyle.Placeholder,
    };
}
