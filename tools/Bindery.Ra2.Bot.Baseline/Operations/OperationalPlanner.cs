// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Baseline.Arbitration;

namespace Bindery.Ra2.Bot.Baseline.Operations;

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
/// <item>A queue with no reported state is empty, and has a factory when the
/// rules say at least one of its types is buildable now; a queue holds one item
/// at a time (the building queue may also hold one finished building awaiting
/// placement).</item>
/// <item>Nothing is queued that the credits on hand (less what this pass already
/// queued) cannot pay for; the ledger then enforces the intent's shares.</item>
/// <item>Budget reservations are this pass's queued cost per pool, pool chosen by
/// <see cref="BudgetPools.ForRole"/> of the item's role; the runtime reserves them in
/// the ledger and the command gate spends them.</item>
/// <item>Zero-cost buildings are deploy products (the construction yard an MCV
/// becomes) and are never queued.</item>
/// <item>Squad membership persists across calls in this instance (see the squad
/// half of this class), so the integrator must keep one planner per match.</item>
/// </list>
/// </remarks>
public sealed partial class OperationalPlanner : IOperationalPlanner
{
    private readonly IRulesDatabase rules;
    private readonly IPlaybookLibrary playbooks;
    private readonly OperationalOptions options;
    private readonly Dictionary<string, RegionGraph> graphByMap = [];

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

        RegionGraph graph = GraphFor(belief.Map);
        Playbook? playbook = playbooks.TryGet(intent.PlaybookId, out Playbook found) ? found : null;
        ProductionPass pass = new(this, belief, features, intent, playbook);

        pass.Run();
        PlanPlacement(belief, features, pass.Commands, pass.Notes);
        List<SquadOrder> squadOrders = PlanSquads(belief, features, intent, graph, leases, pass.Notes);

