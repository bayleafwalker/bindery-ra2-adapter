// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Tactics;

/// <summary>
/// Shared tunables for <see cref="SquadController"/>.
/// </summary>
/// <param name="EngagementRangeCells">
/// How close (in cells) an enemy contact must be, with fresh, confident
/// evidence, before a squad treats it as an engagement rather than a march.
/// </param>
/// <param name="MinConfidence">Minimum <see cref="EnemyContact.Confidence"/> to act on a contact.</param>
/// <param name="MaxContactAgeSeconds">Contacts older than this are treated as stale, not "recent".</param>
/// <param name="ReEngageAboveForceRatio">Local force ratio above which a retreating squad re-engages (spec default 1.0).</param>
/// <param name="MinStateSeconds">Minimum time in a retreat/engage state before it can flip again (spec default 5s).</param>
/// <param name="EngageContactAgeSeconds">
/// A mobile contact older than this is out of sight: it neither engages the squad, nor is a focus-fire target,
/// nor counts toward the local force ratio (it has moved on; an attack order on it is ignored in RA2). Buildings
/// stay engageable up to <paramref name="MaxContactAgeSeconds"/>, since they do not move.
/// </param>
public sealed record SquadControllerOptions(
    double EngagementRangeCells = 10.0,
    double MinConfidence = 0.4,
    double MaxContactAgeSeconds = 20.0,
    double ReEngageAboveForceRatio = 1.0,
    double MinStateSeconds = 5.0,
    double EngageContactAgeSeconds = 2.0);

/// <param name="ThreatRangeCells">How close an enemy combat contact must be for a harvester to flee.</param>
public sealed record HarvesterSafetyOptions(
    string Owner = "harvest",
    int Priority = 20,
    double MinHoldSeconds = 5.0,
    double TtlSeconds = 3.0,
    double ThreatRangeCells = 15.0,
    double MinConfidence = 0.4,
    double MaxContactAgeSeconds = 20.0);

/// <param name="HealthFractionThreshold">A vehicle below this health goes to a depot.</param>
/// <param name="RepairedFraction">A vehicle under repair stays until this health (exit threshold of the hysteresis).</param>
/// <param name="StallSeconds">A vehicle under repair whose health has not risen for this long is given back.</param>
/// <param name="RetryCooldownSeconds">How long a vehicle given back after a stalled repair is left alone.</param>
public sealed record RepairControllerOptions(
    string Owner = "repair",
    int Priority = 15,
    double MinHoldSeconds = 5.0,
    double TtlSeconds = 3.0,
    double HealthFractionThreshold = 0.4,
    double MaxDepotRangeCells = 40.0,
    double RepairedFraction = 0.9,
    double StallSeconds = 30.0,
    double RetryCooldownSeconds = 60.0);

public sealed record DeployControllerOptions(
    string Owner = "deploy",
    int Priority = 30,
    double MinHoldSeconds = 2.0,
    double TtlSeconds = 3.0);
