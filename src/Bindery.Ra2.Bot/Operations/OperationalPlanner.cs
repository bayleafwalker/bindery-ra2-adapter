// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bindery.Ra2.Bot.Arbitration;

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
/// <item>A ready own superweapon is fired at the known enemy building whose surroundings (within
/// <see cref="OperationalOptions.SuperweaponTargetRadiusCells"/>) hold the most known enemy building value;
/// with no enemy building known it waits.</item>
/// </list>
/// </remarks>
public sealed partial class OperationalPlanner : IOperationalPlanner
{
    private readonly IRulesDatabase rules;
    private readonly IPlaybookLibrary playbooks;
    private readonly OperationalOptions options;
    private readonly Dictionary<string, RegionGraph> graphByMap = [];

    /// <summary>
    /// While queues are not reported: per queue, the item the planner last ordered into it. This is the planner's own
    /// ledger of issued orders, the only evidence it has that a queue is occupied and that money is committed.
    /// </summary>
    private readonly Dictionary<QueueKind, UnreportedOrder> unreportedOrders = [];

    /// <summary>An order issued while queues are unreported: when, what it costs, and until when it keeps its queue busy.</summary>
    private readonly record struct UnreportedOrder(GameTime OrderedAt, int Cost, double BuildSeconds, GameTime BusyUntil)
    {
        /// <summary>Cost times the share of the build time not yet elapsed: the game charges while it builds.</summary>
        public double OwedAt(GameTime now) => BuildSeconds <= 0
            ? 0
            : Math.Max(0, Cost) * (1 - Math.Clamp(now.SecondsSince(OrderedAt) / BuildSeconds, 0, 1));
    }

    /// <summary>What the orders issued while queues were unreported still owe at <paramref name="now"/>, rounded up.</summary>
    private int UnreportedDebt(GameTime now)
    {
        double owed = 0;
        foreach (UnreportedOrder order in unreportedOrders.Values) owed += order.OwedAt(now);
        return (int)Math.Min(int.MaxValue, Math.Ceiling(owed - 1e-9));
    }

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
        PlanPlacement(belief, features, intent, pass.Commands, pass.Notes);
        PlanSuperweapons(belief, pass.Commands, pass.Notes);
        List<SquadOrder> squadOrders = PlanSquads(belief, features, intent, graph, leases, pass.Notes);

        SortedDictionary<string, int> sortedReservations = new(pass.Reservations, StringComparer.Ordinal);
        return new OperationalPlan(belief.Time, intent.IntentId, pass.Commands, squadOrders, sortedReservations, pass.Notes,
            belief.QueuesKnown ? 0 : pass.Owed);
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
        // Where production is paid as it builds, the credits on hand still include what queued items owe: from the
        // reported queues, or, when none are reported, from the planner's own earlier orders.
        public int Owed { get; } = !planner.options.ProductionChargedWhileBuilding ? 0
            : belief.QueuesKnown ? ProductionDebt.Unpaid(planner.rules, belief.Queues)
            : planner.UnreportedDebt(belief.Time);
        private int credits;
        private int economyReserve;
        private UnitRule? reservedFor;

        public List<GameCommand> Commands { get; } = [];

        public Dictionary<string, int> Reservations { get; } = [];

        public List<string> Notes { get; } = [];

        private double Seconds => belief.Time.Seconds;

