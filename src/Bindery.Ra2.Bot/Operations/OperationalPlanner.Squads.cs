// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bindery.Ra2.Bot.Arbitration;

namespace Bindery.Ra2.Bot.Operations;

/// <summary>
/// Squad formation and objective assignment. Membership is recomputed on every
/// pass from the live combat units, except for the persistent scout and harass
/// squads, and every squad has a stable id so tactical hysteresis state survives:
/// <list type="bullet">
/// <item><c>scout</c>: one cheap, fast unit while the intent has a
/// <see cref="ObjectiveKind.Scout"/> objective, visiting the intent's
/// <see cref="StrategicIntent.RegionsOfInterest"/> first (in order, skipping any seen within
/// <see cref="OperationalOptions.ScoutRevisitSeconds"/>), then non-own start locations
/// (oldest sighting first), then the region seen longest ago.</item>
/// <item><c>harass</c>: up to <see cref="OperationalOptions.HarassSquadSize"/> units
/// while the intent has a <see cref="ObjectiveKind.Harass"/> objective, in sorties: a sortie
/// starts every <c>harassIntervalSeconds</c> (playbook parameter, else
/// <see cref="OperationalOptions.DefaultHarassIntervalSeconds"/>), goes to the target engaged, and after
/// <see cref="OperationalOptions.HarassDwellSeconds"/> there heads home unengaged to regroup until the
/// next sortie. A sortie still travelling when the next one is due simply carries on.</item>
/// <item><c>retreat</c>: everything else, when the intent has a
/// <see cref="ObjectiveKind.Retreat"/> objective.</item>
/// <item><c>defend</c>: everything else while a base region is threatened (base threat
/// ratio at least the playbook's <c>defendThreatRatio</c>, else
/// <see cref="OperationalOptions.DefaultDefendThreatRatio"/>), and otherwise while no attack is on;
/// it stages, engaged, at the first of the intent's <see cref="StrategicIntent.RegionsOfInterest"/> that is
/// reachable, not the attack target and not enemy-held, else at the own building region nearest the target.</item>
/// <item><c>attack</c>: everything else once an attack is wanted (an
/// <see cref="ObjectiveKind.AttackRegion"/> objective, or attack conditions on any
/// non-defend posture) and ready (all attack conditions hold and own army value reaches
/// the playbook's <c>attackArmyValue</c> or <see cref="OperationalOptions.MinAttackArmyValue"/>).
/// An attack in progress continues until army value drops below
/// <see cref="OperationalOptions.AttackHoldFraction"/> of that threshold (hysteresis). The attack order carries
/// the playbook's <c>siegeRangeBufferCells</c> as <see cref="SquadOrder.StandoffBufferCells"/>, which the squad
/// controller turns into artillery stand-off.</item>
/// </list>
/// The attack target is the objective's region while it still holds known enemy
/// presence or is unseen; once it is seen empty, the planner moves on to the known enemy
/// building nearest the base, then the enemy's suspected start, then the non-own start
/// location seen longest ago (map start locations are public knowledge).
/// Units change squads by releasing the old squad's lease first; units leased by a
/// non-squad controller (repair, harvester safety) are left alone.
/// </summary>
public sealed partial class OperationalPlanner
{
    private const string ScoutSquad = "scout";
    private const string HarassSquad = "harass";
    private const string AttackSquad = "attack";
    private const string DefendSquad = "defend";
    private const string RetreatSquad = "retreat";

    private readonly Dictionary<string, SquadState> squads = new(StringComparer.Ordinal);
    private RegionId? scoutTarget;
    private RegionId? attackTarget;
    private bool attacking;
    private GameTime? harassCycleStart;
    private GameTime? harassArrivedAt;
    private bool harassReturning;

    private sealed class SquadState
    {
        public required string SquadId { get; init; }
        public ObjectiveKind Objective { get; set; }
        public RegionId TargetRegion { get; set; }
        public bool Engage { get; set; }
        public List<EntityId> Units { get; } = [];
    }

