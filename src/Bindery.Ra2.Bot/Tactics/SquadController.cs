// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Tactics;

/// <summary>
/// Executes <see cref="SquadOrder"/>s from the operational layer: routes
/// squads to their target region, focus-fires the single best target when
/// enemies are close enough to engage, and applies the retreat/re-engage
/// hysteresis from spec invariant 3 (retreat below 0.6 local force ratio,
/// re-engage above 1.0, minimum 5 s in state) so a squad hovering near the
/// threshold does not flicker between fighting and fleeing every tick.
/// </summary>
/// <remarks>
/// <para>Siege stand-off: when an engaged order carries <see cref="SquadOrder.StandoffBufferCells"/> &gt; 0, has
/// artillery, and its target region holds a known enemy defense that can hit ground units, the squad holds at a
/// cell outside that defense's region, at least its range plus the buffer away and within the artillery's range.
/// Artillery in position bombards the defense; units not in position move there (a plain move, so they do not
/// stop to fight on the way into the defense's reach); the rest of the squad screens against mobile enemies.
/// When no such cell exists (the artillery cannot out-range the defense by the buffer, or every candidate cell is
/// water or out of ground reach) the squad assaults as usual. Siege happens only while the squad is Engaged: the
/// retreat hysteresis is evaluated first, so an outnumbered sieging squad retreats.</para>
/// This controller only ever commands units held under the lease owner
/// <c>squad:&lt;SquadId&gt;</c> for that order's <see cref="SquadOrder.SquadId"/>;
/// it never assumes it holds a unit's lease. The operational planner is the
/// only writer of those leases, so a unit this controller does not see
/// leased for its squad this tick is dropped from the command silently
/// (the plan will pick it up again once leased).
/// </remarks>
public sealed class SquadController(SquadControllerOptions options) : ITacticalController
{
    private enum CombatState { Engaged, Retreating }

    /// <summary>
    /// Hysteresis state of one squad. <see cref="Members"/> is the membership the state was last evaluated for:
    /// the planner reuses squad ids ("attack", "defend") for every wave, so a state belongs to the units that
    /// earned it, not to the id.
    /// </summary>
    private sealed class SquadCombatState
    {
        public CombatState State = CombatState.Engaged;
        public GameTime Since;
        public HashSet<EntityId> Members = [];
    }

    private readonly Dictionary<string, SquadCombatState> combatStates = [];
    private readonly Dictionary<string, RegionGraph> graphByMap = [];

    public string Id => "squad";