        public void Run()
        {
            credits = Math.Max(0, belief.Credits - Owed);
            reservedFor = EconomyReserve();
            // A reserve that can never be paid (less money than the link costs and no income to close the gap) only
            // idles the base; it is dropped so the money at least buys an army.
            if (reservedFor is not null && reservedFor.Cost > credits && features.Economy.IncomePerMinute.Current <= 0)
            {
                Notes.Add($"reserve dropped: {reservedFor.TypeId} costs {reservedFor.Cost}, {credits} left and no income");
                reservedFor = null;
            }
            economyReserve = reservedFor?.Cost ?? 0;
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
            if (!belief.QueuesKnown)
            {
                // Nothing reported is not the same as idle: only the planner's own orders say a queue is busy.
                if (planner.unreportedOrders.TryGetValue(kind, out UnreportedOrder last) && belief.Time < last.BusyUntil) return false;
                return rules.All.Any(r => r.Queue == kind && Buildable(r));
            }
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
            // Only the reserved link itself, and power it (or the base) cannot run without, may use the reserve.
            bool exempt = reservedFor is null || rule.TypeId == reservedFor.TypeId
                || (rule.Role == UnitRole.Power && (belief.Power.LowPower || belief.Power.Surplus + Math.Min(0, reservedFor.Power) < 0));
            if (!exempt && economyReserve > 0 && rule.Cost > credits - economyReserve)
            {
                Notes.Add($"saving: {rule.TypeId} would dip below the {economyReserve} kept for the economy ({reason})");
                return false;
            }
            if (rule.Cost > credits)
            {
                Notes.Add($"saving: {rule.TypeId} costs {rule.Cost}, {credits} left ({reason})");
                return false;
            }
            Commands.Add(new ProduceCommand(options.ControllerId, rule.TypeId, rule.Queue));
            if (!belief.QueuesKnown)
            {
                planner.unreportedOrders[rule.Queue] = new UnreportedOrder(belief.Time, rule.Cost, rule.BuildSeconds,
                    belief.Time.Plus(Math.Max(0, rule.BuildSeconds) + options.UnknownQueueReorderSeconds));
            }
            string pool = BudgetPools.ForRole(rule.Role);
            Reservations[pool] = Reservations.GetValueOrDefault(pool) + rule.Cost;
            credits -= rule.Cost;
            Notes.Add($"{reason}: queued {rule.TypeId} ({pool})");
            return true;
        }