    private List<SquadOrder> PlanSquads(
        BeliefSnapshot belief,
        StrategicFeatures features,
        StrategicIntent intent,
        RegionGraph graph,
        ILeaseManager leases,
        List<string> notes)
    {
        Dictionary<EntityId, OwnEntity> alive = belief.Own.ToDictionary(static e => e.Id);
        Dictionary<string, List<EntityId>> previous = squads.ToDictionary(static kv => kv.Key, static kv => kv.Value.Units.ToList(), StringComparer.Ordinal);
        foreach (SquadState squad in squads.Values) squad.Units.RemoveAll(u => !alive.ContainsKey(u));

        List<OwnEntity> pool = belief.Own
            .Where(static e => e.Kind != EntityKind.Building && e.Role is not (UnitRole.Harvester or UnitRole.Mcv or UnitRole.Engineer))
            .OrderBy(static e => e.Id.Value)
            .ToList();

        RegionId home = HomeRegion(belief);
        double retreatRatio = intent.PlaybookParameters.TryGetValue("retreatBelowForceRatio", out double rr)
            ? rr
            : options.DefaultRetreatBelowForceRatio;
        double defendAt = intent.PlaybookParameters.TryGetValue("defendThreatRatio", out double dt)
            ? dt
            : options.DefaultDefendThreatRatio;
        double standoff = intent.PlaybookParameters.TryGetValue("siegeRangeBufferCells", out double sb) ? Math.Max(0, sb) : 0;

        // Persistent squads first: they keep their members while their objective lasts.
        Objective? scout = intent.Objectives.Where(static o => o.Kind == ObjectiveKind.Scout).OrderBy(static o => o.Priority).FirstOrDefault();
        if (scout is not null && pool.Count > 1) AssignScout(belief, graph, home, pool, intent.RegionsOfInterest, notes);
        else Disband(ScoutSquad);

        Objective? harass = intent.Objectives.Where(static o => o.Kind == ObjectiveKind.Harass && o.Region is not null).OrderBy(static o => o.Priority).FirstOrDefault();
        if (harass is not null) AssignHarass(belief, intent, home, harass.Region!.Value, pool, notes);
        else
        {
            Disband(HarassSquad);
            harassCycleStart = harassArrivedAt = null;
            harassReturning = false;
        }

        foreach (string id in new[] { AttackSquad, DefendSquad, RetreatSquad }) Disband(id);

        Objective? retreat = intent.Objectives.Where(static o => o.Kind == ObjectiveKind.Retreat).OrderBy(static o => o.Priority).FirstOrDefault();
        double threat = ConditionEvaluator.BaseThreatRatio(features);
        if (retreat is not null)
        {
            Fill(GetOrCreate(RetreatSquad, ObjectiveKind.Retreat, retreat.Region ?? home, engage: false), pool);
            attacking = false;
        }
        else if (threat >= defendAt)
        {
            RegionId threatened = features.Threats.Where(static t => t.IsBase)
                .OrderBy(static t => t.LocalForceRatio).ThenBy(static t => t.Region.Value)
                .Select(static t => t.Region).DefaultIfEmpty(home).First();
            Fill(GetOrCreate(DefendSquad, ObjectiveKind.DefendRegion, threatened, engage: true), pool);
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"squads: defending {threatened} (base threat {threat:0.00})"));
            attacking = false;
        }
        else
        {
            Objective? attack = intent.Objectives.Where(static o => o.Kind == ObjectiveKind.AttackRegion).OrderBy(static o => o.Priority).FirstOrDefault();
            bool wanted = attack is not null || (intent.Posture != StrategicPosture.Defend && intent.AttackConditions.Count > 0);
            double army = features.Army.ArmyValue.Current;
            double minArmy = intent.PlaybookParameters.TryGetValue("attackArmyValue", out double av) ? av : options.MinAttackArmyValue;
            bool conditions = intent.AttackConditions.All(c => options.Evaluator(c, features));
            bool ready = wanted && (attacking ? army >= minArmy * options.AttackHoldFraction : conditions && army >= minArmy);
            if (ready)
            {
                RegionId target = ResolveAttackTarget(belief, graph, home, attack?.Region);
                attacking = true;
                Fill(GetOrCreate(AttackSquad, ObjectiveKind.AttackRegion, target, engage: true), pool);
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"squads: attacking {target} with army value {army:0}"));
            }
            else
            {
                attacking = false;
                RegionId staging = wanted ? StagingRegion(belief, features, intent, graph, home, attack?.Region) : home;
                Fill(GetOrCreate(DefendSquad, ObjectiveKind.DefendRegion, staging, engage: true), pool);
                if (wanted) notes.Add(string.Create(CultureInfo.InvariantCulture, $"squads: staging at {staging} (army {army:0}/{minArmy:0}, conditions {(conditions ? "hold" : "not met")})"));
            }
        }

        List<string> empty = squads.Where(static kv => kv.Value.Units.Count == 0).Select(static kv => kv.Key).ToList();
        foreach (string id in empty) squads.Remove(id);

        return LeaseAndOrder(belief, leases, previous, retreatRatio, standoff);
    }

    private List<SquadOrder> LeaseAndOrder(BeliefSnapshot belief, ILeaseManager leases, Dictionary<string, List<EntityId>> previous, double retreatRatio, double standoff)
    {
        List<SquadOrder> orders = [];
        foreach (SquadState squad in squads.Values.OrderBy(static s => s.SquadId, StringComparer.Ordinal))
        {
            string owner = $"squad:{squad.SquadId}";
            List<EntityId> held = [];
            foreach (EntityId unit in squad.Units.OrderBy(static u => u.Value))
            {
                LeaseKey key = LeaseKey.Unit(unit);
                string? currentOwner = leases.OwnerOf(key, belief.Time);
                bool ok;
                if (currentOwner == owner)
                {
                    ok = leases.Renew(key, owner, belief.Time, options.SquadLeaseTtlSeconds);
                }
                else
                {
                    if (currentOwner is not null && currentOwner.StartsWith("squad:", StringComparison.Ordinal)) leases.Release(key, currentOwner);
                    ok = leases.TryAcquire(key, owner, options.SquadLeasePriority, belief.Time, options.SquadLeaseMinHoldSeconds, options.SquadLeaseTtlSeconds) is not null;
                }
                if (ok) held.Add(unit);
            }
            if (held.Count == 0) continue;
            orders.Add(new SquadOrder(squad.SquadId, squad.Objective, squad.TargetRegion, held, squad.Engage, retreatRatio,
                squad.SquadId == AttackSquad ? standoff : 0));
        }

        // Units that left every squad give their lease back, so another controller (or a later squad) can take them.
        HashSet<EntityId> kept = squads.Values.SelectMany(static s => s.Units).ToHashSet();
        foreach ((string squadId, List<EntityId> units) in previous)
        {
            foreach (EntityId unit in units.Where(u => !kept.Contains(u))) leases.Release(LeaseKey.Unit(unit), $"squad:{squadId}");
        }
        return orders;
    }

    private SquadState GetOrCreate(string squadId, ObjectiveKind objective, RegionId target, bool engage)
    {
        if (!squads.TryGetValue(squadId, out SquadState? state))
        {
            state = new SquadState { SquadId = squadId };
            squads[squadId] = state;
        }
        state.Objective = objective;
        state.TargetRegion = target;
        state.Engage = engage;
        return state;
    }

    private void Disband(string squadId) => squads.Remove(squadId);

    private static void Fill(SquadState squad, List<OwnEntity> pool)
    {
        foreach (OwnEntity unit in pool) squad.Units.Add(unit.Id);
        pool.Clear();
    }

    private void AssignScout(BeliefSnapshot belief, RegionGraph graph, RegionId home, List<OwnEntity> pool, IReadOnlyList<RegionId> regionsOfInterest, List<string> notes)
    {
        SquadState squad = GetOrCreate(ScoutSquad, ObjectiveKind.Scout, scoutTarget ?? home, engage: false);
        squad.Units.RemoveAll(u => !pool.Any(p => p.Id == u));
        if (squad.Units.Count == 0)
        {
            OwnEntity? candidate = pool
                .OrderByDescending(e => rules.TryGet(e.TypeId, out UnitRule r) ? r.Speed : 0)
                .ThenBy(static e => e.Value).ThenBy(static e => e.Id.Value)
                .FirstOrDefault();
            if (candidate is null) return;
            squad.Units.Add(candidate.Id);
        }
        OwnEntity scout = pool.First(p => p.Id == squad.Units[0]);
        pool.RemoveAll(p => squad.Units.Contains(p.Id));

        bool arrived = scoutTarget is { } t && scout.Region == t;
        bool seenRecently = scoutTarget is { } s && belief.RegionLastSeen.TryGetValue(s, out GameTime seen) && belief.Time.SecondsSince(seen) < 5;
        if (scoutTarget is null || arrived || (seenRecently && scout.Region != scoutTarget))
        {
            scoutTarget = NextScoutTarget(belief, graph, home, scout.Region, regionsOfInterest);
            notes.Add($"scout: heading to {scoutTarget}");
        }
        squad.TargetRegion = scoutTarget ?? home;
    }

    private RegionId? NextScoutTarget(BeliefSnapshot belief, RegionGraph graph, RegionId home, RegionId from, IReadOnlyList<RegionId> regionsOfInterest)
    {
        double Age(Region r) => belief.RegionLastSeen.TryGetValue(r.Id, out GameTime t) ? belief.Time.SecondsSince(t) : double.MaxValue;
        foreach (RegionId interest in regionsOfInterest)
        {
            Region? region = belief.Map.Regions.FirstOrDefault(r => r.Id == interest);
            if (region is null || interest == from || double.IsInfinity(graph.Distance(from, interest))) continue;
            if (Age(region) > options.ScoutRevisitSeconds) return interest;
        }
        HashSet<RegionId> ownRegions = belief.Own.Where(static e => e.Kind == EntityKind.Building).Select(static e => e.Region).ToHashSet();
        Region? start = belief.Map.Regions
            .Where(r => r.IsStartLocation && !ownRegions.Contains(r.Id) && r.Id != from && Age(r) > options.ScoutRevisitSeconds)
            .OrderByDescending(Age).ThenBy(r => graph.Distance(from, r.Id)).ThenBy(static r => r.Id.Value)
            .FirstOrDefault();
        if (start is not null) return start.Id;
        return belief.Map.Regions
            .Where(r => !r.Water && r.Id != from && !double.IsInfinity(graph.Distance(from, r.Id)))
            .OrderByDescending(Age).ThenBy(r => graph.Distance(from, r.Id)).ThenBy(static r => r.Id.Value)
            .Select(static r => (RegionId?)r.Id)
            .FirstOrDefault() ?? home;
    }

    private void AssignHarass(BeliefSnapshot belief, StrategicIntent intent, RegionId home, RegionId region, List<OwnEntity> pool, List<string> notes)
    {
        SquadState squad = GetOrCreate(HarassSquad, ObjectiveKind.Harass, region, engage: true);
        squad.Units.RemoveAll(u => !pool.Any(p => p.Id == u));
        foreach (OwnEntity unit in pool
            .Where(p => !squad.Units.Contains(p.Id))
            .OrderByDescending(e => rules.TryGet(e.TypeId, out UnitRule r) ? r.Speed : 0)
            .ThenBy(static e => e.Id.Value)
            .ToList())
        {
            if (squad.Units.Count >= options.HarassSquadSize) break;
            squad.Units.Add(unit.Id);
        }
        List<OwnEntity> members = pool.Where(p => squad.Units.Contains(p.Id)).ToList();
        pool.RemoveAll(p => squad.Units.Contains(p.Id));

        // Sortie timing: cycles of harassIntervalSeconds from the first sortie; each cycle goes out, dwells, returns.
        double interval = Math.Max(1, intent.PlaybookParameters.TryGetValue("harassIntervalSeconds", out double hi) ? hi : options.DefaultHarassIntervalSeconds);
        GameTime now = belief.Time;
        if (harassCycleStart is not { } start || now.SecondsSince(start) >= interval)
        {
            harassCycleStart = harassCycleStart is { } previous
                ? previous.Plus(Math.Floor(now.SecondsSince(previous) / interval) * interval)
                : now;
            harassArrivedAt = null;
            harassReturning = false;
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"harass: sortie to {region} (every {interval:0} s)"));
        }
        if (!harassReturning)
        {
            bool arrived = members.Count > 0 && members.Count(m => m.Region == region) * 2 > members.Count;
            if (arrived) harassArrivedAt ??= now;
            if (harassArrivedAt is { } at && now.SecondsSince(at) >= options.HarassDwellSeconds)
            {
                harassReturning = true;
                notes.Add($"harass: sortie done, regrouping at {home}");
            }
        }
        squad.TargetRegion = harassReturning ? home : region;
        squad.Engage = !harassReturning;
    }

    private static RegionId HomeRegion(BeliefSnapshot belief)
    {
        OwnEntity? anchor = belief.Own
            .Where(static e => e.Kind == EntityKind.Building)
            .OrderBy(static e => e.Role == UnitRole.Production ? 0 : 1).ThenBy(static e => e.Id.Value)
            .FirstOrDefault();
        if (anchor is not null) return anchor.Region;
        return belief.Own.OrderBy(static e => e.Id.Value).Select(static e => e.Region).FirstOrDefault();
    }

    /// <summary>
    /// Where the army gathers before a push: the first region of interest that is reachable, not the target and
    /// not enemy-held; otherwise the own building region closest to the attack target.
    /// </summary>
    private RegionId StagingRegion(BeliefSnapshot belief, StrategicFeatures features, StrategicIntent intent, RegionGraph graph, RegionId home, RegionId? objective)
    {
        RegionId? target = objective ?? attackTarget ?? KnownEnemyBaseRegion(belief, graph, home);
        foreach (RegionId interest in intent.RegionsOfInterest)
        {
            if (interest == target || !belief.Map.Regions.Any(r => r.Id == interest) || double.IsInfinity(graph.Distance(home, interest))) continue;
            if (features.MapControl.Control.TryGetValue(interest, out RegionControl control) && control == RegionControl.Enemy) continue;
            return interest;
        }
        if (target is not { } t) return home;
        return belief.Own.Where(static e => e.Kind == EntityKind.Building)
            .Select(static e => e.Region).Distinct()
            .OrderBy(r => graph.Distance(r, t)).ThenBy(static r => r.Value)
            .DefaultIfEmpty(home).First();
    }

    private RegionId ResolveAttackTarget(BeliefSnapshot belief, RegionGraph graph, RegionId home, RegionId? objective)
    {
        // A target the army cannot walk to (water, or cut off) is no target: contacts near a shore can map to
        // the water region by nearest centre, and ordering a ground army there only produces rejected moves.
        bool Reachable(RegionId region) => !double.IsInfinity(graph.Distance(home, region));
        if (objective is { } requested && !Reachable(requested)) objective = null;
        if (attackTarget is { } previous && !Reachable(previous)) attackTarget = null;

        bool Cleared(RegionId region) =>
            belief.RegionLastSeen.TryGetValue(region, out GameTime seen)
            && belief.Time.SecondsSince(seen) < 2
            && !belief.Enemies.Any(c => !c.ConfirmedDestroyed && c.LastSeenRegion == region && belief.Time.SecondsSince(c.LastSeenAt) < 2);

        if (objective is { } o && !Cleared(o))
        {
            attackTarget = o;
            return o;
        }
        if (attackTarget is { } current && !Cleared(current)) return current;
        RegionId? next = KnownEnemyBaseRegion(belief, graph, home);
        next ??= belief.EnemyPlayers.Select(static p => p.SuspectedStart).FirstOrDefault(s => s is { } start && Reachable(start));
        next ??= belief.Map.Regions
            .Where(r => r.IsStartLocation && r.Id != home && !Cleared(r.Id) && Reachable(r.Id))
            .OrderBy(r => belief.RegionLastSeen.TryGetValue(r.Id, out GameTime t) ? t.Frame : long.MinValue)
            .ThenBy(r => graph.Distance(home, r.Id)).ThenBy(static r => r.Id.Value)
            .Select(static r => (RegionId?)r.Id).FirstOrDefault();
        attackTarget = next ?? objective ?? home;
        return attackTarget.Value;
    }

    private static RegionId? KnownEnemyBaseRegion(BeliefSnapshot belief, RegionGraph graph, RegionId home) =>
        belief.Enemies
            .Where(static c => !c.ConfirmedDestroyed && c.Kind == EntityKind.Building)
            .Select(static c => c.LastSeenRegion).Distinct()
            .Where(r => !double.IsInfinity(graph.Distance(home, r)))
            .OrderBy(r => graph.Distance(home, r)).ThenBy(static r => r.Value)
            .Select(static r => (RegionId?)r).FirstOrDefault();
}
