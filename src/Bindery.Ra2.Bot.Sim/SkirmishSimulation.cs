// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;

namespace Bindery.Ra2.Bot.Sim;

/// <summary>
/// An honest approximation of RA2 at region granularity for offline bot
/// development: the bindery region sim, not retail RA2. Economy, production,
/// movement and combat are resolved against <see cref="RegionGraph"/>
/// distances rather than real pathfinding or projectiles, so results are
/// directional, not a substitute for retail balance.
/// </summary>
public sealed class SkirmishSimulation
{
    private const double HarvesterHarvestSeconds = 4.0;
    private const double HarvesterUnloadSeconds = 1.0;
    private const int HarvesterLoadValue = 700;
    private const double PlacementRadiusCells = 24.0;
    private const double RepairHealPerSecondFraction = 0.04;

    private readonly SimMap map;
    private readonly IRulesDatabase rules;
    private readonly SimSettings settings;
    private readonly Xorshift rng;
    private readonly Dictionary<PlayerId, SimPlayerState> players = [];
    private readonly Dictionary<RegionId, PlayerId> startRegionOwner = [];
    private readonly List<SimEntity> entities = [];
    private readonly Dictionary<RegionId, double> oreRemaining = [];
    private readonly List<(PlayerId Player, GameCommand Command)> pending = [];
    private List<GameEvent> frameEvents = [];
    private uint nextEntityId = 1;