        /// <summary>
        /// The link whose cost every other purchase (army, defence, tech, spare power) must leave untouched while the economy's critical chain is
        /// unfinished and its next link is not yet paid for: the first refinery, the step that unlocks harvesters
        /// (the war factory), then the first two harvesters. Without it cheap infantry drains the starting credits
        /// and the base never gets an income; with it, only money beyond that next link goes to units.
        /// </summary>
        private UnitRule? EconomyReserve()
        {
            int queuedHarvesters = belief.Queues.SelectMany(static q => q.Items).Count(i => rules.TryGet(i.TypeId, out UnitRule r) && r.Role == UnitRole.Harvester);
            UnitRule? refinery = Cheapest(UnitRole.Economy, QueueKind.Building);
            if (features.Economy.Refineries == 0)
            {
                return refinery is not null && !own.Contains(refinery.TypeId) ? refinery : null;
            }
            UnitRule? harvester = CheapestOfRole(UnitRole.Harvester);
            if (harvester is null) return null;
            if (!Buildable(harvester))
            {
                // The step toward harvesters still to pay for; once it is queued, the first harvester itself.
                string? step = FirstStepToward(harvester);
                if (step is not null) return rules.TryGet(step, out UnitRule stepRule) ? stepRule : null;
                return rules.PathTo(faction, own, harvester.TypeId) is not null ? harvester : null;
            }
            return features.Economy.Harvesters + queuedHarvesters < 2 ? harvester : null;
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

        /// <summary>True when the economy reserve would refuse <paramref name="typeId"/> (see <see cref="Queue"/>).</summary>
        private bool BlockedByReserve(string typeId) =>
            reservedFor is not null && economyReserve > 0 && typeId != reservedFor.TypeId
            && rules.TryGet(typeId, out UnitRule rule) && rule.Role != UnitRole.Power && rule.Cost > credits - economyReserve;

        private (string TypeId, string Reason)? NextBuilding()
        {
            if (belief.Power.LowPower && PowerType() is { } emergency) return (emergency.TypeId, "power: low power");

            // Barracks before the refinery: infantry is the only army available before the war factory, the
            // starting credits otherwise sit idle for minutes, and every production building also shortens the
            // build time of what follows (the refinery and war factory). Not when it would dip into the economy
            // reserve: the reserve would refuse it every plan, nothing would be queued, and no income would ever
            // arrive; the reserved link comes first then.
            if (!rules.All.Any(r => r.Queue == QueueKind.Infantry && r.Kind != EntityKind.Building && Buildable(r))
                && CheapestOfQueue(QueueKind.Infantry) is { } infantry && FirstStepToward(infantry) is { } barracks
                && !BlockedByReserve(barracks))
            {
                return (barracks, $"opening: toward {infantry.TypeId}");
            }

            UnitRule? refinery = Cheapest(UnitRole.Economy, QueueKind.Building);
            int refineries = features.Economy.Refineries;
            if (refineries == 0 && FirstStepToward(refinery) is { } firstRefinery) return (firstRefinery, "economy: first refinery");

            UnitRule? harvester = CheapestOfRole(UnitRole.Harvester);
            if (harvester is not null && !Buildable(harvester) && FirstStepToward(harvester) is { } harvesterStep)
            {
                return (harvesterStep, $"economy: toward {harvester.TypeId}");
            }

            if (refineries < RefineryTarget() && FirstStepToward(refinery, repeatable: true) is { } moreRefinery) return (moreRefinery, $"economy: refinery {refineries + 1}");

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

            // The construction yard is a Production building but trains nothing (zero cost: a deploy product), so it
            // does not count toward the factories the army has.
            int productionBuildings = belief.Own.Count(e => e.Kind == EntityKind.Building && e.Role == UnitRole.Production
                && !(rules.TryGet(e.TypeId, out UnitRule r) && r.Cost == 0));
            if (productionBuildings < options.MaxProductionBuildings && credits >= options.ExtraProductionCredits && intent.Budget.Army > 0)
            {
                if (ExtraFactory() is { } extra) return (extra.TypeId, $"production: building {productionBuildings + 1}");
            }
            return null;
        }

        /// <summary>
        /// The factory to add: the cheapest buildable Production building that trains a unit of the composition's
        /// largest role (a second war factory for a tank army; each factory speeds only its own queue in RA2),
        /// else the cheapest buildable one. Picking the cheapest outright always added barracks.
        /// </summary>
        private UnitRule? ExtraFactory()
        {
            List<UnitRule> factories = rules.All
                .Where(r => r.Role == UnitRole.Production && r.Kind == EntityKind.Building && Buildable(r))
                .OrderBy(static r => r.Cost).ThenBy(static r => r.TypeId, StringComparer.Ordinal)
                .ToList();
            foreach (CompositionTarget target in intent.Composition
                .Where(static c => c.MinShare > 0 && c.Role != UnitRole.Defense)
                .OrderByDescending(static c => c.MinShare).ThenBy(static c => c.Role.ToString(), StringComparer.Ordinal))
            {
                List<UnitRule> units = rules.All
                    .Where(r => r.Role == target.Role && r.Kind != EntityKind.Building && r.Cost > 0 && r.Factions.Contains(faction))
                    .ToList();
                UnitRule? trainer = factories.FirstOrDefault(f => units.Any(u => u.Prerequisites.Any(group => group.Contains(f.TypeId))));
                if (trainer is not null) return trainer;
            }
            return factories.FirstOrDefault();
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
        /// <remarks>
        /// With <paramref name="repeatable"/> an owned goal building is still returned when buildable: a second
        /// refinery is a copy of one the base already has, and "owned" would otherwise read as "available".
        /// </remarks>
        private string? FirstStepToward(UnitRule? goal, bool repeatable = false)
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
            return building && (repeatable || !own.Contains(goal.TypeId)) && Buildable(goal) ? goal.TypeId : null;
        }

        private UnitRule? PowerType() =>
            rules.All.Where(r => r.Queue == QueueKind.Building && r.Power > 0 && Buildable(r))
                .OrderByDescending(static r => r.Power / (double)Math.Max(1, r.Cost)).ThenBy(static r => r.TypeId, StringComparer.Ordinal)
                .FirstOrDefault();

        private UnitRule? Cheapest(UnitRole role, QueueKind queue) =>
            rules.All.Where(r => r.Role == role && r.Queue == queue && r.Cost > 0 && r.Factions.Contains(faction))
                .OrderBy(static r => r.Cost).ThenBy(static r => r.TypeId, StringComparer.Ordinal)
                .FirstOrDefault();

        /// <summary>The cheapest armed unit of this faction produced by <paramref name="queue"/>.</summary>
        private UnitRule? CheapestOfQueue(QueueKind queue) =>
            rules.All.Where(r => r.Queue == queue && r.Kind != EntityKind.Building && r.Cost > 0 && r.Damage > 0 && r.Factions.Contains(faction))
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
                    Queue(harvester, string.Create(CultureInfo.InvariantCulture, $"economy: {features.Economy.Harvesters}/{target:0.#} harvesters"));
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

            // When no listed combat role can be produced anywhere yet (before the war factory, only infantry
            // exists), the shares are unattainable: a queue that can fight builds its best role rather than
            // standing idle on credits while the base is defenceless.
            bool listedAttainable = intent.Composition.Any(c => c.Role != UnitRole.Defense && c.MinShare > 0
                && rules.All.Any(r => r.Role == c.Role && r.Kind != EntityKind.Building && Buildable(r) && (enemyAir || r.Weapon != WeaponClass.AntiAir)));
            double unlistedMax = listedAttainable ? Math.Max(0, 1 - intent.Composition.Sum(static c => c.MinShare)) : 1.0;
            UnitRole[] combat = [UnitRole.AntiArmor, UnitRole.AntiInfantry, UnitRole.Artillery, UnitRole.AntiAir];
            List<CompositionTarget> candidates = [.. listed];
            foreach (UnitRole role in combat)
            {
                if (intent.Composition.Any(c => c.Role == role) || !CanMake(role)) continue;
                candidates.Add(new CompositionTarget(role, 0, intent.Composition.Count == 0 ? 1 : unlistedMax));
            }
            if (!listedAttainable && candidates.Count > 0)
            {
                return candidates.OrderBy(c => Current(c.Role)).ThenBy(static c => c.Role.ToString(), StringComparer.Ordinal).First().Role;
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

    // ----- Superweapons: fire when ready. -----

    private void PlanSuperweapons(BeliefSnapshot belief, List<GameCommand> commands, List<string> notes)
    {
        if (belief.Superweapons is not { } timers) return;
        List<EnemyContact> buildings = belief.Enemies
            .Where(static c => !c.ConfirmedDestroyed && c.Kind == EntityKind.Building)
            .OrderBy(static c => c.Id.Value)
            .ToList();
        foreach (SuperweaponStatus timer in timers.Where(t => t.Owner == belief.Self && t.Ready && t.Building is not null).OrderBy(static t => t.Building!.Value.Value))
        {
            if (!belief.Own.Any(e => e.Id == timer.Building!.Value)) continue;
            EnemyContact? best = null;
            double bestValue = 0;
            foreach (EnemyContact candidate in buildings)
            {
                double value = buildings
                    .Where(b => b.LastSeenPosition.DistanceTo(candidate.LastSeenPosition) <= options.SuperweaponTargetRadiusCells)
                    .Sum(static b => Math.Max(1, b.Value) * b.Confidence); // a deploy product (construction yard) costs 0 but is still a target
                // The strike hits both sides: what it would destroy of our own (an assault on that base) counts against it.
                value -= belief.Own
                    .Where(o => o.Position.DistanceTo(candidate.LastSeenPosition) <= options.SuperweaponTargetRadiusCells)
                    .Sum(static o => Math.Max(1, o.Value));
                if (value > bestValue)
                {
                    best = candidate;
                    bestValue = value;
                }
            }
            if (best is null)
            {
                notes.Add($"superweapon: {timer.TypeId} ready, no known enemy building worth more than the own objects the strike would hit");
                continue;
            }
            commands.Add(new LaunchSuperweaponCommand(options.ControllerId, timer.Building!.Value, best.LastSeenPosition));
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"superweapon: {timer.TypeId} at {best.LastSeenPosition} ({bestValue:0} known building value net of own objects in the radius)"));
        }
    }