    public IReadOnlyList<GameCommand> Tick(BeliefSnapshot belief, IReadOnlyList<SquadOrder> squads, ILeaseManager leases, IRulesDatabase rules)
    {
        ArgumentNullException.ThrowIfNull(belief);
        ArgumentNullException.ThrowIfNull(squads);
        ArgumentNullException.ThrowIfNull(leases);
        ArgumentNullException.ThrowIfNull(rules);

        RegionGraph graph = GraphFor(belief.Map);
        Dictionary<EntityId, OwnEntity> ownById = belief.Own.ToDictionary(static e => e.Id);
        // Engagement is decided on what the squad can see now: a mobile contact that left vision seconds ago is
        // somewhere else by now (attacking it is ignored in RA2, and its value would make the squad flee a ghost).
        // Buildings do not move, so a structure stays a target for as long as its contact is fresh.
        List<EnemyContact> freshEnemies = belief.Enemies
            .Where(e => !e.ConfirmedDestroyed &&
                        e.Confidence >= options.MinConfidence &&
                        belief.Time.SecondsSince(e.LastSeenAt) <= (e.Kind == EntityKind.Building
                            ? options.MaxContactAgeSeconds
                            : Math.Min(options.MaxContactAgeSeconds, options.EngageContactAgeSeconds)))
            .ToList();
        RegionId? home = HomeRegion(belief);

        // A squad the planner no longer orders is gone: its id may come back later with other units and a new
        // objective, and must not inherit this state (a stale Retreating would abandon the next attack).
        HashSet<string> ordered = squads.Select(static s => s.SquadId).ToHashSet(StringComparer.Ordinal);
        foreach (string gone in combatStates.Keys.Where(k => !ordered.Contains(k)).ToList()) combatStates.Remove(gone);

        List<GameCommand> commands = [];
        foreach (SquadOrder order in squads.OrderBy(static s => s.SquadId, StringComparer.Ordinal))
        {
            string owner = $"squad:{order.SquadId}";
            List<EntityId> held = order.Units.Where(u => leases.OwnerOf(LeaseKey.Unit(u), belief.Time) == owner).ToList();
            if (held.Count == 0) continue;

            List<OwnEntity> members = held.Select(id => ownById.TryGetValue(id, out OwnEntity? e) ? e : null)
                .Where(static e => e is not null).Select(static e => e!).ToList();
            if (members.Count == 0) continue;

            Cell centroid = new(
                (int)Math.Round(members.Average(static m => m.Position.X)),
                (int)Math.Round(members.Average(static m => m.Position.Y)));

            List<EnemyContact> nearby = freshEnemies
                .Where(e => e.LastSeenPosition.DistanceTo(centroid) <= options.EngagementRangeCells)
                .ToList();

            if (!order.Engage)
            {
                commands.AddRange(RouteCommands(order, owner, members, graph, belief));
                continue;
            }

            // Out of contact, a retreating squad keeps retreating until it is home: routing it toward the target
            // would walk it straight back into the fight it fled, and it would bounce at the edge of engagement
            // range. Home, it is a fresh force again (the planner decides whether the attack still goes on).
            SquadCombatState? known = KnownState(order.SquadId, members);
            if (nearby.Count == 0 && known is { State: CombatState.Retreating })
            {
                if (home is not { } h || ModeRegion(members) != h)
                {
                    commands.Add(RetreatCommand(order, owner, held, belief));
                    continue;
                }
                known.State = CombatState.Engaged;
                known.Since = belief.Time;
            }

            bool siegeable = order.StandoffBufferCells > 0;
            if (nearby.Count == 0 && !siegeable)
            {
                commands.AddRange(RouteCommands(order, owner, members, graph, belief));
                continue;
            }

            // Only armed enemies oppose the squad: buildings and unarmed units are targets, not force, so a
            // squad reaching an enemy base does not read the base's worth as a reason to retreat. The hysteresis
            // runs before the siege branch, so a sieging squad retreats when outnumbered like any other.
            double ownValue = members.Sum(static m => m.Value);
            double enemyValue = nearby.Where(e => IsArmed(e, rules)).Sum(static e => e.Value);
            double localRatio = enemyValue <= 0 ? double.PositiveInfinity : ownValue / enemyValue;

            SquadCombatState state = GetOrInitState(order.SquadId, belief.Time, localRatio, order.RetreatBelowForceRatio, members);
            double heldSeconds = belief.Time.SecondsSince(state.Since);
            if (state.State == CombatState.Engaged && localRatio < order.RetreatBelowForceRatio && heldSeconds >= options.MinStateSeconds)
            {
                state.State = CombatState.Retreating;
                state.Since = belief.Time;
            }
            else if (state.State == CombatState.Retreating && localRatio > options.ReEngageAboveForceRatio && heldSeconds >= options.MinStateSeconds)
            {
                state.State = CombatState.Engaged;
                state.Since = belief.Time;
            }

            if (state.State == CombatState.Engaged && siegeable
                && Siege(order, owner, members, centroid, nearby, belief, rules, graph) is { } siege)
            {
                commands.AddRange(siege);
                continue;
            }

            if (state.State == CombatState.Engaged && nearby.Count == 0)
            {
                commands.AddRange(RouteCommands(order, owner, members, graph, belief));
                continue;
            }

            // A squad already in its home region has nowhere to retreat to: a move to its own base centre would
            // only keep it from returning fire while the base is overrun, so it fights where it stands.
            bool atHome = home is { } homeRegion && ModeRegion(members) == homeRegion;
            if (state.State == CombatState.Retreating && !atHome)
            {
                commands.Add(RetreatCommand(order, owner, held, belief));
            }
            else
            {
                if (ChooseFocusTarget(members, nearby, rules) is { } t) commands.Add(Engage(owner, held, nearby.First(e => e.Id == t), belief));
                else commands.AddRange(RouteCommands(order, owner, members, graph, belief));
            }
        }
        return commands;
    }

