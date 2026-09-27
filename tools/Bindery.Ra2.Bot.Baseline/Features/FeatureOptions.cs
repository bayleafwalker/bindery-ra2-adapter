// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Baseline.Features;

/// <summary>
/// Tuning for <see cref="FeatureCompiler"/>. The 15 s / 25% army-value-swing
/// figure and the 60 s scouting-coverage window come straight from
/// <c>docs/architecture/strategic-bot.md</c>; the rest are conventional
/// RTS-AI defaults, documented where they are used.
/// </summary>
/// <param name="HistorySeconds">
/// How much per-compile history the trend ring buffer retains. Must be at
/// least the largest trend window (60 s).
/// </param>
/// <param name="ThreatSearchCells">
/// Radius, in ground cells of travel distance over the region graph, within
/// which an enemy contact counts toward a region's <see cref="ThreatAssessment.EnemyValue"/>.
/// </param>
/// <param name="SlowestTypicalSpeed">
/// A single conventional cells/second used for every ETA and reinforcement
/// estimate, standing in for "the slowest unit likely to be in the
/// force" — RA2 infantry-speed order of magnitude. A real per-composition
/// speed model belongs to a later package; this keeps ETAs a deterministic,
/// explainable function of distance alone.
/// </param>
/// <param name="ArmyValueSwingThreshold">Fractional own-army-value change over <see cref="ArmyValueSwingWindowSeconds"/> that raises <see cref="StrategicEventKind.ArmyValueSwing"/>.</param>
/// <param name="ArmyValueSwingWindowSeconds">Window for the army-value-swing check. Spec default: 15 s.</param>
/// <param name="ScoutingWindowSeconds">A region counts as "covered" for <see cref="ScoutingFeatures.CoverageFraction"/> if seen within this many seconds. Spec default: 60 s.</param>
/// <param name="EnemyStartUnscoutedGraceSeconds">How long into the match to withhold the "enemy start unscouted" unknown, so it does not fire in the opening seconds.</param>
/// <param name="EnemyTechUnknownThresholdSeconds">Age, in seconds, of the newest sighting of any enemy building before "enemy tech unknown" fires. Spec example: 120 s.</param>
/// <param name="EnemyArmyUnseenThresholdSeconds">Age, in seconds, of the newest sighting of any enemy unit before "enemy army not seen" fires. Spec example: 60 s.</param>
/// <param name="EventDedupWindowSeconds">Minimum gap between two emissions of the same event kind (and region, where applicable) before it is re-emitted.</param>
/// <param name="LowPowerGraceSeconds">Same de-dup gap, specifically for <see cref="StrategicEventKind.LowPower"/>, which would otherwise re-fire every compile while power stays negative.</param>
public sealed record FeatureOptions(
    double HistorySeconds = 65.0,
    double ThreatSearchCells = 60.0,
    double SlowestTypicalSpeed = 4.0,
    double ArmyValueSwingThreshold = 0.25,
    double ArmyValueSwingWindowSeconds = 15.0,
    double ScoutingWindowSeconds = 60.0,
    double EnemyStartUnscoutedGraceSeconds = 30.0,
    double EnemyTechUnknownThresholdSeconds = 120.0,
    double EnemyArmyUnseenThresholdSeconds = 60.0,
    double EventDedupWindowSeconds = 20.0,
    double LowPowerGraceSeconds = 30.0);