    // ----- Placement: place any ready building/defense item. -----

    /// <summary>The last placement order per queue: which item, onto which cell, when.</summary>
    private readonly Dictionary<QueueKind, (string TypeId, Cell Cell, GameTime At)> pendingPlacement = [];

    /// <summary>Cells the game refused a building on, until when they stay excluded.</summary>
    private readonly Dictionary<Cell, GameTime> refusedCells = [];

    /// <remarks>
    /// <para>Placement has no command-result feedback, so refusal is inferred: an item still ready
    /// <see cref="OperationalOptions.PlacementRetrySeconds"/> after it was ordered onto a cell was not placed there,
    /// and that cell is excluded for <see cref="OperationalOptions.RejectedPlacementMemorySeconds"/>. Until then the
    /// same order is repeated (it may still be in flight). Without this the queue deadlocked: the search is a pure
    /// function of the base, so it chose the refused cell again every pass and nothing else was ever built.</para>
    /// <para>Every cell chosen this pass joins the avoid list, so the building and the defense placed in one pass
    /// never get the same cell.</para>
    /// <para>A refinery goes toward the ore it should mine: the Expand objective's field, else the nearest field
    /// with no own refinery by it, else the nearest field. The search is anchored on the own building nearest that
    /// field (placement must stay by the base), so each new refinery creeps toward the untaken ore instead of
    /// crowding the home field.</para>
    /// </remarks>
    private void PlanPlacement(BeliefSnapshot belief, StrategicFeatures features, StrategicIntent intent, List<GameCommand> commands, List<string> notes)
    {
        foreach (Cell expired in refusedCells.Where(kv => kv.Value <= belief.Time).Select(static kv => kv.Key).ToList()) refusedCells.Remove(expired);

        List<OwnEntity> buildings = belief.Own.Where(static e => e.Kind == EntityKind.Building).ToList();
        if (buildings.Count == 0) return;
        List<Cell> avoid = [.. buildings.Select(static e => e.Position), .. refusedCells.Keys.OrderBy(static c => c.X).ThenBy(static c => c.Y)];
        Cell centroid = new(
            (int)Math.Round(buildings.Average(static e => e.Position.X)),
            (int)Math.Round(buildings.Average(static e => e.Position.Y)));

        foreach (QueueKind kind in new[] { QueueKind.Building, QueueKind.Defense })
        {
            ProductionQueueState? queue = belief.Queues.FirstOrDefault(q => q.Kind == kind);
            QueueItem? ready = queue?.Items.FirstOrDefault(static i => i.Ready);
            if (ready is not { } item)
            {
                pendingPlacement.Remove(kind);
                continue;
            }

            // A building of this type now standing on the pending cell means that order was placed, and this is the
            // next copy (a second power plant), not the same item waiting.
            if (pendingPlacement.TryGetValue(kind, out (string TypeId, Cell Cell, GameTime At) pending) && pending.TypeId == item.TypeId
                && !buildings.Any(b => b.TypeId == item.TypeId && b.Position.DistanceTo(pending.Cell) < options.BuildGridStep))
            {
                if (belief.Time.SecondsSince(pending.At) < options.PlacementRetrySeconds)
                {
                    avoid.Add(pending.Cell);
                    commands.Add(new PlaceBuildingCommand(options.ControllerId, item.TypeId, pending.Cell));
                    notes.Add($"placement: {item.TypeId} at {pending.Cell} (awaiting the game)");
                    continue;
                }
                refusedCells[pending.Cell] = belief.Time.Plus(options.RejectedPlacementMemorySeconds);
                avoid.Add(pending.Cell);
                notes.Add($"placement: {item.TypeId} refused at {pending.Cell}, searching elsewhere");
            }

            Cell anchor = centroid;
            Cell? bias = null;
            if (rules.TryGet(item.TypeId, out UnitRule rule) && rule.Role == UnitRole.Economy)
            {
                if (RefineryField(belief, intent, buildings, centroid) is { } field)
                {
                    bias = field.Center;
                    anchor = buildings.OrderBy(b => b.Position.DistanceTo(field.Center)).ThenBy(static b => b.Id.Value).First().Position;
                }
            }
            else if (kind == QueueKind.Defense)
            {
                ThreatAssessment? worst = features.Threats.Where(static t => t.IsBase)
                    .OrderBy(static t => t.LocalForceRatio).FirstOrDefault();
                Region? region = worst is null ? null : belief.Map.Regions.FirstOrDefault(r => r.Id == worst.Region);
                // Unthreatened: face the nearest non-own start location, where attacks come from.
                region ??= belief.Map.Regions
                    .Where(r => r.IsStartLocation && r.Center.DistanceTo(centroid) > r.Radius)
                    .OrderBy(r => r.Center.DistanceTo(centroid)).ThenBy(static r => r.Id.Value)
                    .FirstOrDefault();
                if (region is not null) bias = region.Center;
            }

            Cell chosen = BuildPlacement.ChooseCell(anchor, bias, avoid, belief.Map.Width, belief.Map.Height, options.BuildSearchRings, options.BuildGridStep);
            avoid.Add(chosen);
            pendingPlacement[kind] = (item.TypeId, chosen, belief.Time);
            commands.Add(new PlaceBuildingCommand(options.ControllerId, item.TypeId, chosen));
            notes.Add($"placement: {item.TypeId} at {chosen}");
        }
    }

