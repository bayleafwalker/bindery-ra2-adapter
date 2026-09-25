// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Operations;

/// <summary>
/// Turns one <see cref="StrategicIntent"/> into production, economy, tech,
/// defense and squad decisions once a second (or sooner, on a severe
/// <see cref="StrategicEvent"/>). It never touches unit IDs directly except
/// to hand them to squads via leases: everything it decides is either a
/// production/placement command or a <see cref="SquadOrder"/> that the
/// tactical layer executes.
/// </summary>
/// <remarks>
/// Decisions the spec leaves open, and the convention chosen here:
/// <list type="bullet">
/// <item>Building anchor: the centroid of own buildings (there is no
/// distinct "construction yard" <see cref="UnitRole"/> in the contracts),
/// so placement spirals outward from the base's own centre of mass.</item>
/// <item>Budget reservations are informational per-pool spend for this tick
/// only (production cost actually queued); the ledger that enforces the
/// budget across ticks belongs to the Arbitration package.</item>
/// <item>Squad membership persists across calls in this instance: new
/// combat units are folded into existing under-strength squads before new
/// squads are formed, so a standing army does not fragment every tick.</item>
/// </list>
/// </remarks>
public sealed class OperationalPlanner : IOperationalPlanner
{
    private readonly IRulesDatabase rules;
    private readonly IPlaybookLibrary playbooks;
    private readonly OperationalOptions options;
    private readonly Dictionary<string, SquadState> squads = [];
    private readonly Dictionary<string, RegionGraph> graphByMap = [];
    private int nextScoutId;
    private int nextHarassId;