    public SkirmishSimulation(SimMap map, IRulesDatabase rules, SimSettings settings)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Players.Count > map.StartRegions.Count)
        {
            throw new ArgumentException("Map does not have enough start regions for the requested players.", nameof(settings));
        }

        this.map = map;
        this.rules = rules;
        this.settings = settings;
        rng = new Xorshift(settings.Seed);
        Graph = new RegionGraph(map.Map);
        Players = settings.Players.Select(p => p.Id).ToList();

        foreach (OreField field in map.Map.OreFields)
        {
            oreRemaining[field.Region] = oreRemaining.GetValueOrDefault(field.Region) + field.InitialValue;
        }

        for (int i = 0; i < settings.Players.Count; i++)
        {
            SimPlayer player = settings.Players[i];
            RegionId start = map.StartRegions[i];
            startRegionOwner[start] = player.Id;
            SimPlayerState state = new() { Id = player.Id, Faction = player.Faction, Credits = settings.StartingCredits };
            players[player.Id] = state;

            UnitRule? mcvRule = rules.All
                .Where(r => r.Factions.Contains(player.Faction) && r.Role == UnitRole.Mcv)
                .OrderBy(r => r.Cost).ThenBy(r => r.TypeId, StringComparer.Ordinal)
                .Cast<UnitRule?>().FirstOrDefault();
            if (mcvRule is { } rule)
            {
                Region region = RegionById(start);
                SpawnEntity(player.Id, rule.TypeId, region.Center, start);
            }
        }
    }

    public MapInfo Map => map.Map;

    public RegionGraph Graph { get; }

    public GameTime Time { get; private set; } = new(0);

    public bool MatchEnded { get; private set; }

    public PlayerId? Winner { get; private set; }

    /// <summary>"elimination" or "timeout"; null until <see cref="MatchEnded"/>.</summary>
    public string? EndReason { get; private set; }

    public IReadOnlyList<PlayerId> Players { get; }

    public int RejectedCommandCount(PlayerId player) => players.TryGetValue(player, out SimPlayerState? s) ? s.RejectedCommands : 0;

    public int AssetValue(PlayerId player)
    {
        if (!players.TryGetValue(player, out SimPlayerState? state)) return 0;
        int total = state.Credits;
        foreach (SimEntity e in entities)
        {
            if (e.Owner == player && e.Alive && rules.TryGet(e.TypeId, out UnitRule rule)) total += rule.Cost;
        }
        return total;
    }

    /// <summary>Queues a command to be applied at the start of the next <see cref="Step"/>.</summary>
    public void Submit(PlayerId player, GameCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        pending.Add((player, command));
    }

    /// <summary>Advances the simulation by one 1/15 s frame.</summary>
    public void Step()
    {
        if (MatchEnded) return;

        frameEvents = [];
        ApplyPendingCommands();
        AdvanceMovement();
        Time = Time.Plus(1.0 / GameTime.FramesPerSecond);

        if (Time.Frame % GameTime.FramesPerSecond == 0)
        {
            AdvanceEconomy();
            AdvanceProduction();
            ResolveCombat();
            AdvanceRepair();
        }

        foreach (SimPlayerState state in players.Values.OrderBy(p => p.Id.Value))
        {
            state.PendingEvents.AddRange(frameEvents.Where(e => IsEventForPlayer(e, state.Id)));
        }

        CheckVictory();
    }

    /// <summary>Builds this player's observation frame for the current time.</summary>
    public ObservationFrame Observe(PlayerId player, ObservationMode mode = ObservationMode.Belief)
    {
        SimPlayerState state = players[player];
        IReadOnlySet<RegionId> visible = mode == ObservationMode.Oracle
            ? map.Map.Regions.Select(r => r.Id).ToHashSet()
            : VisibleRegionsFor(player);

        List<ObservedEntity> observed = [];
        foreach (SimEntity e in entities.OrderBy(e => e.Id.Value))
        {
            if (!e.Alive) continue;
            bool own = e.Owner == player;
            bool visibleEnemy = mode == ObservationMode.Oracle || (own is false && visible.Contains(e.Region));
            if (!own && !visibleEnemy) continue;
            rules.TryGet(e.TypeId, out UnitRule rule);
            observed.Add(new ObservedEntity(e.Id, e.Owner, e.TypeId, e.Position, e.Health, e.MaxHealth, e.Deployed, own ? e.ExplicitTarget : null));
        }

        List<ProductionQueueState> queues = [];
        foreach (QueueRuntime q in state.Queues.Values.OrderBy(q => q.Kind))
        {
            List<QueueItem> items = q.Items.Select((i, idx) => new QueueItem(i.TypeId, Math.Clamp(i.Progress, 0, 1), i.Ready, OnHold: idx > 0)).ToList();
            queues.Add(new ProductionQueueState(q.Kind, items, CountFactories(state)));
        }

        List<GameEvent> events = mode == ObservationMode.Oracle
            ? frameEvents
            : state.PendingEvents.Where(e => IsEventForPlayer(e, player)).ToList();

        PowerState power = ComputePower(state);
        return new ObservationFrame(Time, mode, player, state.Faction, state.Credits, power, observed, queues, events, visible, map.Map);
    }

    /// <summary>Deterministic digest of all visible-and-hidden state, for replay verification.</summary>
    public string ComputeStateHash()
    {
        StringBuilder sb = new();
        sb.Append(Time.Frame).Append('|');
        foreach (SimPlayerState p in players.Values.OrderBy(p => p.Id.Value))
        {
            sb.Append(p.Id.Value).Append(':').Append(p.Credits).Append(':').Append(p.RejectedCommands).Append(';');
        }
        foreach (SimEntity e in entities.OrderBy(e => e.Id.Value))
        {
            sb.Append(e.Id.Value).Append(':').Append(e.Owner.Value).Append(':').Append(e.TypeId).Append(':')
              .Append(e.Health).Append(':').Append(e.Position.X).Append(',').Append(e.Position.Y).Append(';');
        }
        foreach ((RegionId region, double remaining) in oreRemaining.OrderBy(kv => kv.Key.Value))
        {
            sb.Append(region.Value).Append('=').Append(remaining).Append(';');
        }
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    // ----- hidden-state access for SimLeakageProbe only -----

    internal RegionId StartRegionOf(PlayerId player) => startRegionOwner.First(kv => kv.Value == player).Key;

    internal IReadOnlySet<RegionId> VisibleRegionsForProbe(PlayerId player) => VisibleRegionsFor(player);

    internal void DebugAdjustCredits(PlayerId player, int delta) => players[player].Credits += delta;

    internal void DebugEnqueue(PlayerId player, QueueKind kind, string typeId)
    {
        SimPlayerState state = players[player];
        QueueRuntime queue = state.Queues.TryGetValue(kind, out QueueRuntime? existing) ? existing : state.Queues[kind] = new QueueRuntime { Kind = kind };
        queue.Items.Add(new QueueItemRuntime { TypeId = typeId });
    }

    /// <summary>Spawns an entity without emitting a creation event, for probing fog in a region the caller has already checked is unseen.</summary>
    internal void DebugSpawnSilently(PlayerId owner, string typeId, RegionId region)
    {
        UnitRule rule = rules.Get(typeId);
        Cell position = RegionCenter(region);
        SimEntity entity = new()
        {
            Id = new EntityId(nextEntityId++),
            Owner = owner,
            TypeId = typeId,
            Position = position,
            Region = region,
            Health = rule.Strength,
            MaxHealth = rule.Strength,
        };
        entity.SnapTo(position);
        entities.Add(entity);
    }

    // ----- setup helpers -----

    private Region RegionById(RegionId id) => map.Map.Regions.First(r => r.Id == id);

    private Cell RegionCenter(RegionId id) => RegionById(id).Center;

    private SimEntity SpawnEntity(PlayerId owner, string typeId, Cell position, RegionId region)
    {
        UnitRule rule = rules.Get(typeId);
        SimEntity entity = new()
        {
            Id = new EntityId(nextEntityId++),
            Owner = owner,
            TypeId = typeId,
            Position = position,
            Region = region,
            Health = rule.Strength,
            MaxHealth = rule.Strength,
        };
        entity.SnapTo(position);
        entities.Add(entity);
        frameEvents.Add(new GameEvent(GameEventKind.EntityCreated, Time, entity.Id, owner, typeId, position));
        return entity;
    }

    // ----- command application -----

    private void ApplyPendingCommands()
    {
        foreach ((PlayerId player, GameCommand command) in pending)
        {
            if (!players.TryGetValue(player, out SimPlayerState? state)) continue;
            bool ok = Apply(player, state, command);
            if (!ok) state.RejectedCommands++;
        }
        pending.Clear();
    }

    private bool Apply(PlayerId player, SimPlayerState state, GameCommand command) => command switch
    {
        ProduceCommand c => ApplyProduce(player, state, c),
        CancelProductionCommand c => ApplyCancel(state, c),
        PlaceBuildingCommand c => ApplyPlace(player, state, c),
        SellCommand c => ApplySell(player, state, c),
        MoveCommand c => ApplyMove(player, c, attackMove: false),
        AttackMoveCommand c => ApplyMove(player, c.Controller, c.Units, c.Destination, attackMove: true),
        AttackCommand c => ApplyAttack(player, c),
        StopCommand c => ApplyStop(player, c),
        DeployCommand c => ApplyDeploy(player, state, c),
        RepairCommand c => ApplyRepair(player, c),
        HarvestCommand c => ApplyHarvest(player, c),
        SetRallyPointCommand => true,
        _ => false,
    };

    private bool OwnsAlive(PlayerId player, EntityId id, out SimEntity entity)
    {
        SimEntity? found = entities.Find(e => e.Id == id);
        if (found is null || found.Owner != player || !found.Alive) { entity = null!; return false; }
        entity = found;
        return true;
    }

    private HashSet<string> OwnedBuildingTypes(PlayerId player) =>
        entities.Where(e => e.Owner == player && e.Alive && rules.TryGet(e.TypeId, out UnitRule r) && r.Kind == EntityKind.Building)
                .Select(e => e.TypeId).ToHashSet();

    private bool ApplyProduce(PlayerId player, SimPlayerState state, ProduceCommand c)
    {
        if (!rules.TryGet(c.TypeId, out UnitRule rule) || rule.Queue != c.Queue) return false;
        if (!rules.CanBuild(state.Faction, OwnedBuildingTypes(player), c.TypeId)) return false;
        if (state.Credits < rule.Cost) return false;
        state.Credits -= rule.Cost;
        QueueRuntime queue = state.Queues.TryGetValue(c.Queue, out QueueRuntime? existing) ? existing : state.Queues[c.Queue] = new QueueRuntime { Kind = c.Queue };
        queue.Items.Add(new QueueItemRuntime { TypeId = c.TypeId });
        return true;
    }

    private bool ApplyCancel(SimPlayerState state, CancelProductionCommand c)
    {
        if (!state.Queues.TryGetValue(c.Queue, out QueueRuntime? queue)) return false;
        int index = queue.Items.FindLastIndex(i => i.TypeId == c.TypeId);
        if (index < 0) return false;
        if (rules.TryGet(c.TypeId, out UnitRule rule)) state.Credits += rule.Cost;
        queue.Items.RemoveAt(index);
        return true;
    }

    private bool ApplyPlace(PlayerId player, SimPlayerState state, PlaceBuildingCommand c)
    {
        int index = state.PendingPlacements.FindIndex(p => p.TypeId == c.TypeId);
        if (index < 0) return false;
        bool nearOwnBuilding = entities.Any(e => e.Owner == player && e.Alive && rules.TryGet(e.TypeId, out UnitRule r) && r.Kind == EntityKind.Building
                                                  && e.Position.DistanceTo(c.Cell) <= PlacementRadiusCells);
        if (!nearOwnBuilding) return false;
        Region? region = map.Map.RegionOf(c.Cell);
        if (region is null) return false;
        PendingPlacement placement = state.PendingPlacements[index];
        state.PendingPlacements.RemoveAt(index);
        if (state.Queues.TryGetValue(placement.Queue, out QueueRuntime? queue))
        {
            int itemIndex = queue.Items.FindIndex(i => i.TypeId == c.TypeId && i.AwaitingPlacement);
            if (itemIndex >= 0) queue.Items.RemoveAt(itemIndex);
        }
        SpawnEntity(player, c.TypeId, c.Cell, region.Id);
        return true;
    }

    private bool ApplySell(PlayerId player, SimPlayerState state, SellCommand c)
    {
        if (!OwnsAlive(player, c.Building, out SimEntity entity)) return false;
        if (!rules.TryGet(entity.TypeId, out UnitRule rule) || rule.Kind != EntityKind.Building) return false;
        state.Credits += rule.Cost / 2;
        Destroy(entity, "sold");
        return true;
    }

    private bool ApplyMove(PlayerId player, MoveCommand c, bool attackMove) => ApplyMove(player, c.Controller, c.Units, c.Destination, attackMove);

    private bool ApplyMove(PlayerId player, string _, IReadOnlyList<EntityId> units, Cell destination, bool attackMove)
    {
        bool any = false;
        Region? destRegion = map.Map.RegionOf(destination);
        if (destRegion is null) return false;
        foreach (EntityId id in units)
        {
            if (!OwnsAlive(player, id, out SimEntity entity)) continue;
            IReadOnlyList<RegionId> path = Graph.Path(entity.Region, destRegion.Id);
            if (path.Count == 0) continue;
            entity.RemainingPath.Clear();
            for (int i = 1; i < path.Count; i++) entity.RemainingPath.Add(path[i]);
            entity.FinalDestination = destination;
            entity.HoldForCombat = attackMove;
            entity.ExplicitTarget = null;
            entity.Phase = HarvesterPhase.Idle;
            any = true;
        }
        return any;
    }

    private bool ApplyAttack(PlayerId player, AttackCommand c)
    {
        SimEntity? target = entities.Find(e => e.Id == c.Target && e.Alive);
        if (target is null) return false;
        bool any = false;
        foreach (EntityId id in c.Units)
        {
            if (!OwnsAlive(player, id, out SimEntity entity)) continue;
            entity.ExplicitTarget = target.Id;
            if (entity.Region != target.Region)
            {
                IReadOnlyList<RegionId> path = Graph.Path(entity.Region, target.Region);
                if (path.Count > 0)
                {
                    entity.RemainingPath.Clear();
                    for (int i = 1; i < path.Count; i++) entity.RemainingPath.Add(path[i]);
                    entity.FinalDestination = RegionCenter(target.Region);
                    entity.HoldForCombat = true;
                }
            }
            any = true;
        }
        return any;
    }

    private bool ApplyStop(PlayerId player, StopCommand c)
    {
        bool any = false;
        foreach (EntityId id in c.Units)
        {
            if (!OwnsAlive(player, id, out SimEntity entity)) continue;
            entity.RemainingPath.Clear();
            entity.FinalDestination = null;
            entity.ExplicitTarget = null;
            entity.RepairRequested = false;
            entity.HoldForCombat = false;
            any = true;
        }
        return any;
    }

    private bool ApplyDeploy(PlayerId player, SimPlayerState state, DeployCommand c)
    {
        if (!OwnsAlive(player, c.Unit, out SimEntity entity)) return false;
        if (!rules.TryGet(entity.TypeId, out UnitRule rule) || !rule.Deployable) return false;
        if (rule.Role != UnitRole.Mcv)
        {
            entity.Deployed = true;
            return true;
        }
        UnitRule? yard = rules.All
            .Where(r => r.Factions.Contains(state.Faction) && r.Role == UnitRole.Production && r.Kind == EntityKind.Building)
            .OrderBy(r => r.TechLevel).ThenBy(r => r.Cost).ThenBy(r => r.TypeId, StringComparer.Ordinal)
            .Cast<UnitRule?>().FirstOrDefault();
        if (yard is not { } yardRule) return false;
        entity.TypeId = yardRule.TypeId;
        entity.MaxHealth = yardRule.Strength;
        entity.Health = yardRule.Strength;
        entity.Deployed = true;
        entity.RemainingPath.Clear();
        return true;
    }

    private bool ApplyRepair(PlayerId player, RepairCommand c)
    {
        if (!OwnsAlive(player, c.Unit, out SimEntity entity)) return false;
        entity.RepairRequested = true;
        return true;
    }

    private bool ApplyHarvest(PlayerId player, HarvestCommand c)
    {
        if (!OwnsAlive(player, c.Harvester, out SimEntity entity)) return false;
        Region? region = map.Map.RegionOf(c.Ore);
        if (region is null || !region.HasOre || oreRemaining.GetValueOrDefault(region.Id) <= 0) return false;
        entity.AssignedOreRegion = region.Id;
        entity.Phase = HarvesterPhase.Idle;
        entity.RemainingPath.Clear();
        return true;
    }

    // ----- movement -----

    private void AdvanceMovement()
    {
        foreach (SimEntity e in entities)
        {
            if (!e.Alive || e.RemainingPath.Count == 0) continue;
            if (!rules.TryGet(e.TypeId, out UnitRule rule) || rule.Speed <= 0) { e.RemainingPath.Clear(); continue; }
            if (e.HoldForCombat && HasHostilesInRegion(e)) continue;

            double stepCells = rule.Speed / GameTime.FramesPerSecond;
            Cell target = e.RemainingPath.Count == 1 && e.FinalDestination is { } dest ? dest : RegionCenter(e.RemainingPath[0]);
            double dx = target.X - e.ExactX, dy = target.Y - e.ExactY;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance <= stepCells || distance == 0)
            {
                e.SnapTo(target);
                e.Region = e.RemainingPath[0];
                e.RemainingPath.RemoveAt(0);
                if (e.RemainingPath.Count == 0) e.HoldForCombat = false;
            }
            else
            {
                double t = stepCells / distance;
                e.ExactX += dx * t;
                e.ExactY += dy * t;
                e.Position = new Cell((int)Math.Round(e.ExactX), (int)Math.Round(e.ExactY));
            }
        }
    }

    private bool HasHostilesInRegion(SimEntity e) => entities.Any(o => o.Alive && o.Owner != e.Owner && o.Region == e.Region);

    // ----- economy -----

    private void AdvanceEconomy()
    {
        foreach (SimEntity e in entities)
        {
            if (!e.Alive || !rules.TryGet(e.TypeId, out UnitRule rule) || rule.Role != UnitRole.Harvester) continue;
            SimPlayerState state = players[e.Owner];
            StepHarvester(e, rule, state);
        }
    }

    private void StepHarvester(SimEntity e, UnitRule rule, SimPlayerState state)
    {
        switch (e.Phase)
        {
            case HarvesterPhase.Idle:
                RegionId? oreRegion = e.AssignedOreRegion is { } assigned && oreRemaining.GetValueOrDefault(assigned) > 0
                    ? assigned
                    : NearestOreRegion(e.Region);
                if (oreRegion is null) return;
                double toOre = Graph.TravelSeconds(e.Region, oreRegion.Value, rule.Speed);
                if (double.IsInfinity(toOre)) return;
                e.AssignedOreRegion = oreRegion;
                e.Phase = HarvesterPhase.ToOre;
                e.PhaseSecondsRemaining = Math.Max(toOre, 1.0 / GameTime.FramesPerSecond);
                break;

            case HarvesterPhase.ToOre:
                e.PhaseSecondsRemaining -= 1;
                if (e.PhaseSecondsRemaining <= 0 && e.AssignedOreRegion is { } target)
                {
                    e.Region = target;
                    e.SnapTo(RegionCenter(target));
                    e.Phase = HarvesterPhase.Harvesting;
                    e.PhaseSecondsRemaining = HarvesterHarvestSeconds;
                }
                break;

            case HarvesterPhase.Harvesting:
                e.PhaseSecondsRemaining -= 1;
                if (e.PhaseSecondsRemaining <= 0)
                {
                    double available = e.AssignedOreRegion is { } r ? oreRemaining.GetValueOrDefault(r) : 0;
                    int load = (int)Math.Min(HarvesterLoadValue, available);
                    if (e.AssignedOreRegion is { } depletedRegion) oreRemaining[depletedRegion] = Math.Max(0, available - load);
                    e.CarriedValue = load;
                    e.Phase = HarvesterPhase.Idle;
                    if (load > 0)
                    {
                        RegionId? refineryRegion = NearestRefineryRegion(e.Owner, e.Region);
                        if (refineryRegion is not null)
                        {
                            double toRefinery = Graph.TravelSeconds(e.Region, refineryRegion.Value, rule.Speed);
                            if (!double.IsInfinity(toRefinery))
                            {
                                e.TargetRefineryRegion = refineryRegion;
                                e.Phase = HarvesterPhase.ToRefinery;
                                e.PhaseSecondsRemaining = Math.Max(toRefinery, 1.0 / GameTime.FramesPerSecond);
                            }
                        }
                    }
                }
                break;

            case HarvesterPhase.ToRefinery:
                e.PhaseSecondsRemaining -= 1;
                if (e.PhaseSecondsRemaining <= 0 && e.TargetRefineryRegion is { } refinery)
                {
                    e.Region = refinery;
                    e.SnapTo(RegionCenter(refinery));
                    e.Phase = HarvesterPhase.Unloading;
                    e.PhaseSecondsRemaining = HarvesterUnloadSeconds;
                }
                break;

            case HarvesterPhase.Unloading:
                e.PhaseSecondsRemaining -= 1;
                if (e.PhaseSecondsRemaining <= 0)
                {
                    state.Credits += e.CarriedValue;
                    e.CarriedValue = 0;
                    e.Phase = HarvesterPhase.Idle;
                }
                break;
        }
    }

    private RegionId? NearestOreRegion(RegionId from)
    {
        IReadOnlyDictionary<RegionId, double> distances = Graph.DistancesFrom(from);
        return oreRemaining.Where(kv => kv.Value > 0 && distances.ContainsKey(kv.Key))
                            .OrderBy(kv => distances[kv.Key]).ThenBy(kv => kv.Key.Value)
                            .Select(kv => (RegionId?)kv.Key).FirstOrDefault();
    }

    private RegionId? NearestRefineryRegion(PlayerId owner, RegionId from)
    {
        IReadOnlyDictionary<RegionId, double> distances = Graph.DistancesFrom(from);
        return entities.Where(e => e.Owner == owner && e.Alive && rules.TryGet(e.TypeId, out UnitRule r) && r.Role == UnitRole.Economy && distances.ContainsKey(e.Region))
                        .OrderBy(e => distances[e.Region]).ThenBy(e => e.Id.Value)
                        .Select(e => (RegionId?)e.Region).FirstOrDefault();
    }

    // ----- production -----

    private void AdvanceProduction()
    {
        foreach (SimPlayerState state in players.Values.OrderBy(p => p.Id.Value))
        {
            if (state.Defeated) continue;
            PowerState power = ComputePower(state);
            int factories = CountFactories(state);
            double multiplier = factories <= 0 ? 0 : Math.Sqrt(factories) * (power.LowPower ? 0.5 : 1.0);
            foreach (QueueRuntime queue in state.Queues.Values.OrderBy(q => q.Kind))
            {
                if (queue.Items.Count == 0 || multiplier <= 0) continue;
                QueueItemRuntime active = queue.Items[0];
                if (active.AwaitingPlacement) continue;
                if (!rules.TryGet(active.TypeId, out UnitRule rule) || rule.BuildSeconds <= 0) { queue.Items.RemoveAt(0); continue; }
                active.Progress = Math.Min(1.0, active.Progress + multiplier / rule.BuildSeconds);
                if (active.Progress >= 1.0)
                {
                    frameEvents.Add(new GameEvent(GameEventKind.ProductionCompleted, Time, null, state.Id, rule.TypeId, null));
                    if (rule.Kind == EntityKind.Building)
                    {
                        // Stays in the queue (blocking it) until PlaceBuildingCommand succeeds, matching RA2's "ready to place" behaviour.
                        active.AwaitingPlacement = true;
                        state.PendingPlacements.Add(new PendingPlacement { TypeId = rule.TypeId, Queue = queue.Kind });
                    }
                    else
                    {
                        queue.Items.RemoveAt(0);
                        Cell spawnAt = RallyPointFor(state.Id);
                        Region region = map.Map.RegionOf(spawnAt) ?? RegionById(map.StartRegions[0]);
                        SpawnEntity(state.Id, rule.TypeId, spawnAt, region.Id);
                    }
                }
            }
        }
    }

    private Cell RallyPointFor(PlayerId owner)
    {
        SimEntity? factory = entities.Where(e => e.Owner == owner && e.Alive && rules.TryGet(e.TypeId, out UnitRule r) && r.Kind == EntityKind.Building)
                                      .OrderBy(e => e.Id.Value).LastOrDefault();
        if (factory is not null) return new Cell(factory.Position.X + 1 + rng.NextInt(3), factory.Position.Y + 1 + rng.NextInt(3));
        int index = players.Keys.OrderBy(p => p.Value).ToList().IndexOf(owner);
        return RegionCenter(map.StartRegions[Math.Max(0, index)]);
    }

    private int CountFactories(SimPlayerState state) =>
        entities.Count(e => e.Owner == state.Id && e.Alive && rules.TryGet(e.TypeId, out UnitRule r) && r.Role == UnitRole.Production);

    private PowerState ComputePower(SimPlayerState state)
    {
        int produced = 0, drained = 0;
        foreach (SimEntity e in entities)
        {
            if (e.Owner != state.Id || !e.Alive || !rules.TryGet(e.TypeId, out UnitRule rule)) continue;
            if (rule.Power > 0) produced += rule.Power; else drained += -rule.Power;
        }
        return new PowerState(produced, drained);
    }

    // ----- combat -----

    private void ResolveCombat()
    {
        Dictionary<RegionId, List<SimEntity>> byRegion = [];
        foreach (SimEntity e in entities)
        {
            if (!e.Alive) continue;
            (byRegion.TryGetValue(e.Region, out List<SimEntity>? list) ? list : byRegion[e.Region] = []).Add(e);
        }

        foreach ((RegionId region, List<SimEntity> present) in byRegion.OrderBy(kv => kv.Key.Value))
        {
            if (present.Select(e => e.Owner).Distinct().Count() < 2) continue;
            Dictionary<EntityId, double> totalDamage = [];
            Dictionary<EntityId, (EntityId Attacker, double Amount)> topAttacker = [];

            foreach (SimEntity attacker in present.OrderBy(e => e.Id.Value))
            {
                if (!rules.TryGet(attacker.TypeId, out UnitRule rule) || rule.Weapon == WeaponClass.None || rule.Range <= 0 || rule.Damage <= 0) continue;
                SimEntity? target = ChooseTarget(attacker, rule, present);
                if (target is null) continue;
                double amount = rule.Damage * rules.Effectiveness(attacker.TypeId, target.TypeId);
                if (amount <= 0) continue;
                totalDamage[target.Id] = totalDamage.GetValueOrDefault(target.Id) + amount;
                if (!topAttacker.TryGetValue(target.Id, out (EntityId Attacker, double Amount) best) || amount > best.Amount)
                {
                    topAttacker[target.Id] = (attacker.Id, amount);
                }
            }

            foreach ((EntityId targetId, double damage) in totalDamage.OrderBy(kv => kv.Key.Value))
            {
                SimEntity target = present.First(e => e.Id == targetId);
                target.Health = Math.Max(0, target.Health - (int)Math.Round(damage));
                frameEvents.Add(new GameEvent(GameEventKind.UnderAttack, Time, target.Id, target.Owner, target.TypeId, target.Position));
                if (target.Health <= 0)
                {
                    PlayerId? killer = topAttacker.TryGetValue(targetId, out (EntityId Attacker, double Amount) top)
                        ? entities.FirstOrDefault(e => e.Id == top.Attacker)?.Owner
                        : null;
                    Destroy(target, "combat", killer);
                }
            }
        }
    }

    private SimEntity? ChooseTarget(SimEntity attacker, UnitRule attackerRule, List<SimEntity> present)
    {
        if (attacker.ExplicitTarget is { } explicitId)
        {
            SimEntity? explicitTarget = present.Find(e => e.Id == explicitId && e.Alive && e.Owner != attacker.Owner);
            if (explicitTarget is not null && CanTarget(attackerRule, explicitTarget)) return explicitTarget;
        }
        return present.Where(e => e.Alive && e.Owner != attacker.Owner && CanTarget(attackerRule, e))
                       .OrderBy(e => e.Health).ThenBy(e => e.Id.Value)
                       .FirstOrDefault();
    }

    private bool CanTarget(UnitRule attackerRule, SimEntity target)
    {
        if (!rules.TryGet(target.TypeId, out UnitRule targetRule)) return false;
        // AA-only weapons can hit aircraft only; everything else can hit aircraft only if it is a
        // dedicated AA or general-purpose weapon.
        if (attackerRule.AntiAir) return targetRule.Kind == EntityKind.Aircraft;
        if (targetRule.Kind == EntityKind.Aircraft) return attackerRule.Weapon == WeaponClass.General;
        return true;
    }

    private void AdvanceRepair()
    {
        foreach (SimEntity e in entities)
        {
            if (!e.Alive || !e.RepairRequested) continue;
            if (!rules.TryGet(e.TypeId, out UnitRule rule)) continue;
            if (e.Health >= e.MaxHealth) { e.RepairRequested = false; continue; }
            e.Health = Math.Min(e.MaxHealth, e.Health + Math.Max(1, (int)Math.Round(e.MaxHealth * RepairHealPerSecondFraction)));
        }
    }

    private void Destroy(SimEntity entity, string reason, PlayerId? killer = null)
    {
        entity.Health = 0;
        entity.RemainingPath.Clear();
        frameEvents.Add(new GameEvent(GameEventKind.EntityDestroyed, Time, entity.Id, entity.Owner, entity.TypeId, entity.Position, reason));
        if (killer is { } killerPlayer && killerPlayer != entity.Owner)
        {
            frameEvents.Add(new GameEvent(GameEventKind.EntityKilledByUs, Time, entity.Id, killerPlayer, entity.TypeId, entity.Position, reason));
        }
    }

    // ----- fog -----

    private HashSet<RegionId> VisibleRegionsFor(PlayerId player)
    {
        HashSet<RegionId> owned = entities.Where(e => e.Owner == player && e.Alive).Select(e => e.Region).ToHashSet();
        HashSet<RegionId> visible = new(owned);
        foreach (SimEntity e in entities)
        {
            if (e.Owner != player || !e.Alive || !rules.TryGet(e.TypeId, out UnitRule rule) || rule.Sight <= 0) continue;
            Region home = RegionById(e.Region);
            foreach (RegionId neighbour in Graph.Neighbours(e.Region))
            {
                double linkDistance = Graph.Distance(e.Region, neighbour);
                if (rule.Sight >= home.Radius + linkDistance) visible.Add(neighbour);
            }
        }
        return visible;
    }

    private bool IsEventForPlayer(GameEvent gameEvent, PlayerId player)
    {
        if (gameEvent.Owner == player) return true;
        if (gameEvent.Position is { } position)
        {
            Region? region = map.Map.RegionOf(position);
            if (region is not null && VisibleRegionsFor(player).Contains(region.Id)) return true;
        }
        return false;
    }

    // ----- victory -----

    private void CheckVictory()
    {
        foreach (SimPlayerState state in players.Values)
        {
            if (state.Defeated) continue;
            bool hasBuildingOrMcv = entities.Any(e => e.Owner == state.Id && e.Alive
                && rules.TryGet(e.TypeId, out UnitRule r) && (r.Kind == EntityKind.Building || r.Role == UnitRole.Mcv));
            if (!hasBuildingOrMcv) state.Defeated = true;
        }

        List<SimPlayerState> alive = players.Values.Where(p => !p.Defeated).OrderBy(p => p.Id.Value).ToList();
        if (alive.Count <= 1)
        {
            MatchEnded = true;
            Winner = alive.Count == 1 ? alive[0].Id : null;
            EndReason = "elimination";
            return;
        }

        if (Time.Seconds >= settings.MaxSeconds)
        {
            MatchEnded = true;
            Winner = alive.OrderByDescending(p => AssetValue(p.Id)).ThenBy(p => p.Id.Value).First().Id;
            EndReason = "timeout";
        }
    }
}