    /// <summary>
    /// The ore field a new refinery should serve: the highest-priority Expand objective's field that no own refinery
    /// within <see cref="RefineryReachCells"/> serves yet, else the field nearest the base with no such refinery, else
    /// the field nearest the base. An objective naming an already served field (a strategist naming the home field)
    /// would otherwise pull every extra refinery back onto it.
    /// </summary>
    private static OreField? RefineryField(BeliefSnapshot belief, StrategicIntent intent, List<OwnEntity> buildings, Cell centroid)
    {
        List<Cell> refineries = buildings.Where(static b => b.Role == UnitRole.Economy).Select(static b => b.Position).ToList();
        bool Unserved(OreField o) => !refineries.Any(r => r.DistanceTo(o.Center) <= RefineryReachCells);
        foreach (Objective expand in intent.Objectives.Where(static o => o.Kind == ObjectiveKind.Expand && o.Region is not null).OrderBy(static o => o.Priority))
        {
            OreField? wanted = belief.Map.OreFields.Where(o => o.Region == expand.Region && Unserved(o))
                .OrderBy(o => o.Center.DistanceTo(centroid)).ThenBy(static o => o.Region.Value).FirstOrDefault();
            if (wanted is not null) return wanted;
        }
        IOrderedEnumerable<OreField> byDistance = belief.Map.OreFields
            .OrderBy(o => o.Center.DistanceTo(centroid)).ThenBy(static o => o.Region.Value);
        return byDistance.FirstOrDefault(Unserved) ?? byDistance.FirstOrDefault();
    }

    /// <summary>A refinery within this many cells of a field's centre already serves that field.</summary>
    private const double RefineryReachCells = 10;
}
