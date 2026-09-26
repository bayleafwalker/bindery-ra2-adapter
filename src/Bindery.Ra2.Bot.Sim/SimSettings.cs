// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Sim;

/// <summary>One player slot for a <see cref="SkirmishSimulation"/> match.</summary>
/// <param name="IncomeMultiplier">Multiplier on every harvester load this player banks: a handicap (or a retail-style AI bonus) for benchmarks.</param>
/// <param name="StartingCredits">This player's starting credits; null uses <see cref="SimSettings.StartingCredits"/>.</param>
public sealed record SimPlayer(PlayerId Id, Faction Faction, double IncomeMultiplier = 1.0, int? StartingCredits = null);

/// <summary>
/// Match configuration for the bindery region sim, not retail RA2. Two
/// players is the tested and authored configuration; more are accepted as
/// long as the map has enough start regions.
/// </summary>
/// <param name="Seed">Seeds the simulation's <see cref="Xorshift"/> RNG; identical seed and commands replay identically.</param>
/// <param name="MaxSeconds">Match is called a timeout and scored by asset value if no player has won by this wall time.</param>
/// <param name="StartingCredits">Credits every player begins with.</param>
/// <param name="Players">Player slots, matched to the map's start regions in order.</param>
/// <param name="SuperweaponChargeSeconds">Recharge time of every superweapon (RA2's nuclear missile and weather storm: 10 minutes); the charge pauses on low power.</param>
/// <param name="SuperweaponDamage">Damage dealt to every object within <paramref name="SuperweaponRadiusCells"/> of the strike.</param>
/// <param name="SuperweaponRadiusCells">Strike radius in cells.</param>
/// <param name="CombatNoise">
/// Seeded per-shot damage noise: each second's damage from one attacker is scaled by a uniform factor in
/// [1 − noise, 1 + noise]. Zero (the default) keeps combat exactly deterministic from the rules; a positive value
/// makes outcomes depend on the seed, so a benchmark can separate strategies that a noiseless one cannot.
/// </param>
public sealed record SimSettings(
    int Seed,
    double MaxSeconds,
    IReadOnlyList<SimPlayer> Players,
    int StartingCredits = 10_000,
    double SuperweaponChargeSeconds = 600,
    int SuperweaponDamage = 1500,
    double SuperweaponRadiusCells = 6,
    double CombatNoise = 0);
