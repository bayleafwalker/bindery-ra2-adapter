// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot;

/// <summary>A quantity with its change over the last 5, 15 and 60 seconds of game time.</summary>
public sealed record Trend(double Current, double Delta5s, double Delta15s, double Delta60s)
{
    public static Trend Flat(double value) => new(value, 0, 0, 0);
}

public sealed record EconomyFeatures(
    Trend Credits,
    Trend IncomePerMinute,
    Trend SpendingPerMinute,
    double CashRunwaySeconds,
    double ProductionUtilization,
    int Harvesters,
    int Refineries,
    double OreRemainingFraction,
    PowerState Power);

/// <summary>A spatial group of own combat units.</summary>
/// <param name="ValueByRole">The cluster's value split by role (army value by role and location); null only in hand-built fixtures.</param>
public sealed record ForceCluster(RegionId Region, int Units, double Value, double AverageHealth, IReadOnlyDictionary<UnitRole, double>? ValueByRole = null);

public sealed record ArmyFeatures(
    Trend ArmyValue,
    IReadOnlyDictionary<UnitRole, double> ValueByRole,
    IReadOnlyList<ForceCluster> Clusters,
    Trend LossesValue,
    Trend KillsValue);

/// <summary>
/// Belief about the enemy. Every estimate carries the age of the evidence it
/// rests on; <see cref="ArmyValueConfidence"/> is in [0, 1].
/// </summary>
/// <param name="SuperweaponKnown">An enemy superweapon has been seen, launched, or shown by its public timer.</param>
/// <param name="TechLastSeenAgeSeconds">
/// Seconds since each <see cref="KnownTech"/> type (production buildings included) was last seen; null only in
/// hand-built fixtures.
/// </param>
public sealed record EnemyFeatures(
    Trend EstimatedArmyValue,
    double ArmyValueConfidence,
    IReadOnlyDictionary<UnitRole, double> CompositionByRole,
    IReadOnlySet<string> KnownTech,
    IReadOnlyList<string> KnownProduction,
    double NewestObservationAgeSeconds,
    double MedianObservationAgeSeconds,
    bool SuperweaponKnown,
    IReadOnlyDictionary<string, double>? TechLastSeenAgeSeconds = null);

/// <summary>One superweapon's countdown as a feature.</summary>
/// <param name="ChargeFraction">Charge in [0, 1].</param>
/// <param name="SecondsToReady">0 when ready.</param>
public sealed record SuperweaponTimer(string TypeId, double ChargeFraction, double SecondsToReady, bool Ready);

/// <summary>Own and enemy superweapon timers, ordered by readiness (soonest first).</summary>
public sealed record SuperweaponFeatures(IReadOnlyList<SuperweaponTimer> Own, IReadOnlyList<SuperweaponTimer> Enemy);

public enum RegionControl { Own, Contested, Enemy, Neutral, Unknown }

public sealed record MapControlFeatures(
    IReadOnlyDictionary<RegionId, RegionControl> Control,
    IReadOnlyList<RegionId> ExpansionCandidates,
    double OwnedOreFraction);

public sealed record ScoutingFeatures(
    double CoverageFraction,
    IReadOnlyDictionary<RegionId, double> RegionAgeSeconds,
    IReadOnlyList<string> ImportantUnknowns);

/// <summary>Local threat to one region: enemy value near it versus own value that can respond.</summary>
/// <remarks>
/// Seconds-valued features use 9999 for "unreachable" or "never observed" (as
/// <see cref="EconomyFeatures.CashRunwaySeconds"/> caps at 9999), so every feature is finite.
/// </remarks>
/// <param name="LikelyAttackPath">
/// Ground route, as regions from the threatening contact's last-seen region to
/// <see cref="Region"/> inclusive, taken by the contact with the smallest ETA:
/// the approach a defender would hold. Empty when nothing threatens the region
/// or it has no ground route; null only in hand-built fixtures that predate it.
/// </param>
public sealed record ThreatAssessment(
    RegionId Region,
    double EnemyValue,
    double OwnValue,
    double LocalForceRatio,
    double EnemyEtaSeconds,
    double ReinforcementSeconds,
    bool IsBase,
    double Confidence,
    IReadOnlyList<RegionId>? LikelyAttackPath = null);

public enum StrategicEventKind
{
    NewEnemyTech,
    MinerLost,
    McvLost,
    BuildingLost,
    BaseUnderAttack,
    ProductionTransition,
    SuperweaponDetected,
    ArmyValueSwing,
    ExpansionTaken,
    EnemyExpansionSeen,
    LowPower,
}

/// <summary>A meaningful change, detected deterministically. Severity is in [0, 1].</summary>
public sealed record StrategicEvent(StrategicEventKind Kind, GameTime Time, double Severity, string Detail, RegionId? Region = null);

/// <summary>
/// The compiled strategic picture for one snapshot. This is the only view of
/// the game a strategist receives: exact, compact, and built only from belief.
/// </summary>
/// <param name="Superweapons">Own and enemy superweapon charge; null when the source reports no superweapon timers.</param>
public sealed record StrategicFeatures(
    long SnapshotVersion,
    GameTime Time,
    ObservationMode Mode,
    Faction Faction,
    EconomyFeatures Economy,
    ArmyFeatures Army,
    EnemyFeatures Enemy,
    MapControlFeatures MapControl,
    ScoutingFeatures Scouting,
    IReadOnlyList<ThreatAssessment> Threats,
    IReadOnlyList<StrategicEvent> Events,
    SuperweaponFeatures? Superweapons = null);

public interface IFeatureCompiler
{
    /// <summary>
    /// Compiles features for a snapshot. Implementations keep a bounded history
    /// of prior snapshots to compute trends; they must be called in version order.
    /// </summary>
    StrategicFeatures Compile(BeliefSnapshot snapshot);
}
