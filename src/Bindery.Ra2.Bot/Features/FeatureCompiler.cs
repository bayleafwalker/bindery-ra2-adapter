// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Features;

/// <summary>
/// Compiles a <see cref="BeliefSnapshot"/> into <see cref="StrategicFeatures"/>:
/// the only view of the game any strategist ever sees. Everything here reads
/// belief only (never hidden simulator state), so a fog-mode run and an
/// oracle-mode run that differ only in things never observed compile to
/// byte-identical features (see the fog test in the test project).
///
/// Instances are stateful (a ring buffer for trends, running totals for
/// losses/kills, and de-duplication state for strategic events) and must be
/// called once per snapshot in increasing <see cref="BeliefSnapshot.Version"/>
/// order, per the interface contract.
/// </summary>
public sealed partial class FeatureCompiler : IFeatureCompiler
{
    /// <summary>
    /// Stand-in for "unreachable" or "never observed" in any field measured
    /// in seconds. <see cref="Bindery.Ra2.Bot.BotJson"/> (a fixed contract)
    /// cannot serialise <see cref="double.PositiveInfinity"/>, and the spec
    /// itself caps <see cref="EconomyFeatures.CashRunwaySeconds"/> at 9999 for
    /// the same reason, so every other "how long until/since" field in this
    /// package's output follows the same convention instead of raw infinity.
    /// </summary>
    internal const double UnknownSeconds = 9999.0;

    /// <summary>Combat roles counted toward "army value". Harvesters/MCVs are economy, not force.</summary>
    private static readonly HashSet<UnitRole> CombatRoles =
    [
        UnitRole.Scout, UnitRole.AntiArmor, UnitRole.AntiInfantry, UnitRole.AntiAir,
        UnitRole.Artillery, UnitRole.Engineer, UnitRole.Support, UnitRole.Defense,
    ];

    private readonly IRulesDatabase rules;
    private readonly FeatureOptions options;

    private readonly List<Sample> history = [];
    private readonly Dictionary<EntityId, OwnEntityMemory> ownEntityMemory = [];
    private readonly HashSet<GameEvent> processedEvents = [];
    private readonly Dictionary<(StrategicEventKind Kind, RegionId? Region), GameTime> lastEmitted = [];
    private readonly HashSet<string> allSeenTechEver = new(StringComparer.Ordinal);
    private readonly HashSet<string> previousKnownProduction = new(StringComparer.Ordinal);
    /// <summary>Ore regions held by an own building at the previous compile, for <see cref="StrategicEventKind.ExpansionTaken"/>.</summary>
    private readonly HashSet<RegionId> previousOwnExpansions = [];
    /// <summary>Ore regions holding a remembered enemy building at the previous compile, for <see cref="StrategicEventKind.EnemyExpansionSeen"/>.</summary>
    private readonly HashSet<RegionId> previousEnemyExpansions = [];
    private readonly Dictionary<string, int> enemySuperweaponsSeen = new(StringComparer.Ordinal);

    private string? cachedMapId;
    private RegionGraph? cachedGraph;
    private double cumulativeLossesValue;
    private double cumulativeKillsValue;
    private bool superweaponEverLaunched;
    private bool hasCompiledBefore;