    /// <summary>
    /// This squad's state when it still belongs to (mostly) the same units; otherwise the state is dropped. The
    /// member set follows the squad tick by tick, so reinforcements trickling in keep the state, while a wave
    /// that shares less than half its units with the last one starts afresh.
    /// </summary>
    private SquadCombatState? KnownState(string squadId, List<OwnEntity> members)
    {
        if (!combatStates.TryGetValue(squadId, out SquadCombatState? existing)) return null;
        int kept = members.Count(m => existing.Members.Contains(m.Id));
        if (kept * 2 < members.Count)
        {
            combatStates.Remove(squadId);
            return null;
        }
        existing.Members = [.. members.Select(static m => m.Id)];
        return existing;
    }

    private SquadCombatState GetOrInitState(string squadId, GameTime now, double localRatio, double retreatBelow, List<OwnEntity> members)
    {
        if (combatStates.TryGetValue(squadId, out SquadCombatState? existing)) return existing;
        SquadCombatState state = new()
        {
            State = localRatio < retreatBelow ? CombatState.Retreating : CombatState.Engaged,
            Since = now,
            Members = [.. members.Select(static m => m.Id)],
        };
        combatStates[squadId] = state;
        return state;
    }

    /// <summary>
    /// Focus fire: the target whose death removes the most enemy damage per second of the squad's fire. An armed
    /// target scores its damage per second against the squad (its weapon × effectiveness on the members) divided
    /// by the seconds the squad needs to kill it (its remaining strength over the squad's effective damage on it),
    /// so a cheap, fragile shooter goes before a sturdy one and anything that shoots back goes before a building.
    /// Unarmed targets (structures, harvesters) score only their value per kill-second, scaled far below any
    /// shooter, so a squad razes a base only once its defenders are down.
    /// </summary>
    private static EntityId? ChooseFocusTarget(List<OwnEntity> members, List<EnemyContact> candidates, IRulesDatabase rules)
    {
        EntityId? best = null;
        double bestScore = double.NegativeInfinity;
        foreach (EnemyContact candidate in candidates.OrderBy(static c => c.Id.Value))
        {
            // Unknown types (outside the rules) count as one damage point per member and the contact's value as
            // strength, so the choice still orders by effectiveness, health and value.
            bool knownTarget = rules.TryGet(candidate.TypeId, out UnitRule targetRule);
            double squadDamage = 0;
            double threat = 0;
            foreach (OwnEntity member in members)
            {
                double memberDamage = rules.TryGet(member.TypeId, out UnitRule memberRule) ? memberRule.Damage : 1.0;
                squadDamage += memberDamage * rules.Effectiveness(member.TypeId, candidate.TypeId);
                if (knownTarget) threat += targetRule.Damage * rules.Effectiveness(candidate.TypeId, member.TypeId);
            }
            if (squadDamage <= 0) continue;
            double strength = knownTarget ? targetRule.Strength : Math.Max(1, candidate.Value);
            double remaining = Math.Max(1.0, strength * Math.Clamp(candidate.HealthFractionWhenSeen, 0.05, 1.0));
            double killSeconds = remaining / squadDamage;
            threat /= Math.Max(1, members.Count);
            double score = IsArmed(candidate, rules) && threat > 0
                ? 1_000.0 + threat / killSeconds
                : Math.Max(1, candidate.Value) / killSeconds * 1e-3;
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate.Id;
            }
        }
        return best;
    }

    /// <summary>Stand-off siege commands, or null when the order cannot siege (see the type remarks).</summary>
    private static List<GameCommand>? Siege(
        SquadOrder order, string owner, List<OwnEntity> members, Cell centroid, List<EnemyContact> nearby, BeliefSnapshot belief, IRulesDatabase rules, RegionGraph graph)
    {
        List<(OwnEntity Unit, double Range)> artillery = [];
        foreach (OwnEntity member in members)
        {
            if (rules.TryGet(member.TypeId, out UnitRule rule) && rule.Range > 0 && rule.Damage > 0
                && (rule.Role == UnitRole.Artillery || rule.Weapon == WeaponClass.Artillery))
            {
                artillery.Add((member, rule.Range));
            }
        }
        if (artillery.Count == 0) return null;

        EnemyContact? defense = belief.Enemies
            .Where(e => !e.ConfirmedDestroyed && e.Kind == EntityKind.Building && e.LastSeenRegion == order.TargetRegion
                && rules.TryGet(e.TypeId, out UnitRule r) && r.Damage > 0 && r.Range > 0 && r.Weapon is not (WeaponClass.None or WeaponClass.AntiAir))
            .OrderBy(e => e.LastSeenPosition.DistanceTo(centroid)).ThenBy(static e => e.Id.Value)
            .FirstOrDefault();
        if (defense is null) return null;

        double defenseRange = rules.Get(defense.TypeId).Range;
        double reach = artillery.Min(static a => a.Range);
        double need = defenseRange + order.StandoffBufferCells;
        if (need > reach) return null;
        if (StandoffCell(defense, centroid, need, reach, belief.Map, graph, ModeRegion(members)) is not { } standoff) return null;

        List<EntityId> bombard = [];
        List<EntityId> reposition = [];
        HashSet<EntityId> artilleryIds = [.. artillery.Select(static a => a.Unit.Id)];
        foreach ((OwnEntity unit, double range) in artillery)
        {
            double distance = unit.Position.DistanceTo(defense.LastSeenPosition);
            if (distance <= range && distance > defenseRange) bombard.Add(unit.Id);
            else reposition.Add(unit.Id);
        }

        List<OwnEntity> screen = members.Where(m => !artilleryIds.Contains(m.Id)).ToList();
        List<EnemyContact> mobileThreats = nearby.Where(e => e.Kind != EntityKind.Building && IsArmed(e, rules)).ToList();
        List<GameCommand> commands = [];
        if (screen.Count > 0 && mobileThreats.Count > 0 && ChooseFocusTarget(screen, mobileThreats, rules) is { } threat)
        {
            commands.Add(Engage(owner, [.. screen.Select(static m => m.Id)], mobileThreats.First(e => e.Id == threat), belief));
        }
        else
        {
            reposition.AddRange(screen.Where(m => m.Position.DistanceTo(standoff) > 2).Select(static m => m.Id));
        }
        if (reposition.Count > 0) commands.Add(new MoveCommand(owner, [.. reposition.OrderBy(static u => u.Value)], standoff));
        // A defence out of sight cannot be targeted; the artillery holds its stand-off cell (an attack-move there fires
        // on whatever comes into view) until something shows the defence again.
        if (bombard.Count > 0)
        {
            commands.Add(VisibleNow(defense, belief) ? new AttackCommand(owner, bombard, defense.Id) : new AttackMoveCommand(owner, bombard, standoff));
        }
        return commands;
    }

    /// <summary>Whether the contact was seen in the current frame, the only time RA2 (and the simulator) accept an attack order on it.</summary>
    private static bool VisibleNow(EnemyContact contact, BeliefSnapshot belief) => contact.LastSeenAt == belief.Time;

    /// <summary>
    /// Attacks a contact the player sees now. One that is still fresh but out of sight (a building seen seconds ago)
    /// gets an attack-move to where it was seen: an attack order on it would be refused as an invalid command every
    /// tick, while the attack-move walks the squad there and engages it the moment it is visible.
    /// </summary>
    private static GameCommand Engage(string owner, IReadOnlyList<EntityId> units, EnemyContact target, BeliefSnapshot belief) =>
        VisibleNow(target, belief)
            ? new AttackCommand(owner, units, target.Id)
            : new AttackMoveCommand(owner, units, target.LastSeenPosition);

    /// <summary>
    /// The first cell on the line from the defense toward the squad, at a distance in [need, reach], that lies in a
    /// region other than the defense's (same-region units always trade fire), on the map, on land, and in a region
    /// the squad can reach by ground (a move with no ground path is dropped, and the squad would stand idle for as
    /// long as the order lasts); null when none does.
    /// </summary>
    private static Cell? StandoffCell(EnemyContact defense, Cell centroid, double need, double reach, MapInfo map, RegionGraph graph, RegionId from)
    {
        double dx = centroid.X - defense.LastSeenPosition.X, dy = centroid.Y - defense.LastSeenPosition.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-9) return null;
        dx /= length;
        dy /= length;
        for (double d = need; d <= reach + 1e-9; d += 0.5)
        {
            Cell cell = new((int)Math.Round(defense.LastSeenPosition.X + dx * d), (int)Math.Round(defense.LastSeenPosition.Y + dy * d));
            double actual = cell.DistanceTo(defense.LastSeenPosition);
            if (actual < need || actual > reach) continue;
            if (cell.X < 0 || cell.Y < 0 || cell.X >= map.Width || cell.Y >= map.Height) continue;
            if (map.RegionOf(cell) is { } region && region.Id != defense.LastSeenRegion && !region.Water
                && (region.Id == from || graph.Path(from, region.Id).Count > 0))
            {
                return cell;
            }
        }
        return null;
    }

    private static bool IsArmed(EnemyContact contact, IRulesDatabase rules) =>
        rules.TryGet(contact.TypeId, out UnitRule rule) && rule.Weapon != WeaponClass.None && rule.Damage > 0;

    /// <summary>
    /// Toward the next waypoint, one command per region the members stand in: an attack-move for an engaging
    /// order, a plain move for one that does not engage (an attack-move stops to fight in any region with an enemy
    /// in it, so a squad heading home, a scout or a retreat could never leave one). Each group routes from its own
    /// region, so fresh units at base do not drag the front line back to the base's first waypoint (as routing the
    /// whole squad from its most populated region did). A group with no ground route to the target gets nothing.
    /// </summary>
    private static List<GameCommand> RouteCommands(SquadOrder order, string owner, List<OwnEntity> members, RegionGraph graph, BeliefSnapshot belief)
    {
        List<GameCommand> commands = [];
        foreach (IGrouping<RegionId, OwnEntity> group in members.GroupBy(static m => m.Region).OrderBy(static g => g.Key.Value))
        {
            RegionId from = group.Key;
            if (from != order.TargetRegion && graph.Path(from, order.TargetRegion).Count == 0) continue;
            Cell destination = NextWaypoint(from, order.TargetRegion, graph, belief.Map);
            List<EntityId> units = [.. group.Select(static m => m.Id).OrderBy(static u => u.Value)];
            commands.Add(order.Engage ? new AttackMoveCommand(owner, units, destination) : new MoveCommand(owner, units, destination));
        }
        return commands;
    }

    /// <summary>The region of the lowest-id region holding an own building (the retreat destination), if any.</summary>
    private static RegionId? HomeRegion(BeliefSnapshot belief) =>
        belief.Own.Where(static e => e.Kind == EntityKind.Building)
            .Select(static e => (RegionId?)e.Region)
            .OrderBy(static r => r!.Value.Value)
            .FirstOrDefault();

    private static GameCommand RetreatCommand(SquadOrder order, string owner, List<EntityId> held, BeliefSnapshot belief)
    {
        RegionId home = HomeRegion(belief) ?? order.TargetRegion;
        Cell fallback = belief.Map.Regions.Count > 0 ? belief.Map.Regions[0].Center : new Cell(0, 0);
        Cell destination = belief.Map.Regions.FirstOrDefault(r => r.Id == home)?.Center ?? fallback;
        return new MoveCommand(owner, held, destination);
    }

    private static RegionId ModeRegion(List<OwnEntity> members) =>
        members.GroupBy(static m => m.Region).OrderByDescending(static g => g.Count()).ThenBy(static g => g.Key.Value).First().Key;

    private static Cell NextWaypoint(RegionId from, RegionId to, RegionGraph graph, MapInfo map)
    {
        IReadOnlyList<RegionId> path = graph.Path(from, to);
        RegionId waypoint = path.Count > 1 ? path[1] : to;
        Cell fallback = map.Regions.Count > 0 ? map.Regions[0].Center : new Cell(0, 0);
        return map.Regions.FirstOrDefault(r => r.Id == waypoint)?.Center ?? fallback;
    }

    private RegionGraph GraphFor(MapInfo map)
    {
        if (graphByMap.TryGetValue(map.MapId, out RegionGraph? existing)) return existing;
        RegionGraph graph = new(map);
        graphByMap[map.MapId] = graph;
        return graph;
    }
}
