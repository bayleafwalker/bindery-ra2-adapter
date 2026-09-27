// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bindery.Ra2.Bot.Sim;

namespace Bindery.Ra2.Bot.Arena;

/// <summary>
/// How hard and how noisy the benchmark is. The default is the original setting (fair economy, noiseless
/// combat), in which every arm won every match and no comparison carried information; <see cref="Contested"/> is
/// calibrated so the baseline selector wins roughly half its matches.
/// </summary>
/// <param name="Name">Preset name for the report (<c>standard</c>, <c>contested</c> or <c>custom</c>).</param>
/// <param name="OpponentIncomeMultiplier">Multiplier on every load the opponent's harvesters bank (a retail-style AI bonus).</param>
/// <param name="OpponentStartingCredits">Opponent starting credits; null keeps the simulator default.</param>
/// <param name="CombatNoise">Seeded per-shot damage noise (<see cref="SimSettings.CombatNoise"/>).</param>
/// <param name="AlliedIncomeMultiplier">
/// Multiplier on the Allied player's harvest income, whichever seat it is in: a faction balance handicap, because in
/// the approximate fixture the Soviet side wins every mirror match, which makes outcomes a function of the seed's
/// faction assignment rather than of strategy.
/// </param>
/// <param name="AlliedStartingCredits">Starting credits of the Allied player, whichever seat it is in (the same faction handicap); null keeps the default.</param>
public sealed record BenchmarkSettings(string Name, double OpponentIncomeMultiplier = 1.0, int? OpponentStartingCredits = null, double CombatNoise = 0, double AlliedIncomeMultiplier = 1.0, int? AlliedStartingCredits = null)
{
    public static BenchmarkSettings Standard { get; } = new("standard");

    /// <summary>
    /// The calibrated contested benchmark: mirror opponents on the live stack (<see cref="DefaultOpponents"/>), the
    /// Allied side (whichever seat) starts with 20,000 credits against the Soviet 10,000 to offset the fixture's
    /// Soviet edge, and combat carries ±25% seeded noise, so outcomes vary with the seed within each faction.
    /// Calibration (2026-09-26, the selector against the five live styles, all five maps, 6 seeds, 150 matches):
    /// selector 73/150 (0.49), as Allied 28/75, as Soviet 45/75; noiseless the same setting gave 69/150. That was
    /// before refineries came with RA2's free harvester (<see cref="Bindery.Ra2.Bot.Sim.SimSettings.FreeHarvesterWithRefinery"/>);
    /// rechecked after it (2026-09-26, all five maps, 4 seeds): selector 56/100 (0.56) against the live styles, as
    /// Allied 16/50, as Soviet 40/50, still inside the 30–70% band, so the setting was kept.
    /// The defaults also include the held-out opponents (<see cref="OpponentSets.HeldOut"/>), so every default run
    /// reports an out-of-sample win rate next to the live styles' partly in-sample one.
    /// </summary>
    public static BenchmarkSettings Contested { get; } = new("contested", CombatNoise: 0.25, AlliedStartingCredits: 20_000)
    {
        DefaultOpponents = ["live-balanced", "live-rush", "live-tech", "live-turtle", "live-harass", .. Bindery.Ra2.Bot.Sim.Opponents.OpponentProfiles.HeldOutStyles],
    };

    /// <summary>Opponents a run uses when <c>--opponents</c> is not given; null keeps the CLI default.</summary>
    public IReadOnlyList<string>? DefaultOpponents { get; init; }

    public static BenchmarkSettings Preset(string name) => name.ToLowerInvariant() switch
    {
        "standard" => Standard,
        "contested" => Contested,
        _ => throw new ArgumentException($"Unknown benchmark '{name}'; expected standard or contested."),
    };

    /// <summary>The simulator settings for one match under this benchmark.</summary>
    /// <param name="opponentHandicap">The opponent's own income multiplier (<see cref="OpponentSets.IncomeHandicap"/>), on top of the benchmark's.</param>
    public SimSettings ToSimSettings(int seed, double maxSeconds, PlayerId arm, Faction armFaction, PlayerId opponent, Faction opponentFaction, double opponentHandicap = 1.0) =>
        new(seed, maxSeconds,
            [
                new SimPlayer(arm, armFaction, armFaction == Faction.Allied ? AlliedIncomeMultiplier : 1.0, armFaction == Faction.Allied ? AlliedStartingCredits : null),
                new SimPlayer(opponent, opponentFaction, opponentHandicap * OpponentIncomeMultiplier * (opponentFaction == Faction.Allied ? AlliedIncomeMultiplier : 1.0),
                    OpponentStartingCredits ?? (opponentFaction == Faction.Allied ? AlliedStartingCredits : null)),
            ],
            CombatNoise: CombatNoise);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Name} (opponent income ×{OpponentIncomeMultiplier:0.##}, Allied income ×{AlliedIncomeMultiplier:0.##}, Allied starting credits {(AlliedStartingCredits?.ToString(CultureInfo.InvariantCulture) ?? "default")}, opponent starting credits {(OpponentStartingCredits?.ToString(CultureInfo.InvariantCulture) ?? "default")}, combat noise ±{CombatNoise:0.##})");
}