    public FeatureCompiler(IRulesDatabase rules, FeatureOptions options)
    {
        this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    private readonly record struct Sample(
        GameTime Time,
        double Credits,
        double IncomePerMinute,
        double SpendingPerMinute,
        double ArmyValue,
        double LossesValue,
        double KillsValue,
        double EnemyArmyValueEstimate);

    private readonly record struct OwnEntityMemory(UnitRole Role, EntityKind Kind, int Value, RegionId Region);

    public StrategicFeatures Compile(BeliefSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        RegionGraph graph = GraphFor(snapshot.Map);
        RememberOwnEntities(snapshot);

        List<StrategicEvent> events = [];
        ProcessRecentEvents(snapshot, events);
        PruneProcessedEvents(snapshot.Time);

        (double armyValueCurrent, IReadOnlyDictionary<UnitRole, double> valueByRole, IReadOnlyList<ForceCluster> clusters) =
            CompileArmy(snapshot);

        EnemyFeatures enemy = CompileEnemy(snapshot, out double enemyArmyValueCurrent);
        MapControlFeatures mapControl = CompileMapControl(snapshot);
        ScoutingFeatures scouting = CompileScouting(snapshot, enemy);
        EconomyFeatures economy = CompileEconomy(snapshot, out double incomePerMinuteCurrent, out double spendingPerMinuteCurrent);

        IReadOnlyList<ThreatAssessment> threats = CompileThreats(snapshot, graph, clusters, armyValueCurrent);
        SuperweaponFeatures? superweapons = CompileSuperweapons(snapshot, events);
        if (superweapons is { Enemy.Count: > 0 }) enemy = enemy with { SuperweaponKnown = true };

        Trend creditsTrend = BuildTrend(snapshot.Time, snapshot.Credits, static s => s.Credits);
        Trend incomeTrend = BuildTrend(snapshot.Time, incomePerMinuteCurrent, static s => s.IncomePerMinute);
        Trend spendingTrend = BuildTrend(snapshot.Time, spendingPerMinuteCurrent, static s => s.SpendingPerMinute);
        Trend armyValueTrend = BuildTrend(snapshot.Time, armyValueCurrent, static s => s.ArmyValue);
        Trend lossesTrend = BuildTrend(snapshot.Time, cumulativeLossesValue, static s => s.LossesValue);
        Trend killsTrend = BuildTrend(snapshot.Time, cumulativeKillsValue, static s => s.KillsValue);
        Trend enemyArmyValueTrend = BuildTrend(snapshot.Time, enemyArmyValueCurrent, static s => s.EnemyArmyValueEstimate);

        EconomyFeatures economyWithTrends = economy with { Credits = creditsTrend, IncomePerMinute = incomeTrend, SpendingPerMinute = spendingTrend };
        ArmyFeatures army = new(armyValueTrend, valueByRole, clusters, lossesTrend, killsTrend);
        EnemyFeatures enemyWithTrend = enemy with { EstimatedArmyValue = enemyArmyValueTrend };

        PushSample(new Sample(
            snapshot.Time, snapshot.Credits, incomePerMinuteCurrent, spendingPerMinuteCurrent,
            armyValueCurrent, cumulativeLossesValue, cumulativeKillsValue, enemyArmyValueCurrent));

        DetectStateTransitionEvents(snapshot, enemyWithTrend, armyValueTrend, events);
        events.Sort(static (a, b) =>
        {
            int byTime = a.Time.CompareTo(b.Time);
            return byTime != 0 ? byTime : a.Kind.CompareTo(b.Kind);
        });

        hasCompiledBefore = true;

        return new StrategicFeatures(
            snapshot.Version, snapshot.Time, snapshot.Mode, snapshot.Faction,
            economyWithTrends, army, enemyWithTrend, mapControl, scouting, threats, events, superweapons);
    }

    /// <summary>
    /// Own and enemy superweapon timers from the snapshot (RA2 shows every timer to every player), soonest first;
    /// null when the source reports none. An enemy superweapon appearing for the first time (per owner and type)
    /// is a <see cref="StrategicEventKind.SuperweaponDetected"/> event.
    /// </summary>
    private SuperweaponFeatures? CompileSuperweapons(BeliefSnapshot snapshot, List<StrategicEvent> events)
    {
        if (snapshot.Superweapons is not { } timers) return null;
        static SuperweaponTimer Timer(SuperweaponStatus s) => new(
            s.TypeId,
            s.ChargeSeconds > 0 ? Math.Clamp(1 - s.SecondsToReady / s.ChargeSeconds, 0, 1) : 1,
            Math.Max(0, s.SecondsToReady),
            s.Ready);
        List<SuperweaponTimer> own = [.. timers.Where(s => s.Owner == snapshot.Self).Select(Timer)
            .OrderBy(static t => t.SecondsToReady).ThenBy(static t => t.TypeId, StringComparer.Ordinal)];
        List<SuperweaponStatus> theirs = [.. timers.Where(s => s.Owner != snapshot.Self)];
        List<SuperweaponTimer> enemy = [.. theirs.Select(Timer).OrderBy(static t => t.SecondsToReady).ThenBy(static t => t.TypeId, StringComparer.Ordinal)];

        foreach (IGrouping<string, SuperweaponStatus> group in theirs
            .GroupBy(static s => $"{s.Owner.Value}:{s.TypeId}", StringComparer.Ordinal)
            .OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            int count = group.Count();
            int before = enemySuperweaponsSeen.GetValueOrDefault(group.Key);
            if (count > before)
            {
                AddEvent(events, StrategicEventKind.SuperweaponDetected, snapshot.Time, 0.7, $"built:{group.First().TypeId}");
            }
            enemySuperweaponsSeen[group.Key] = Math.Max(before, count);
        }
        return new SuperweaponFeatures(own, enemy);
    }

    private RegionGraph GraphFor(MapInfo map)
    {
        if (cachedGraph is not null && cachedMapId == map.MapId) return cachedGraph;
        cachedMapId = map.MapId;
        cachedGraph = new RegionGraph(map);
        return cachedGraph;
    }

    private void RememberOwnEntities(BeliefSnapshot snapshot)
    {
        foreach (OwnEntity e in snapshot.Own) ownEntityMemory[e.Id] = new OwnEntityMemory(e.Role, e.Kind, e.Value, e.Region);
    }

    private (double ArmyValue, IReadOnlyDictionary<UnitRole, double> ValueByRole, IReadOnlyList<ForceCluster> Clusters) CompileArmy(
        BeliefSnapshot snapshot)
    {
        Dictionary<UnitRole, double> valueByRole = [];
        Dictionary<RegionId, (int Units, double Value, double HealthSum)> byRegion = [];
        Dictionary<RegionId, SortedDictionary<UnitRole, double>> roleByRegion = [];
        double total = 0;

        foreach (OwnEntity e in snapshot.Own)
        {
            if (e.Kind == EntityKind.Building || !CombatRoles.Contains(e.Role)) continue;
            total += e.Value;
            valueByRole[e.Role] = valueByRole.GetValueOrDefault(e.Role) + e.Value;

            (int units, double value, double healthSum) = byRegion.TryGetValue(e.Region, out var existing) ? existing : (0, 0.0, 0.0);
            byRegion[e.Region] = (units + 1, value + e.Value, healthSum + e.HealthFraction);
            if (!roleByRegion.TryGetValue(e.Region, out SortedDictionary<UnitRole, double>? roles)) roleByRegion[e.Region] = roles = [];
            roles[e.Role] = roles.GetValueOrDefault(e.Role) + e.Value;
        }

        // Role by location: each cluster carries its own role split, so "where is the anti-armour" is answerable.
        List<ForceCluster> clusters = [.. byRegion
            .OrderBy(static kv => kv.Key.Value)
            .Select(kv => new ForceCluster(kv.Key, kv.Value.Units, kv.Value.Value, kv.Value.Units == 0 ? 0 : kv.Value.HealthSum / kv.Value.Units, roleByRegion[kv.Key]))];

        return (total, valueByRole, clusters);
    }

    private Trend BuildTrend(GameTime now, double current, Func<Sample, double> selector)
    {
        if (history.Count == 0) return Trend.Flat(current);
        return new Trend(
            current,
            current - Baseline(now, 5.0, selector),
            current - Baseline(now, 15.0, selector),
            current - Baseline(now, 60.0, selector));
    }

    private double Baseline(GameTime now, double windowSeconds, Func<Sample, double> selector)
    {
        GameTime cutoff = now.Plus(-windowSeconds);
        Sample best = history[0];
        foreach (Sample s in history)
        {
            if (s.Time <= cutoff) best = s;
            else break;
        }
        return selector(best);
    }

    private void PushSample(Sample sample)
    {
        history.Add(sample);
        GameTime cutoff = sample.Time.Plus(-options.HistorySeconds);
        int keepFrom = 0;
        while (keepFrom < history.Count - 1 && history[keepFrom].Time < cutoff) keepFrom++;
        if (keepFrom > 0) history.RemoveRange(0, keepFrom);
    }

    private static RegionId? RegionForCell(MapInfo map, Cell? cell) => cell is { } c ? map.RegionOf(c)?.Id : null;

    /// <summary>Adds an event unconditionally. Used only where the caller already guarantees single-fire-per-change semantics.</summary>
    private static void AddEvent(List<StrategicEvent> events, StrategicEventKind kind, GameTime time, double severity, string detail, RegionId? region = null) =>
        events.Add(new StrategicEvent(kind, time, Math.Clamp(severity, 0, 1), detail, region));

    /// <summary>Adds an event, suppressed if the same (kind, region) fired within <see cref="FeatureOptions.EventDedupWindowSeconds"/>.</summary>
    private void EmitGated(List<StrategicEvent> events, StrategicEventKind kind, GameTime time, double severity, string detail, RegionId? region = null)
    {
        (StrategicEventKind, RegionId?) key = (kind, region);
        if (lastEmitted.TryGetValue(key, out GameTime last) && time.SecondsSince(last) < options.EventDedupWindowSeconds) return;
        lastEmitted[key] = time;
        AddEvent(events, kind, time, severity, detail, region);
    }
}
