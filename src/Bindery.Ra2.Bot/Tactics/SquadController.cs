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

    private sealed class SquadCombatState
    {
        public CombatState State = CombatState.Engaged;
        public GameTime Since;
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
        List<EnemyContact> freshEnemies = belief.Enemies
            .Where(e => !e.ConfirmedDestroyed &&
                        e.Confidence >= options.MinConfidence &&
                        belief.Time.SecondsSince(e.LastSeenAt) <= options.MaxContactAgeSeconds)
            .ToList();

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

            if (!order.Engage || nearby.Count == 0)
            {
                commands.Add(RouteCommand(order, owner, held, members, centroid, graph, belief));
                continue;
            }

            // Only armed enemies oppose the squad: buildings and unarmed units are targets, not force, so a
            // squad reaching an enemy base does not read the base's worth as a reason to retreat.
            double ownValue = members.Sum(static m => m.Value);
            double enemyValue = nearby.Where(e => IsArmed(e, rules)).Sum(static e => e.Value);
            double localRatio = enemyValue <= 0 ? double.PositiveInfinity : ownValue / enemyValue;

            SquadCombatState state = GetOrInitState(order.SquadId, belief.Time, localRatio, order.RetreatBelowForceRatio);
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

            if (state.State == CombatState.Retreating)
            {
                commands.Add(RetreatCommand(order, owner, held, belief, graph));
            }
            else
            {
                EntityId? target = ChooseFocusTarget(members, nearby, rules);
                commands.Add(target is { } t
                    ? new AttackCommand(owner, held, t)
                    : RouteCommand(order, owner, held, members, centroid, graph, belief));
            }
        }
        return commands;
    }

    private SquadCombatState GetOrInitState(string squadId, GameTime now, double localRatio, double retreatBelow)
    {
        if (combatStates.TryGetValue(squadId, out SquadCombatState? existing)) return existing;
        SquadCombatState state = new()
        {
            State = localRatio < retreatBelow ? CombatState.Retreating : CombatState.Engaged,
            Since = now,
        };
        combatStates[squadId] = state;
        return state;
    }

    /// <summary>
    /// Best target by average, over squad members, of
    /// effectiveness × (1 - health fraction) × value; one target per squad per tick.
    /// </summary>
    private static EntityId? ChooseFocusTarget(List<OwnEntity> members, List<EnemyContact> candidates, IRulesDatabase rules)
    {
        EntityId? best = null;
        double bestScore = double.NegativeInfinity;
        foreach (EnemyContact candidate in candidates.OrderBy(static c => c.Id.Value))
        {
            double effectiveness = 0;
            int count = 0;
            foreach (OwnEntity member in members)
            {
                effectiveness += rules.Effectiveness(member.TypeId, candidate.TypeId);
                count++;
            }
            double avgEffectiveness = count > 0 ? effectiveness / count : 1.0;
            // Low health first, but a full-health target still scores (the 1.25 floor), and armed targets
            // outrank unarmed ones so the squad kills what shoots back before it razes buildings.
            double threat = IsArmed(candidate, rules) ? 3.0 : 1.0;
            double score = avgEffectiveness * (1.25 - candidate.HealthFractionWhenSeen) * Math.Max(1, candidate.Value) * threat;
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate.Id;
            }
        }
        return best;
    }

    private static bool IsArmed(EnemyContact contact, IRulesDatabase rules) =>
        rules.TryGet(contact.TypeId, out UnitRule rule) && rule.Weapon != WeaponClass.None && rule.Damage > 0;

    private GameCommand RouteCommand(
        SquadOrder order,
        string owner,
        List<EntityId> held,
        List<OwnEntity> members,
        Cell centroid,
        RegionGraph graph,
        BeliefSnapshot belief)
    {
        RegionId currentRegion = ModeRegion(members);
        Cell destination = NextWaypoint(currentRegion, order.TargetRegion, graph, belief.Map);
        return new AttackMoveCommand(owner, held, destination);
    }

    private GameCommand RetreatCommand(SquadOrder order, string owner, List<EntityId> held, BeliefSnapshot belief, RegionGraph graph)
    {
        RegionId home = belief.Own.Where(static e => e.Kind == EntityKind.Building)
            .Select(static e => e.Region)
            .OrderBy(static r => r.Value)
            .DefaultIfEmpty(order.TargetRegion)
            .First();
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