    public OperationalPlanner(IRulesDatabase rules, IPlaybookLibrary playbooks, OperationalOptions options)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(playbooks);
        ArgumentNullException.ThrowIfNull(options);
        this.rules = rules;
        this.playbooks = playbooks;
        this.options = options;
    }

    public OperationalPlan Plan(BeliefSnapshot belief, StrategicFeatures features, StrategicIntent intent, ILeaseManager leases)
    {
        ArgumentNullException.ThrowIfNull(belief);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(leases);

        List<string> notes = [];
        Dictionary<string, int> reservations = [];
        List<GameCommand> commands = [];
        IReadOnlySet<string> ownBuildingTypes = belief.OwnBuildingTypes;
        RegionGraph graph = GraphFor(belief.Map);
        playbooks.TryGet(intent.PlaybookId, out Playbook playbook);

        PlanProduction(belief, features, intent, playbook, ownBuildingTypes, commands, reservations, notes);
        PlanPlacement(belief, features, commands, notes);
        List<SquadOrder> squadOrders = PlanSquads(belief, features, intent, graph, leases, notes);

        SortedDictionary<string, int> sortedReservations = new(reservations, StringComparer.Ordinal);
        return new OperationalPlan(belief.Time, intent.IntentId, commands, squadOrders, sortedReservations, notes);
    }

    private RegionGraph GraphFor(MapInfo map)
    {
        if (graphByMap.TryGetValue(map.MapId, out RegionGraph? existing)) return existing;
        RegionGraph graph = new(map);
        graphByMap[map.MapId] = graph;
        return graph;
    }

    // ----- Production: economy, power, tech, defense, composition -----

    private void PlanProduction(
        BeliefSnapshot belief,
        StrategicFeatures features,
        StrategicIntent intent,
        Playbook? playbook,
        IReadOnlySet<string> ownBuildingTypes,
        List<GameCommand> commands,
        Dictionary<string, int> reservations,
        List<string> notes)
    {
        Dictionary<QueueKind, ProductionQueueState> queues = [];
        foreach (ProductionQueueState q in belief.Queues) queues[q.Kind] = q;

        bool HasRoom(QueueKind kind) =>
            queues.TryGetValue(kind, out ProductionQueueState? q) && q.Factories > 0 && !q.Items.Any(static i => !i.Ready);

        void Reserve(string pool, int cost) => reservations[pool] = reservations.GetValueOrDefault(pool) + cost;

        bool CanQueue(string typeId) => rules.TryGet(typeId, out UnitRule rule) &&
            rules.CanBuild(belief.Faction, ownBuildingTypes, typeId) &&
            rule.Factions.Contains(belief.Faction);

        // -- Building queue: power-first, then refinery/tech by budget share. --
        if (HasRoom(QueueKind.Building))
        {
            string? candidate = ChooseBuildingCandidate(belief, features, intent, playbook, ownBuildingTypes, out string candidatePool);
            if (candidate is not null && CanQueue(candidate))
            {
                if (rules.TryGet(candidate, out UnitRule candidateRule) &&
                    belief.Power.Surplus + candidateRule.Power < 0 &&
                    TryFindPowerType(belief.Faction, ownBuildingTypes, out string powerType) &&
                    powerType != candidate)
                {
                    commands.Add(new ProduceCommand(options.ControllerId, powerType, QueueKind.Building));
                    Reserve("economy", rules.Get(powerType).Cost);
                    notes.Add($"power-first: {candidate} needs {-candidateRule.Power} but surplus is {belief.Power.Surplus}; queued {powerType} instead");
                }
                else
                {
                    commands.Add(new ProduceCommand(options.ControllerId, candidate, QueueKind.Building));
                    Reserve(candidatePool, rules.Get(candidate).Cost);
                    notes.Add($"building: queued {candidate} ({candidatePool})");
                }
            }
            else if (belief.Power.LowPower && TryFindPowerType(belief.Faction, ownBuildingTypes, out string emergencyPower) && CanQueue(emergencyPower))
            {
                commands.Add(new ProduceCommand(options.ControllerId, emergencyPower, QueueKind.Building));
                Reserve("economy", rules.Get(emergencyPower).Cost);
                notes.Add($"power-first: low power with no other building need; queued {emergencyPower}");
            }
        }

        // -- Defense queue: buy defenses at threatened base regions. --
        if (intent.Budget.Defense > 0 && HasRoom(QueueKind.Defense))
        {
            bool threatened = features.Threats.Any(static t => t.IsBase && t.LocalForceRatio < 1.0);
            if (threatened)
            {
                UnitRule? best = BestBuildable(QueueKind.Defense, UnitRole.Defense, belief.Faction, ownBuildingTypes, features);
                if (best is { } defenseRule)
                {
                    commands.Add(new ProduceCommand(options.ControllerId, defenseRule.TypeId, QueueKind.Defense));
                    Reserve("defense", defenseRule.Cost);
                    notes.Add($"defense: base threatened, queued {defenseRule.TypeId}");
                }
            }
        }

        // -- Vehicle queue: harvesters first, then composition gap. --
        if (HasRoom(QueueKind.Vehicle))
        {
            int refineries = features.Economy.Refineries;
            double target = playbook is not null && intent.PlaybookParameters.TryGetValue("harvesterTarget", out double t)
                ? t
                : Math.Max(1, refineries) * options.DefaultHarvesterTargetPerRefinery;
            if (features.Economy.Harvesters < target)
            {
                UnitRule? harvester = BestBuildable(QueueKind.Vehicle, UnitRole.Harvester, belief.Faction, ownBuildingTypes, features);
                if (harvester is { } h)
                {
                    commands.Add(new ProduceCommand(options.ControllerId, h.TypeId, QueueKind.Vehicle));
                    Reserve("economy", h.Cost);
                    notes.Add($"economy: {features.Economy.Harvesters}/{target:0.#} harvesters, queued {h.TypeId}");
                }
            }
            else if (intent.Budget.Army > 0)
            {
                QueueComposition(QueueKind.Vehicle, belief, features, intent, commands, reservations, notes);
            }
        }

        // -- Infantry / Aircraft / Naval queues: composition gap only. --
        foreach (QueueKind kind in new[] { QueueKind.Infantry, QueueKind.Aircraft, QueueKind.Naval })
        {
            if (intent.Budget.Army > 0 && HasRoom(kind))
            {
                QueueComposition(kind, belief, features, intent, commands, reservations, notes);
            }
        }
    }

    private void QueueComposition(
        QueueKind kind,
        BeliefSnapshot belief,
        StrategicFeatures features,
        StrategicIntent intent,
        List<GameCommand> commands,
        Dictionary<string, int> reservations,
        List<string> notes)
    {
        (UnitRole Role, double Gap)? gap = BestGapForKind(kind, intent, features, belief.Faction, belief.OwnBuildingTypes);
        if (gap is not { } g || g.Gap <= 0) return;
        UnitRule? best = BestBuildable(kind, g.Role, belief.Faction, belief.OwnBuildingTypes, features);
        if (best is not { } rule) return;
        commands.Add(new ProduceCommand(options.ControllerId, rule.TypeId, kind));
        reservations["army"] = reservations.GetValueOrDefault("army") + rule.Cost;
        notes.Add($"composition: {g.Role} gap {g.Gap:0.00}, queued {rule.TypeId} in {kind}");
    }

    private (UnitRole Role, double Gap)? BestGapForKind(
        QueueKind kind,
        StrategicIntent intent,
        StrategicFeatures features,
        Faction faction,
        IReadOnlySet<string> ownBuildingTypes)
    {
        double total = features.Army.ValueByRole.Values.Sum();
        (UnitRole Role, double Gap)? best = null;
        foreach (CompositionTarget target in intent.Composition.OrderBy(static c => c.Role.ToString(), StringComparer.Ordinal))
        {
            bool buildable = rules.All.Any(r => r.Role == target.Role && r.Queue == kind && r.Factions.Contains(faction) &&
                rules.CanBuild(faction, ownBuildingTypes, r.TypeId));
            if (!buildable) continue;
            double current = total > 0 ? features.Army.ValueByRole.GetValueOrDefault(target.Role) / total : 0.0;
            double gap = target.MinShare - current;
            if (best is null || gap > best.Value.Gap) best = (target.Role, gap);
        }
        return best;
    }

    private UnitRule? BestBuildable(
        QueueKind kind,
        UnitRole role,
        Faction faction,
        IReadOnlySet<string> ownBuildingTypes,
        StrategicFeatures features)
    {
        UnitRule? best = null;
        double bestScore = double.NegativeInfinity;
        foreach (UnitRule rule in rules.All.OrderBy(static r => r.TypeId, StringComparer.Ordinal))
        {
            if (rule.Role != role || rule.Queue != kind) continue;
            if (!rule.Factions.Contains(faction)) continue;
            if (!rules.CanBuild(faction, ownBuildingTypes, rule.TypeId)) continue;
            double effectiveness = EffectivenessAgainstEnemy(rule, features);
            double cost = Math.Max(1, rule.Cost) + rule.BuildSeconds * 10.0;
            double score = effectiveness * 1000.0 / cost;
            if (score > bestScore)
            {
                bestScore = score;
                best = rule;
            }
        }
        return best;
    }

    private double EffectivenessAgainstEnemy(UnitRule candidate, StrategicFeatures features)
    {
        if (features.Enemy.KnownTech.Count == 0) return 1.0;
        double sum = 0, weight = 0;
        foreach (string typeId in features.Enemy.KnownTech.OrderBy(static t => t, StringComparer.Ordinal))
        {
            if (!rules.TryGet(typeId, out UnitRule enemyRule)) continue;
            double w = features.Enemy.CompositionByRole.TryGetValue(enemyRule.Role, out double cw) ? Math.Max(cw, 0.05) : 0.05;
            sum += rules.Effectiveness(candidate.TypeId, typeId) * w;
            weight += w;
        }
        return weight > 0 ? sum / weight : 1.0;
    }

    private string? ChooseBuildingCandidate(
        BeliefSnapshot belief,
        StrategicFeatures features,
        StrategicIntent intent,
        Playbook? playbook,
        IReadOnlySet<string> ownBuildingTypes,
        out string pool)
    {
        int oreRegionsControlled = features.MapControl.Control
            .Count(kv => kv.Value == RegionControl.Own && belief.Map.OreFields.Any(o => o.Region == kv.Key));
        int refineryTarget = Math.Max(1, oreRegionsControlled);
        string? refineryCandidate = features.Economy.Refineries < refineryTarget
            ? FindBuildingType(belief.Faction, UnitRole.Economy, ownBuildingTypes)
            : null;

        string? techCandidate = null;
        if (playbook is not null)
        {
            foreach (string goal in playbook.TechGoals)
            {
                if (ownBuildingTypes.Contains(goal)) continue; // already reached
                IReadOnlyList<string>? path = rules.PathTo(belief.Faction, ownBuildingTypes, goal);
                if (path is null) continue; // unreachable for this faction
                techCandidate = path.Count > 0 ? path[0] : goal; // first missing prerequisite, or the goal itself when directly buildable
                break;
            }
        }

        bool preferEconomy = intent.Budget.Economy >= intent.Budget.Tech;
        if (preferEconomy && refineryCandidate is not null) { pool = "economy"; return refineryCandidate; }
        if (intent.Budget.Tech > 0 && techCandidate is not null) { pool = "tech"; return techCandidate; }
        if (refineryCandidate is not null) { pool = "economy"; return refineryCandidate; }
        pool = "economy";
        return null;
    }

    private string? FindBuildingType(Faction faction, UnitRole role, IReadOnlySet<string> ownBuildingTypes) =>
        rules.All.OrderBy(static r => r.TypeId, StringComparer.Ordinal)
            .FirstOrDefault(r => r.Role == role && r.Queue == QueueKind.Building && r.Factions.Contains(faction) &&
                rules.CanBuild(faction, ownBuildingTypes, r.TypeId))?.TypeId;

    private bool TryFindPowerType(Faction faction, IReadOnlySet<string> ownBuildingTypes, out string typeId)
    {
        UnitRule? rule = rules.All.OrderBy(static r => r.TypeId, StringComparer.Ordinal)
            .FirstOrDefault(r => r.Queue == QueueKind.Building && r.Power > 0 && r.Factions.Contains(faction) &&
                rules.CanBuild(faction, ownBuildingTypes, r.TypeId));
        typeId = rule?.TypeId ?? string.Empty;
        return rule is not null;
    }

    // ----- Placement: place any ready building/defense item. -----

    private void PlanPlacement(BeliefSnapshot belief, StrategicFeatures features, List<GameCommand> commands, List<string> notes)
    {
        List<Cell> ownBuildings = belief.Own.Where(static e => e.Kind == EntityKind.Building).Select(static e => e.Position).ToList();
        if (ownBuildings.Count == 0) return;
        Cell anchor = new(
            (int)Math.Round(ownBuildings.Average(static c => c.X)),
            (int)Math.Round(ownBuildings.Average(static c => c.Y)));

        foreach (QueueKind kind in new[] { QueueKind.Building, QueueKind.Defense })
        {
            ProductionQueueState? queue = belief.Queues.FirstOrDefault(q => q.Kind == kind);
            QueueItem? ready = queue?.Items.FirstOrDefault(static i => i.Ready);
            if (ready is not { } item) continue;

            Cell? bias = null;
            if (rules.TryGet(item.TypeId, out UnitRule rule) && rule.Role == UnitRole.Economy)
            {
                bias = belief.Map.OreFields
                    .OrderBy(o => o.Center.DistanceTo(anchor))
                    .Select(static o => (Cell?)o.Center)
                    .FirstOrDefault();
            }
            else if (kind == QueueKind.Defense)
            {
                ThreatAssessment? worst = features.Threats.Where(static t => t.IsBase)
                    .OrderBy(static t => t.LocalForceRatio).FirstOrDefault();
                if (worst is not null)
                {
                    Region? region = belief.Map.Regions.FirstOrDefault(r => r.Id == worst.Region);
                    if (region is not null) bias = region.Center;
                }
            }

            Cell chosen = BuildPlacement.ChooseCell(anchor, bias, ownBuildings, belief.Map.Width, belief.Map.Height, options.BuildSearchRings, options.BuildGridStep);
            commands.Add(new PlaceBuildingCommand(options.ControllerId, item.TypeId, chosen));
            notes.Add($"placement: {item.TypeId} at {chosen}");
        }
    }

    // ----- Squads: form, assign objectives, reinforce, lease units. -----

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
        HashSet<EntityId> assigned = squads.Values.SelectMany(static s => s.Units).ToHashSet();
        List<OwnEntity> combatPool = belief.Own
            .Where(e => e.Kind != EntityKind.Building &&
                        e.Role is not (UnitRole.Harvester or UnitRole.Mcv or UnitRole.Engineer) &&
                        !assigned.Contains(e.Id))
            .OrderBy(static e => e.Id.Value)
            .ToList();

        double retreatRatio = intent.PlaybookParameters.TryGetValue("retreatBelowForceRatio", out double rr)
            ? rr
            : options.DefaultRetreatBelowForceRatio;

        foreach (Objective objective in intent.Objectives.OrderBy(static o => o.Priority))
        {
            switch (objective.Kind)
            {
                case ObjectiveKind.DefendRegion when objective.Region is { } defendRegion:
                    AssignDefend(defendRegion, combatPool);
                    break;
                case ObjectiveKind.Retreat:
                    AssignRetreat(objective.Region, belief, combatPool);
                    break;
                case ObjectiveKind.AttackRegion when objective.Region is { } attackRegion:
                    AssignAttack(attackRegion, belief, features, intent, graph, combatPool, retreatRatio, notes);
                    break;
                case ObjectiveKind.Scout:
                    AssignScout(combatPool);
                    break;
                case ObjectiveKind.Harass:
                    AssignHarass(objective.Region, combatPool, retreatRatio);
                    break;
                case ObjectiveKind.DenyExpansion when objective.Region is { } denyRegion:
                    AssignDefend(denyRegion, combatPool);
                    break;
            }
        }

        Reinforce(combatPool);

        List<string> stale = squads.Where(static kv => kv.Value.Units.Count == 0).Select(static kv => kv.Key).ToList();
        foreach (string id in stale) squads.Remove(id);

        List<SquadOrder> orders = [];
        foreach (SquadState squad in squads.Values.OrderBy(static s => s.SquadId, StringComparer.Ordinal))
        {
            List<EntityId> held = [];
            foreach (EntityId unit in squad.Units.OrderBy(static u => u.Value))
            {
                string owner = $"squad:{squad.SquadId}";
                LeaseKey key = LeaseKey.Unit(unit);
                string? currentOwner = leases.OwnerOf(key, belief.Time);
                bool ok = currentOwner == owner
                    ? leases.Renew(key, owner, belief.Time, options.SquadLeaseTtlSeconds)
                    : leases.TryAcquire(key, owner, options.SquadLeasePriority, belief.Time, options.SquadLeaseMinHoldSeconds, options.SquadLeaseTtlSeconds) is not null;
                if (ok) held.Add(unit);
            }
            squad.Units.RemoveAll(u => !held.Contains(u));
            if (held.Count == 0) continue;
            orders.Add(new SquadOrder(squad.SquadId, squad.Objective, squad.TargetRegion, held, squad.Engage, retreatRatio));
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

    private void AssignDefend(RegionId region, List<OwnEntity> pool)
    {
        SquadState squad = GetOrCreate($"defend-{region.Value}", ObjectiveKind.DefendRegion, region, engage: true);
        List<OwnEntity> nearby = pool.Where(e => e.Region == region).ToList();
        foreach (OwnEntity unit in nearby)
        {
            squad.Units.Add(unit.Id);
            pool.Remove(unit);
        }
    }

    private void AssignRetreat(RegionId? fallback, BeliefSnapshot belief, List<OwnEntity> pool)
    {
        RegionId target = fallback ?? belief.Own.Where(static e => e.Kind == EntityKind.Building)
            .Select(static e => e.Region).DefaultIfEmpty(default).First();
        SquadState squad = GetOrCreate("retreat", ObjectiveKind.Retreat, target, engage: false);
        foreach (SquadState other in squads.Values)
        {
            if (other.SquadId == squad.SquadId) continue;
            other.Objective = ObjectiveKind.Retreat;
            other.TargetRegion = target;
            other.Engage = false;
        }
        foreach (OwnEntity unit in pool.ToList())
        {
            squad.Units.Add(unit.Id);
            pool.Remove(unit);
        }
    }

    private void AssignAttack(
        RegionId target,
        BeliefSnapshot belief,
        StrategicFeatures features,
        StrategicIntent intent,
        RegionGraph graph,
        List<OwnEntity> pool,
        double retreatRatio,
        List<string> notes)
    {
        bool conditionsHold = intent.AttackConditions.Count == 0 ||
            intent.AttackConditions.All(c => options.Evaluator(c, features));

        if (!conditionsHold)
        {
            RegionId rally = NearestOwnRegion(belief, graph, target);
            SquadState staging = GetOrCreate($"attack-{target.Value}", ObjectiveKind.AttackRegion, rally, engage: false);
            notes.Add($"attack {target}: conditions not met, staging at {rally}");
            foreach (OwnEntity unit in pool.ToList())
            {
                staging.Units.Add(unit.Id);
                pool.Remove(unit);
            }
            return;
        }

        SquadState squad = GetOrCreate($"attack-{target.Value}", ObjectiveKind.AttackRegion, target, engage: true);
        notes.Add($"attack {target}: conditions met");
        foreach (OwnEntity unit in pool.ToList())
        {
            squad.Units.Add(unit.Id);
            pool.Remove(unit);
        }
    }

    private RegionId NearestOwnRegion(BeliefSnapshot belief, RegionGraph graph, RegionId target)
    {
        RegionId? best = null;
        double bestDistance = double.MaxValue;
        foreach (OwnEntity building in belief.Own.Where(static e => e.Kind == EntityKind.Building))
        {
            double d = graph.Distance(building.Region, target);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = building.Region;
            }
        }
        return best ?? target;
    }

    private void AssignScout(List<OwnEntity> pool)
    {
        if (pool.Count == 0) return;
        OwnEntity? candidate = pool.OrderBy(static e => e.Value).ThenBy(static e => e.Id.Value).FirstOrDefault();
        if (candidate is null) return;
        string id = $"scout-{nextScoutId++}";
        SquadState squad = GetOrCreate(id, ObjectiveKind.Scout, candidate.Region, engage: false);
        squad.Units.Add(candidate.Id);
        pool.Remove(candidate);
    }

    private void AssignHarass(RegionId? region, List<OwnEntity> pool, double retreatRatio)
    {
        if (pool.Count == 0) return;
        List<OwnEntity> chosen = pool.OrderBy(static e => e.Value).ThenBy(static e => e.Id.Value)
            .Take(options.HarassSquadSize).ToList();
        if (chosen.Count == 0) return;
        RegionId target = region ?? chosen[0].Region;
        string id = $"harass-{nextHarassId++}";
        SquadState squad = GetOrCreate(id, ObjectiveKind.Harass, target, engage: true);
        foreach (OwnEntity unit in chosen)
        {
            squad.Units.Add(unit.Id);
            pool.Remove(unit);
        }
    }

    private void Reinforce(List<OwnEntity> pool)
    {
        foreach (OwnEntity unit in pool.ToList())
        {
            SquadState? target = squads.Values
                .Where(s => s.Units.Count < options.ReinforceSquadTargetSize && s.SquadId != "retreat")
                .OrderBy(static s => s.Units.Count)
                .ThenBy(static s => s.SquadId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (target is null) continue;
            target.Units.Add(unit.Id);
            pool.Remove(unit);
        }
    }
}