        SortedDictionary<string, int> sortedReservations = new(pass.Reservations, StringComparer.Ordinal);
        return new OperationalPlan(belief.Time, intent.IntentId, pass.Commands, squadOrders, sortedReservations, pass.Notes);
    }

    private RegionGraph GraphFor(MapInfo map)
    {
        if (graphByMap.TryGetValue(map.MapId, out RegionGraph? existing)) return existing;
        RegionGraph graph = new(map);
        graphByMap[map.MapId] = graph;
        return graph;
    }

    // ----- Production: economy, power, tech, defense, composition -----

    private sealed class ProductionPass(OperationalPlanner planner, BeliefSnapshot belief, StrategicFeatures features, StrategicIntent intent, Playbook? playbook)
    {
        private readonly IRulesDatabase rules = planner.rules;
        private readonly OperationalOptions options = planner.options;
        // Buildings already in a queue (building or finished and awaiting placement) count as owned for
        // planning, or a finished-but-unplaced prerequisite would be queued a second time.
        private readonly IReadOnlySet<string> own = belief.OwnBuildingTypes
            .Concat(belief.Queues.Where(static q => q.Kind is QueueKind.Building or QueueKind.Defense).SelectMany(static q => q.Items).Select(static i => i.TypeId))
            .ToHashSet(StringComparer.Ordinal);
        private readonly IReadOnlySet<string> placed = belief.OwnBuildingTypes;
        private readonly Faction faction = belief.Faction;
        private int credits = Math.Max(0, belief.Credits);

        public List<GameCommand> Commands { get; } = [];

        public Dictionary<string, int> Reservations { get; } = [];

        public List<string> Notes { get; } = [];

        private double Seconds => belief.Time.Seconds;

        public void Run()
        {
            if (HasRoom(QueueKind.Building)) PlanBuilding();
            if (HasRoom(QueueKind.Defense)) PlanDefense();
            if (HasRoom(QueueKind.Vehicle)) PlanVehicle();
            foreach (QueueKind kind in new[] { QueueKind.Infantry, QueueKind.Aircraft, QueueKind.Naval })
            {
                if (intent.Budget.Army > 0 && HasRoom(kind)) QueueComposition(kind);
            }
        }

        private bool HasRoom(QueueKind kind)
        {
            ProductionQueueState? state = belief.Queues.FirstOrDefault(q => q.Kind == kind);
            if (state is not null)
            {
                if (state.Factories <= 0) return false;
                // A building queue waits for its finished item to be placed (placement is issued this pass) before
                // the next is chosen, so the choice sees the placed building.
                bool placeable = kind is QueueKind.Building or QueueKind.Defense;
                if (placeable ? state.Items.Count > 0 : state.Items.Any(static i => !i.Ready))
                {
                    return false;
                }
            }
            return rules.All.Any(r => r.Queue == kind && Buildable(r));
        }

        private bool Buildable(UnitRule rule) =>
            rule.Cost > 0 && rule.Factions.Contains(faction) && rules.CanBuild(faction, placed, rule.TypeId);

        private bool Queue(UnitRule rule, string reason)
        {
            if (!Buildable(rule)) return false;
            if (rule.Cost > credits)
            {
                Notes.Add($"saving: {rule.TypeId} costs {rule.Cost}, {credits} left ({reason})");
                return false;
            }
            Commands.Add(new ProduceCommand(options.ControllerId, rule.TypeId, rule.Queue));
            string pool = BudgetPools.ForRole(rule.Role);
            Reservations[pool] = Reservations.GetValueOrDefault(pool) + rule.Cost;
            credits -= rule.Cost;
            Notes.Add($"{reason}: queued {rule.TypeId} ({pool})");
            return true;
        }

        // -- Building queue --

        private void PlanBuilding()
        {
            (string TypeId, string Reason)? next = NextBuilding();
            if (next is not { } choice || !rules.TryGet(choice.TypeId, out UnitRule rule)) return;
            if (belief.Power.Surplus + rule.Power < 0 && rule.Power < 0 && PowerType() is { } power && power.TypeId != rule.TypeId)
            {
                Queue(power, $"power-first: {rule.TypeId} needs {-rule.Power} but surplus is {belief.Power.Surplus}");
                return;
            }
            Queue(rule, choice.Reason);
        }

        private (string TypeId, string Reason)? NextBuilding()
        {
            if (belief.Power.LowPower && PowerType() is { } emergency) return (emergency.TypeId, "power: low power");

            UnitRule? refinery = Cheapest(UnitRole.Economy, QueueKind.Building);
            int refineries = features.Economy.Refineries;
            if (refineries == 0 && FirstStepToward(refinery) is { } firstRefinery) return (firstRefinery, "economy: first refinery");

            UnitRule? harvester = CheapestOfRole(UnitRole.Harvester);
            if (harvester is not null && !Buildable(harvester) && FirstStepToward(harvester) is { } harvesterStep)
            {
                return (harvesterStep, $"economy: toward {harvester.TypeId}");
            }

            if (refineries < RefineryTarget() && FirstStepToward(refinery) is { } moreRefinery) return (moreRefinery, $"economy: refinery {refineries + 1}");

            foreach (CompositionTarget target in intent.Composition
                .Where(static c => c.MinShare > 0 && c.Role != UnitRole.Defense)
                .OrderByDescending(static c => c.MinShare).ThenBy(static c => c.Role.ToString(), StringComparer.Ordinal))
            {
                if (rules.All.Any(r => r.Role == target.Role && r.Kind != EntityKind.Building && Buildable(r))) continue;
                foreach (UnitRule candidate in rules.All
                    .Where(r => r.Role == target.Role && r.Kind != EntityKind.Building && r.Factions.Contains(faction))
                    .OrderBy(static r => r.Cost).ThenBy(static r => r.TypeId, StringComparer.Ordinal))
                {
                    if (FirstStepToward(candidate) is { } step) return (step, $"composition: toward {candidate.TypeId} ({target.Role})");
                }
            }

            IEnumerable<string> goals = (playbook?.TechGoals ?? []).Concat(
                intent.Objectives.Where(static o => o.Kind == ObjectiveKind.TechTo && o.TypeId is not null).OrderBy(static o => o.Priority).Select(static o => o.TypeId!));
            foreach (string goal in goals)
            {
                if (rules.TryGet(goal, out UnitRule goalRule) && FirstStepToward(goalRule) is { } step) return (step, $"tech: toward {goal}");
            }

            if (belief.Power.Surplus < options.PowerBuffer && PowerType() is { } buffer) return (buffer.TypeId, "power: buffer");

            int productionBuildings = belief.Own.Count(static e => e.Kind == EntityKind.Building && e.Role == UnitRole.Production);
            if (productionBuildings < options.MaxProductionBuildings && credits >= options.ExtraProductionCredits && intent.Budget.Army > 0)
            {
                UnitRule? extra = rules.All
                    .Where(r => r.Role == UnitRole.Production && r.Kind == EntityKind.Building && Buildable(r))
                    .OrderBy(static r => r.Cost).ThenBy(static r => r.TypeId, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (extra is not null) return (extra.TypeId, $"production: building {productionBuildings + 1}");
            }
            return null;
        }

        private int RefineryTarget()
        {
            double expandAt = intent.PlaybookParameters.TryGetValue("expandAtSeconds", out double e) ? e : options.DefaultExpandAtSeconds;
            int target = 1;
            if (Seconds >= expandAt) target++;
            if (intent.Budget.Economy >= 0.45 && Seconds >= expandAt / 2) target++;
            if (Seconds >= expandAt + 360) target++;
            int oreRegions = belief.Map.Regions.Count(static r => r.HasOre);
            return Math.Clamp(target, 1, Math.Max(1, Math.Min(options.MaxRefineries, oreRegions)));
        }

        /// <summary>
        /// The next building to queue so <paramref name="goal"/> becomes available: the first missing buildable
        /// step of its prerequisite path, or the goal itself when it is a building buildable now; null when the
        /// goal is already available (a unit that can be built, a building that is owned) or unreachable.
        /// </summary>
        private string? FirstStepToward(UnitRule? goal)
        {
            if (goal is null) return null;
            IReadOnlyList<string>? path = rules.PathTo(faction, own, goal.TypeId);
            if (path is null) return null;
            foreach (string step in path)
            {
                if (own.Contains(step)) continue;
                if (rules.TryGet(step, out UnitRule stepRule) && stepRule.Queue == QueueKind.Building && Buildable(stepRule)) return step;
                return null; // the first missing step is not buildable yet (should not happen for a well-formed path)
            }
            bool building = goal.Kind == EntityKind.Building && goal.Queue == QueueKind.Building;
            return building && !own.Contains(goal.TypeId) && Buildable(goal) ? goal.TypeId : null;
        }

        private UnitRule? PowerType() =>
            rules.All.Where(r => r.Queue == QueueKind.Building && r.Power > 0 && Buildable(r))
                .OrderByDescending(static r => r.Power / (double)Math.Max(1, r.Cost)).ThenBy(static r => r.TypeId, StringComparer.Ordinal)
                .FirstOrDefault();

        private UnitRule? Cheapest(UnitRole role, QueueKind queue) =>
            rules.All.Where(r => r.Role == role && r.Queue == queue && r.Cost > 0 && r.Factions.Contains(faction))
                .OrderBy(static r => r.Cost).ThenBy(static r => r.TypeId, StringComparer.Ordinal)
                .FirstOrDefault();

        private UnitRule? CheapestOfRole(UnitRole role) =>
            rules.All.Where(r => r.Role == role && r.Cost > 0 && r.Factions.Contains(faction))
                .OrderBy(static r => r.Cost).ThenBy(static r => r.TypeId, StringComparer.Ordinal)
                .FirstOrDefault();

        // -- Defense queue --

        private void PlanDefense()
        {
            int defenses = belief.Own.Count(static e => e.Kind == EntityKind.Building && e.Role == UnitRole.Defense);
            bool threatened = features.Threats.Any(static t => t.IsBase && t.LocalForceRatio < 1.0);
            double share = intent.Budget.Defense;
            bool defenseComposition = intent.Composition.Any(static c => c.Role == UnitRole.Defense && c.MinShare > 0);
            int target = share >= 0.25 || defenseComposition
                ? (int)Math.Min(options.MaxDefenses, 1 + Seconds / 90)
                : share >= 0.15 ? (int)Math.Min(options.MaxDefenses / 2, Seconds / 240) : 0;
            if (threatened && share > 0) target = Math.Max(target, defenses + 1);
            if (defenses >= target) return;
            UnitRule? best = BestBuildable(QueueKind.Defense, UnitRole.Defense);
            if (best is not null) Queue(best, threatened ? "defense: base threatened" : $"defense: {defenses + 1}/{target}");
        }

        // -- Vehicle queue: harvesters first, then composition --

        private void PlanVehicle()
        {
            int refineries = features.Economy.Refineries;
            double target = intent.PlaybookParameters.TryGetValue("harvesterTarget", out double t)
                ? Math.Max(t, refineries)
                : Math.Max(1, refineries) * options.DefaultHarvesterTargetPerRefinery;
            if (refineries > 0 && features.Economy.Harvesters < target)
            {
                UnitRule? harvester = BestBuildable(QueueKind.Vehicle, UnitRole.Harvester);
                if (harvester is not null)
                {
                    Queue(harvester, $"economy: {features.Economy.Harvesters}/{target:0.#} harvesters");
                    return;
                }
            }
            if (intent.Budget.Army > 0) QueueComposition(QueueKind.Vehicle);
        }

        private void QueueComposition(QueueKind kind)
        {
            UnitRole? role = RoleFor(kind);
            if (role is not { } r) return;
            UnitRule? best = BestBuildable(kind, r);
            if (best is not null) Queue(best, $"composition: {r} in {kind}");
        }

        /// <summary>
        /// The role to build next in a queue. Listed composition targets come first: the largest shortfall below
        /// a target's minimum share. Otherwise every role competes on room below its maximum share, where a combat
        /// role the composition does not list may take up to <c>1 − Σ minimum shares</c> (the playbook bounds what
        /// it lists, not what it leaves out), so a queue the composition does not mention still produces. Anti-air
        /// that cannot hit ground is left out unless enemy aircraft have been seen. When nothing has room, the
        /// listed target with the least excess is built anyway (only one buildable role means its share is 1).
        /// </summary>
        private UnitRole? RoleFor(QueueKind kind)
        {
            double total = features.Army.ValueByRole.Values.Sum();
            double Current(UnitRole role) => total > 0 ? features.Army.ValueByRole.GetValueOrDefault(role) / total : 0.0;
            bool enemyAir = features.Enemy.KnownTech.Any(t => rules.TryGet(t, out UnitRule r) && r.Kind == EntityKind.Aircraft);
            bool CanMake(UnitRole role) => rules.All.Any(r => r.Role == role && r.Queue == kind && r.Kind != EntityKind.Building && Buildable(r)
                && (enemyAir || r.Weapon != WeaponClass.AntiAir));

            List<CompositionTarget> listed = intent.Composition
                .Where(c => c.Role != UnitRole.Defense && CanMake(c.Role))
                .OrderBy(static c => c.Role.ToString(), StringComparer.Ordinal)
                .ToList();
            CompositionTarget? shortest = listed.OrderByDescending(c => c.MinShare - Current(c.Role)).FirstOrDefault();
            if (shortest is not null && shortest.MinShare - Current(shortest.Role) > 0) return shortest.Role;

            double unlistedMax = Math.Max(0, 1 - intent.Composition.Sum(static c => c.MinShare));
            UnitRole[] combat = [UnitRole.AntiArmor, UnitRole.AntiInfantry, UnitRole.Artillery, UnitRole.AntiAir];
            List<CompositionTarget> candidates = [.. listed];
            foreach (UnitRole role in combat)
            {
                if (intent.Composition.Any(c => c.Role == role) || !CanMake(role)) continue;
                candidates.Add(new CompositionTarget(role, 0, intent.Composition.Count == 0 ? 1 : unlistedMax));
            }
            CompositionTarget? roomiest = candidates.OrderByDescending(c => c.MaxShare - Current(c.Role)).ThenBy(static c => c.Role.ToString(), StringComparer.Ordinal).FirstOrDefault();
            if (roomiest is not null && roomiest.MaxShare - Current(roomiest.Role) > 0) return roomiest.Role;
            return listed.OrderByDescending(c => c.MaxShare - Current(c.Role)).Select(static c => (UnitRole?)c.Role).FirstOrDefault();
        }

        private UnitRule? BestBuildable(QueueKind kind, UnitRole role)
        {
            UnitRule? best = null;
            double bestScore = double.NegativeInfinity;
            foreach (UnitRule rule in rules.All.OrderBy(static r => r.TypeId, StringComparer.Ordinal))
            {
                if (rule.Role != role || rule.Queue != kind || !Buildable(rule)) continue;
                double effectiveness = EffectivenessAgainstEnemy(rule);
                double cost = Math.Max(1, rule.Cost) + rule.BuildSeconds * 10.0;
                double strength = role is UnitRole.Harvester ? 1.0 : Math.Max(1.0, rule.Damage * rule.Strength) / Math.Max(1, rule.Cost);
                double score = effectiveness * strength * 1000.0 / cost;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = rule;
                }
            }
            return best;
        }

        private double EffectivenessAgainstEnemy(UnitRule candidate)
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
            return weight > 0 ? Math.Max(0.05, sum / weight) : 1.0;
        }
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
                Region? region = worst is null ? null : belief.Map.Regions.FirstOrDefault(r => r.Id == worst.Region);
                // Unthreatened: face the nearest non-own start location, where attacks come from.
                region ??= belief.Map.Regions
                    .Where(r => r.IsStartLocation && r.Center.DistanceTo(anchor) > r.Radius)
                    .OrderBy(r => r.Center.DistanceTo(anchor)).ThenBy(static r => r.Id.Value)
                    .FirstOrDefault();
                if (region is not null) bias = region.Center;
            }

            Cell chosen = BuildPlacement.ChooseCell(anchor, bias, ownBuildings, belief.Map.Width, belief.Map.Height, options.BuildSearchRings, options.BuildGridStep);
            commands.Add(new PlaceBuildingCommand(options.ControllerId, item.TypeId, chosen));
            notes.Add($"placement: {item.TypeId} at {chosen}");
        }
    }
}
