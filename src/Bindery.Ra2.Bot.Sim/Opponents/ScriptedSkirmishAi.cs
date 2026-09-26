// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Rules;

namespace Bindery.Ra2.Bot.Sim.Opponents;

/// <summary>
/// An independent scripted skirmish opponent in the manner of retail RA2's AI: a fixed per-faction build list with
/// seeded timing jitter, harvester replacement, power management, a defence list, and team-based attack waves that
/// accumulate a task force at the base and then attack-move on the nearest known enemy base region without ever
/// retreating. Units left at home defend any region with own structures that enemies enter.
/// </summary>
/// <remarks>
/// It shares no code with the bot's belief, features, strategy, arbitration, runtime, operations or tactics: it
/// reads only its own <see cref="ObservationFrame"/> (fog applies; the enemy's start is inferred from the map's
/// public start locations and from what it has seen) plus the contracts, <see cref="RegionGraph"/> and
/// <see cref="IRulesDatabase"/>. It acts once per game second; given the same frames and seed it issues the same
/// commands. The scripts are written against the approximate fixture; on another roster (a mod) each type id the
/// roster does not define is replaced by <see cref="RosterSubstitution"/> (same role, nearest cost) or its step is
/// dropped, as a retail AI's ai.ini would be edited for a mod.
/// </remarks>
public sealed class ScriptedSkirmishAi
{
    private const string Controller = "scripted-ai";
    private const double ReorderSeconds = 6;

    private readonly IRulesDatabase rules;
    private readonly PlayerId self;
    private readonly Faction faction;
    private readonly MapInfo map;
    private readonly RegionGraph graph;
    private readonly OpponentScript script;
    private readonly OpponentDifficulty difficulty;
    private readonly Xorshift rng;

    private readonly Dictionary<EntityId, (RegionId Region, string TypeId)> knownEnemyBuildings = [];
    private readonly HashSet<RegionId> clearedStarts = [];
    private readonly HashSet<EntityId> attackers = [];
    private readonly Dictionary<EntityId, GameTime> lastOrdered = [];
    private readonly HashSet<QueueKind> queuedThisTick = [];
    private RegionId? home;
    private RegionId? waveTarget;
    private int wavesLaunched;
    private double nextBuildAllowedAt;

