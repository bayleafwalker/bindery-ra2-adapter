// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Sim;

/// <summary>One player slot for a <see cref="SkirmishSimulation"/> match.</summary>
public sealed record SimPlayer(PlayerId Id, Faction Faction);

/// <summary>
/// Match configuration for the bindery region sim, not retail RA2. Two
/// players is the tested and authored configuration; more are accepted as
/// long as the map has enough start regions.
/// </summary>
/// <param name="Seed">Seeds the simulation's <see cref="Xorshift"/> RNG; identical seed and commands replay identically.</param>
/// <param name="MaxSeconds">Match is called a timeout and scored by asset value if no player has won by this wall time.</param>
/// <param name="StartingCredits">Credits every player begins with.</param>
/// <param name="Players">Player slots, matched to the map's start regions in order.</param>
public sealed record SimSettings(int Seed, double MaxSeconds, IReadOnlyList<SimPlayer> Players, int StartingCredits = 10_000);