    public ScriptedSkirmishAi(IRulesDatabase rules, PlayerId self, Faction faction, MapInfo map, string style, OpponentDifficulty difficulty, int seed)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(style);
        this.rules = rules;
        this.self = self;
        this.faction = faction;
        this.map = map;
        this.difficulty = difficulty;
        graph = new RegionGraph(map);
        script = Fit(OpponentProfiles.For(style, faction), rules, faction);
        Style = style;
        rng = new Xorshift(unchecked(seed * 7919 + self.Value * 104_729 + style.Length));
    }

    public string Style { get; }

    private static readonly Lazy<RulesDatabase> AuthoredAgainst = new(RulesDatabase.LoadEmbeddedFixture);

    /// <summary>
    /// The script on <paramref name="rules"/>' roster: unchanged when the roster defines every type it names, else
    /// with each missing type replaced by <see cref="RosterSubstitution"/> or its step dropped.
    /// </summary>
    internal static OpponentScript Fit(OpponentScript script, IRulesDatabase rules, Faction faction)
    {
        IEnumerable<string> named = script.Build.Select(static s => s.TypeId).Concat(script.Defenses.Select(static s => s.TypeId))
            .Concat(script.TaskForce.Select(static s => s.TypeId)).Concat((script.Surplus ?? []).Select(static s => s.TypeId));
        if (named.All(t => rules.TryGet(t, out _))) return script;
        string? Map(string typeId) => rules.TryGet(typeId, out _) ? typeId : RosterSubstitution.Resolve(typeId, [faction], rules, AuthoredAgainst.Value);
        List<BuildStep> build = [.. script.Build.Select(s => Map(s.TypeId) is { } t ? s with { TypeId = t } : null).OfType<BuildStep>()];
        return script with
        {
            Build = build,
            Defenses = [.. script.Defenses.Select(s => Map(s.TypeId) is { } t ? s with { TypeId = t } : null).OfType<BuildStep>()],
            DefensesAfterStep = Math.Min(script.DefensesAfterStep, Math.Max(0, build.Count - 1)),
            TaskForce = [.. script.TaskForce.Select(s => Map(s.TypeId) is { } t ? s with { TypeId = t } : null).OfType<TaskForceSlot>()],
            Surplus = script.Surplus is null ? null : [.. script.Surplus.Select(s => Map(s.TypeId) is { } t ? s with { TypeId = t } : null).OfType<BuildStep>()],
        };
    }

    /// <summary>Attack waves launched so far.</summary>
    public int WavesLaunched => wavesLaunched;

    /// <summary>Returns this second's commands; frames between whole seconds get none (the sim resolves per second).</summary>
    public IReadOnlyList<GameCommand> Tick(ObservationFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Time.Frame % GameTime.FramesPerSecond != 0) return [];

        List<ObservedEntity> own = [.. frame.Entities.Where(e => e.Owner == self).OrderBy(e => e.Id.Value)];
        List<ObservedEntity> enemies = [.. frame.Entities.Where(e => e.Owner != self).OrderBy(e => e.Id.Value)];
        RememberEnemyBuildings(frame, enemies);

        List<GameCommand> commands = [];
        queuedThisTick.Clear();
        ObservedEntity? mcv = own.FirstOrDefault(e => Rule(e.TypeId)?.Role == UnitRole.Mcv && !e.Deployed);
        bool hasYard = own.Any(e => Rule(e.TypeId) is { Kind: EntityKind.Building, Role: UnitRole.Production, Cost: 0 });
        if (mcv is not null && !hasYard)
        {
            home ??= RegionOf(mcv.Position);
            commands.Add(new DeployCommand(Controller, mcv.Id));
            return commands;
        }
        ObservedEntity? anyBuilding = own.FirstOrDefault(e => Rule(e.TypeId)?.Kind == EntityKind.Building);
        if (anyBuilding is null) return commands;
        home ??= RegionOf(anyBuilding.Position);

        PlaceReadyBuildings(frame, own, commands);
        int credits = frame.Credits;
        // Harvesters first: a refinery without them earns nothing, and they share the vehicle queue with the army.
        credits = QueueHarvesters(frame, own, credits, commands);
        credits = QueueBase(frame, own, credits, commands);
        credits = QueueDefenses(frame, own, credits, commands);
        QueueArmy(frame, own, credits, commands);
        CommandArmy(frame, own, enemies, commands);
        return commands;
    }

    // ----- knowledge -----

    private void RememberEnemyBuildings(ObservationFrame frame, List<ObservedEntity> enemies)
    {
        foreach (ObservedEntity e in enemies)
        {
            if (Rule(e.TypeId)?.Kind == EntityKind.Building) knownEnemyBuildings[e.Id] = (RegionOf(e.Position), e.TypeId);
        }
        HashSet<EntityId> present = [.. enemies.Select(e => e.Id)];
        foreach (EntityId id in knownEnemyBuildings.Keys.OrderBy(k => k.Value).ToList())
        {
            if (frame.VisibleRegions.Contains(knownEnemyBuildings[id].Region) && !present.Contains(id)) knownEnemyBuildings.Remove(id);
        }
        foreach (Region start in map.Regions.Where(r => r.IsStartLocation))
        {
            if (start.Id == home || !frame.VisibleRegions.Contains(start.Id)) continue;
            bool enemyThere = enemies.Any(e => Rule(e.TypeId)?.Kind == EntityKind.Building && RegionOf(e.Position) == start.Id);
            if (!enemyThere) clearedStarts.Add(start.Id);
        }
    }

    // ----- construction -----

    private void PlaceReadyBuildings(ObservationFrame frame, List<ObservedEntity> own, List<GameCommand> commands)
    {
        foreach (ProductionQueueState queue in frame.Queues.Where(q => q.Kind is QueueKind.Building or QueueKind.Defense))
        {
            QueueItem? ready = queue.Items.FirstOrDefault(i => i.Ready);
            if (ready is null) continue;
            UnitRule rule = rules.Get(ready.TypeId);
            commands.Add(new PlaceBuildingCommand(Controller, ready.TypeId, PlacementCell(own, rule.Role == UnitRole.Defense)));
        }
    }

    /// <summary>A cell inside the home region, spread on a ring; defences are biased toward the enemy's approach.</summary>
    private Cell PlacementCell(List<ObservedEntity> own, bool defense)
    {
        Region region = map.Regions.First(r => r.Id == home);
        List<Cell> taken = [.. own.Where(e => Rule(e.TypeId)?.Kind == EntityKind.Building).Select(e => e.Position)];
        Cell? toward = defense ? ApproachCell(region) : null;
        List<Cell> candidates = [];
        int reach = Math.Max(2, region.Radius - 1);
        for (int dx = -reach; dx <= reach; dx += 2)
        {
            for (int dy = -reach; dy <= reach; dy += 2)
            {
                Cell c = new(region.Center.X + dx, region.Center.Y + dy);
                if (c.X < 0 || c.Y < 0 || c.X >= map.Width || c.Y >= map.Height) continue;
                // Two cells clear of every own building: the simulator refuses an overlapping footprint.
                if (RegionOf(c) != region.Id || taken.Any(t => t.DistanceTo(c) < 2.0)) continue;
                candidates.Add(c);
            }
        }
        if (candidates.Count == 0) return region.Center;
        IOrderedEnumerable<Cell> ordered = toward is { } t2
            ? candidates.OrderBy(c => c.DistanceTo(t2))
            : candidates.OrderBy(c => c.DistanceTo(region.Center));
        return ordered.ThenBy(c => c.X).ThenBy(c => c.Y).First();
    }

    private Cell? ApproachCell(Region homeRegion)
    {
        RegionId? enemy = EnemyStarts().FirstOrDefault();
        if (enemy is null) return null;
        IReadOnlyList<RegionId> path = graph.Path(homeRegion.Id, enemy.Value);
        return path.Count > 1 ? map.Regions.First(r => r.Id == path[1]).Center : null;
    }

    private int QueueBase(ObservationFrame frame, List<ObservedEntity> own, int credits, List<GameCommand> commands)
    {
        if (QueueBusy(frame, QueueKind.Building) || frame.Time.Seconds < nextBuildAllowedAt) return credits;
        HashSet<string> owned = OwnedBuildingTypes(own);
        string? next = null;
        if (frame.Power.LowPower || frame.Power.Surplus < 0) next = PowerType();
        if (next is null)
        {
            foreach (BuildStep step in script.Build)
            {
                if (Count(own, frame, step.TypeId) >= step.Count) continue;
                next = step.TypeId;
                break;
            }
        }
        if (next is null && credits >= OpponentScript.SurplusCredits && script.Surplus is { } surplus)
        {
            next = surplus.FirstOrDefault(step => Count(own, frame, step.TypeId) < step.Count && rules.CanBuild(faction, owned, step.TypeId))?.TypeId;
        }
        if (next is null) return credits;
        UnitRule rule = rules.Get(next);
        if (rule.Power < 0 && frame.Power.Surplus + rule.Power < 0 && PowerType() is { } power) { next = power; rule = rules.Get(power); }
        if (!rules.CanBuild(faction, owned, next) || credits < rule.Cost) return credits;
        Produce(commands, rule);
        nextBuildAllowedAt = frame.Time.Seconds + BuildPause();
        return credits - rule.Cost;
    }

    private int QueueHarvesters(ObservationFrame frame, List<ObservedEntity> own, int credits, List<GameCommand> commands)
    {
        string? harvester = rules.All.Where(r => r.Factions.Contains(faction) && r.Role == UnitRole.Harvester)
            .OrderBy(r => r.Cost).ThenBy(r => r.TypeId, StringComparer.Ordinal).Select(r => r.TypeId).FirstOrDefault();
        if (harvester is null) return credits;
        int refineries = own.Count(e => Rule(e.TypeId)?.Role == UnitRole.Economy);
        int want = refineries * script.HarvestersPerRefinery;
        if (Count(own, frame, harvester) >= want || QueueBusy(frame, QueueKind.Vehicle)) return credits;
        UnitRule rule = rules.Get(harvester);
        if (!rules.CanBuild(faction, OwnedBuildingTypes(own), harvester) || credits < rule.Cost) return credits;
        Produce(commands, rule);
        return credits - rule.Cost;
    }

    private int QueueDefenses(ObservationFrame frame, List<ObservedEntity> own, int credits, List<GameCommand> commands)
    {
        if (QueueBusy(frame, QueueKind.Defense)) return credits;
        for (int i = 0; i <= Math.Min(script.DefensesAfterStep, script.Build.Count - 1); i++)
        {
            if (Count(own, frame, script.Build[i].TypeId) < script.Build[i].Count) return credits;
        }
        HashSet<string> owned = OwnedBuildingTypes(own);
        foreach (BuildStep step in script.Defenses)
        {
            if (Count(own, frame, step.TypeId) >= step.Count) continue;
            UnitRule rule = rules.Get(step.TypeId);
            if (!rules.CanBuild(faction, owned, step.TypeId)) continue;
            if (frame.Power.Surplus + rule.Power < 0) return credits;
            if (credits < rule.Cost) return credits;
            Produce(commands, rule);
            return credits - rule.Cost;
        }
        return credits;
    }

    private void QueueArmy(ObservationFrame frame, List<ObservedEntity> own, int credits, List<GameCommand> commands)
    {
        HashSet<string> owned = OwnedBuildingTypes(own);
        // Keep a reserve while the base list is unfinished, so the army does not starve it: at least 600, and the
        // next missing step's cost, so a cheap infantry task force cannot keep the war factory from ever being paid.
        int reserve = BaseComplete(own, frame) ? 0 : Math.Max(600, NextBaseStepCost(own, frame));
        Dictionary<string, int> free = own.Where(e => IsCombat(e.TypeId) && !attackers.Contains(e.Id))
            .GroupBy(e => e.TypeId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (TaskForceSlot slot in CurrentTaskForce())
        {
            if (!rules.TryGet(slot.TypeId, out UnitRule rule)) continue;
            int have = free.GetValueOrDefault(slot.TypeId) + Queued(frame, slot.TypeId);
            if (have >= slot.Count || QueueBusy(frame, rule.Queue)) continue;
            if (!rules.CanBuild(faction, owned, slot.TypeId) || credits - reserve < rule.Cost) continue;
            Produce(commands, rule);
            credits -= rule.Cost;
        }
    }

    // ----- army -----

    private void CommandArmy(ObservationFrame frame, List<ObservedEntity> own, List<ObservedEntity> enemies, List<GameCommand> commands)
    {
        attackers.RemoveWhere(id => own.All(e => e.Id != id));
        List<ObservedEntity> combat = [.. own.Where(e => IsCombat(e.TypeId))];
        List<ObservedEntity> guards = [.. combat.Where(e => !attackers.Contains(e.Id))];

        // Base defence: home units attack-move on any region with own structures that enemy fighters entered.
        HashSet<RegionId> baseRegions = [.. own.Where(e => Rule(e.TypeId)?.Kind == EntityKind.Building).Select(e => RegionOf(e.Position))];
        RegionId? threatened = enemies.Where(e => IsCombat(e.TypeId) && baseRegions.Contains(RegionOf(e.Position)))
            .Select(e => (RegionId?)RegionOf(e.Position)).OrderBy(r => r!.Value.Value).FirstOrDefault();
        if (threatened is { } threat)
        {
            Cell centre = map.Regions.First(r => r.Id == threat).Center;
            List<EntityId> responders = [.. guards.Where(g => RegionOf(g.Position) != threat || NeedsOrder(g.Id, frame.Time)).Select(g => g.Id)];
            if (responders.Count > 0) Order(commands, responders, centre, frame.Time);
        }

        // Free units not at home gather there (the retail AI's team rally), so a task force forms in one place.
        if (threatened is null && home is { } homeRegion)
        {
            Cell rally = map.Regions.First(r => r.Id == homeRegion).Center;
            List<EntityId> strays = [.. guards.Where(g => RegionOf(g.Position) != homeRegion && NeedsOrder(g.Id, frame.Time)).Select(g => g.Id)];
            if (strays.Count > 0) Order(commands, strays, rally, frame.Time);
        }

        // Launch a wave once the task force is complete at home.
        if (threatened is null && frame.Time.Seconds >= EarliestAttack() && TaskForceReady(guards, out List<ObservedEntity> wave))
        {
            waveTarget = ChooseTarget(wave[0].Position);
            if (waveTarget is { } target)
            {
                foreach (ObservedEntity unit in wave) attackers.Add(unit.Id);
                wavesLaunched++;
                Order(commands, [.. wave.Select(w => w.Id)], map.Regions.First(r => r.Id == target).Center, frame.Time);
            }
        }

        // Attacking teams press on: at a target with no enemy structure left they move to the next one.
        List<ObservedEntity> team = [.. combat.Where(e => attackers.Contains(e.Id))];
        if (team.Count > 0)
        {
            if (waveTarget is { } current && TargetCleared(current, enemies, frame)) waveTarget = ChooseTarget(team[0].Position);
            if (waveTarget is { } target)
            {
                Cell centre = map.Regions.First(r => r.Id == target).Center;
                List<EntityId> stale = [.. team.Where(u => NeedsOrder(u.Id, frame.Time)).Select(u => u.Id)];
                if (stale.Count > 0) Order(commands, stale, centre, frame.Time);
            }
        }
    }

    private bool TaskForceReady(List<ObservedEntity> guards, out List<ObservedEntity> wave)
    {
        wave = [];
        List<ObservedEntity> atHome = [.. guards.Where(g => RegionOf(g.Position) == home)];
        foreach (TaskForceSlot slot in CurrentTaskForce())
        {
            List<ObservedEntity> ofType = [.. atHome.Where(g => g.TypeId == slot.TypeId).Take(slot.Count)];
            if (ofType.Count < slot.Count) { wave = []; return false; }
            wave.AddRange(ofType);
        }
        return wave.Count > 0;
    }

    private bool TargetCleared(RegionId target, List<ObservedEntity> enemies, ObservationFrame frame) =>
        frame.VisibleRegions.Contains(target)
        && !enemies.Any(e => RegionOf(e.Position) == target)
        && !knownEnemyBuildings.Values.Any(b => b.Region == target);

    /// <summary>The nearest region holding a remembered enemy structure, else the nearest uncleared enemy start, else the stalest region.</summary>
    private RegionId? ChooseTarget(Cell from)
    {
        RegionId origin = RegionOf(from);
        IReadOnlyDictionary<RegionId, double> distance = graph.DistancesFrom(origin);
        RegionId? building = knownEnemyBuildings.Values.Select(b => b.Region).Distinct()
            .Where(distance.ContainsKey).OrderBy(r => distance[r]).ThenBy(r => r.Value).Cast<RegionId?>().FirstOrDefault();
        if (building is not null) return building;
        RegionId? start = EnemyStarts().Where(r => !clearedStarts.Contains(r) && distance.ContainsKey(r))
            .OrderBy(r => distance[r]).ThenBy(r => r.Value).Cast<RegionId?>().FirstOrDefault();
        if (start is not null) return start;
        List<RegionId> reachable = [.. map.Regions.Where(r => distance.ContainsKey(r.Id) && r.Id != home).Select(r => r.Id).OrderBy(r => r.Value)];
        return reachable.Count == 0 ? null : reachable[rng.NextInt(reachable.Count)];
    }

    private IEnumerable<RegionId> EnemyStarts() =>
        map.Regions.Where(r => r.IsStartLocation && r.Id != home).Select(r => r.Id).OrderBy(r => r.Value);

    private bool NeedsOrder(EntityId id, GameTime now) =>
        !lastOrdered.TryGetValue(id, out GameTime at) || now.SecondsSince(at) >= ReorderSeconds;

    private void Order(List<GameCommand> commands, List<EntityId> units, Cell destination, GameTime now)
    {
        if (units.Count == 0) return;
        foreach (EntityId id in units) lastOrdered[id] = now;
        commands.Add(new AttackMoveCommand(Controller, units, destination));
    }

    // ----- script arithmetic -----

    private IReadOnlyList<TaskForceSlot> CurrentTaskForce()
    {
        double scale = difficulty switch { OpponentDifficulty.Easy => 0.5, OpponentDifficulty.Medium => 0.75, _ => 1.0 };
        int total = script.TaskForce.Sum(s => s.Count);
        int growth = Math.Min(wavesLaunched * script.WaveGrowth, Math.Max(0, script.MaxWaveUnits - total));
        List<TaskForceSlot> slots = [];
        for (int i = 0; i < script.TaskForce.Count; i++)
        {
            TaskForceSlot slot = script.TaskForce[i];
            int count = slot.Count + (i == 0 ? growth : 0);
            slots.Add(slot with { Count = Math.Max(1, (int)Math.Round(count * scale)) });
        }
        return slots;
    }

    private double EarliestAttack() => difficulty switch
    {
        OpponentDifficulty.Easy => Math.Max(script.EarliestAttackSeconds, 480),
        OpponentDifficulty.Medium => Math.Max(script.EarliestAttackSeconds, 300),
        _ => script.EarliestAttackSeconds,
    };

    /// <summary>Seeded pause between base build steps; retail AI "thinks" between structures, more slowly when easier.</summary>
    private double BuildPause() => difficulty switch
    {
        OpponentDifficulty.Easy => 10 + rng.NextInt(11),
        OpponentDifficulty.Medium => 4 + rng.NextInt(7),
        _ => rng.NextInt(3),
    };

    private bool BaseComplete(List<ObservedEntity> own, ObservationFrame frame) =>
        script.Build.All(s => Count(own, frame, s.TypeId) >= s.Count);

    private int NextBaseStepCost(List<ObservedEntity> own, ObservationFrame frame) =>
        script.Build.FirstOrDefault(s => Count(own, frame, s.TypeId) < s.Count) is { } step && rules.TryGet(step.TypeId, out UnitRule rule) ? rule.Cost : 0;

    private string? PowerType() => rules.All
        .Where(r => r.Factions.Contains(faction) && r.Role == UnitRole.Power && r.Kind == EntityKind.Building)
        .OrderBy(r => r.Cost).ThenBy(r => r.TypeId, StringComparer.Ordinal).Select(r => r.TypeId).FirstOrDefault();

    private bool QueueBusy(ObservationFrame frame, QueueKind kind) =>
        queuedThisTick.Contains(kind) || frame.Queues.Any(q => q.Kind == kind && q.Items.Count > 0);

    private void Produce(List<GameCommand> commands, UnitRule rule)
    {
        commands.Add(new ProduceCommand(Controller, rule.TypeId, rule.Queue));
        queuedThisTick.Add(rule.Queue);
    }

    private static int Queued(ObservationFrame frame, string typeId) =>
        frame.Queues.Sum(q => q.Items.Count(i => i.TypeId == typeId));

    private static int Count(List<ObservedEntity> own, ObservationFrame frame, string typeId) =>
        own.Count(e => e.TypeId == typeId) + Queued(frame, typeId);

    private HashSet<string> OwnedBuildingTypes(List<ObservedEntity> own) =>
        own.Where(e => Rule(e.TypeId)?.Kind == EntityKind.Building).Select(e => e.TypeId).ToHashSet(StringComparer.Ordinal);

    private bool IsCombat(string typeId) =>
        Rule(typeId) is { } r && r.Kind != EntityKind.Building && r.Damage > 0 && r.Weapon != WeaponClass.None;

    private UnitRule? Rule(string typeId) => rules.TryGet(typeId, out UnitRule r) ? r : null;

    private RegionId RegionOf(Cell cell) => map.RegionOf(cell)?.Id ?? map.Regions[0].Id;
}
