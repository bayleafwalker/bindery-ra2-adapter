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

    /// <summary>How close to an own service depot a unit must stand to be repaired.</summary>
    private const double RepairRangeCells = 3.0;

    /// <summary>
    /// A new building may not stand closer than this to any live building: RA2 refuses a placement that overlaps
    /// another structure's footprint (two cells covers the fixture's 2x2 footprints).
    /// </summary>
    private const double PlacementClearanceCells = 2.0;

    private readonly SimMap map;
    private readonly IRulesDatabase rules;
    private readonly SimSettings settings;
    private readonly Xorshift rng;
    private readonly Xorshift combatRng;
    private readonly Dictionary<PlayerId, SimPlayerState> players = [];
    private readonly Dictionary<RegionId, PlayerId> startRegionOwner = [];
    private readonly List<SimEntity> entities = [];
    private readonly Dictionary<RegionId, double> oreRemaining = [];
    private readonly List<(PlayerId Player, GameCommand Command)> pending = [];
    private readonly Dictionary<QueueKind, HashSet<string>> factoryTypes;

    /// <summary>Queues some building declares itself a factory for (<see cref="UnitRule.Produces"/>); only those count for them.</summary>
    private readonly HashSet<QueueKind> declaredFactoryQueues;
    private List<GameEvent> frameEvents = [];
    // Each player's objects are numbered in a range of their own. One shared counter would let a player read the
    // enemy's hidden production off the gap between two of its own ids, which RA2 never shows.
    private const uint EntityIdsPerPlayer = 0x0100_0000;
    private readonly Dictionary<PlayerId, uint> nextEntityIdByOwner = [];

    // Probe spawns take ids from a separate range, so a perturbed simulation hands out the same ids to everything
    // built afterwards as its unperturbed twin (a shifted id would be a visible difference that is not a leak).
    private uint nextProbeEntityId = 0xF000_0000;

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
        // A separate stream, so turning combat noise on never shifts the rally-point sequence.
        combatRng = new Xorshift(unchecked(settings.Seed * 7919 + 104729));
        if (settings.CombatNoise is < 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(settings), "Combat noise must be in [0, 1).");
        Graph = new RegionGraph(map.Map);
        Players = settings.Players.Select(p => p.Id).ToList();
        factoryTypes = FactoryTypesByQueue(rules);
        declaredFactoryQueues = [.. rules.All.Where(static r => r.Kind == EntityKind.Building).SelectMany(static r => r.Produces ?? [])];

        foreach (OreField field in map.Map.OreFields)
        {
            oreRemaining[field.Region] = oreRemaining.GetValueOrDefault(field.Region) + field.InitialValue;
        }

        for (int i = 0; i < settings.Players.Count; i++)
        {
            SimPlayer player = settings.Players[i];
            RegionId start = map.StartRegions[i];
            startRegionOwner[start] = player.Id;
            SimPlayerState state = new() { Id = player.Id, Faction = player.Faction, Credits = player.StartingCredits ?? settings.StartingCredits, IncomeMultiplier = player.IncomeMultiplier };
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

    /// <summary>The most recent commands (up to 32) the simulator rejected for a player, oldest first; for diagnostics.</summary>
    public IReadOnlyList<GameCommand> RecentRejections(PlayerId player) =>
        players.TryGetValue(player, out SimPlayerState? s) ? [.. s.RecentRejections] : [];

    /// <summary>
    /// Credits, plus every live object at <see cref="SimValuation.ValueOf"/>, plus what has been paid for every queued
    /// item (production is paid as it builds, so a finished building waiting for placement counts in full). The
    /// timeout winner is decided by it, so value must not vanish into production that has not finished yet.
    /// </summary>
    public int AssetValue(PlayerId player)
    {
        if (!players.TryGetValue(player, out SimPlayerState? state)) return 0;
        int total = state.Credits;
        foreach (SimEntity e in entities)
        {
            if (e.Owner == player && e.Alive) total += SimValuation.ValueOf(rules, e.TypeId);
        }
        foreach (QueueRuntime queue in state.Queues.Values)
        {
            foreach (QueueItemRuntime item in queue.Items)
            {
                total += item.Paid;
            }
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
            AdvanceSuperweapons();
            DropTargetsInFog();
            ResolveCombat();
            AdvanceRepair();
        }

        // Each observation carries the events of the most recent step only: a frame is a set of new facts, and
        // re-delivering old events would make every consumer de-duplicate (and grow without bound over a match).
        foreach (SimPlayerState state in players.Values.OrderBy(p => p.Id.Value))
        {
            state.PendingEvents.Clear();
            if (frameEvents.Count == 0) continue;
            HashSet<RegionId> visible = VisibleRegionsFor(state.Id);
            foreach (GameEvent gameEvent in frameEvents)
            {
                if (EventForPlayer(gameEvent, state.Id, visible) is { } delivered) state.PendingEvents.Add(delivered);
            }
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
            // Visibility is decided by the cell the object is reported at (the rule events use too), so a frame can
            // never carry a position in a region it does not see.
            bool visibleEnemy = mode == ObservationMode.Oracle
                || (own is false && map.Map.RegionOf(e.Position) is { } at && visible.Contains(at.Id));
            if (!own && !visibleEnemy) continue;
            rules.TryGet(e.TypeId, out UnitRule rule);
            observed.Add(new ObservedEntity(e.Id, e.Owner, e.TypeId, e.Position, e.Health, e.MaxHealth, e.Deployed, own ? e.ExplicitTarget : null));
        }

        List<ProductionQueueState> queues = [];
        foreach (QueueRuntime q in state.Queues.Values.OrderBy(q => q.Kind))
        {
            List<QueueItem> items = q.Items.Select((i, idx) => new QueueItem(i.TypeId, Math.Clamp(i.Progress, 0, 1), i.Ready, OnHold: idx > 0)).ToList();
            queues.Add(new ProductionQueueState(q.Kind, items, CountFactories(state, q.Kind)));
        }

        // Oracle frames see every region, so they take this step's events through the same per-player rule with all
        // regions visible: another player's kills stay that player's, as in belief frames.
        List<GameEvent> events = mode == ObservationMode.Oracle
            ? [.. frameEvents.Select(e => EventForPlayer(e, player, [.. visible])).OfType<GameEvent>()]
            : [.. state.PendingEvents];

        PowerState power = ComputePower(state);
        Dictionary<RegionId, int> ore = oreRemaining
            .Where(kv => visible.Contains(kv.Key))
            .OrderBy(static kv => kv.Key.Value)
            .ToDictionary(static kv => kv.Key, static kv => (int)Math.Round(kv.Value));
        // Every other player is an enemy in a sim skirmish (no teams, no neutral houses).
        HashSet<PlayerId> enemies = [.. players.Keys.Where(p => p != player)];
        return new ObservationFrame(Time, mode, player, state.Faction, state.Credits, power, observed, queues, events, visible, map.Map, ore,
            SuperweaponTimers(player, mode), Enemies: enemies);
    }

    /// <summary>
    /// Deterministic digest of all visible-and-hidden state, for replay verification. It covers everything a later
    /// frame depends on (queues and their progress, pending placements, sub-cell positions, orders, harvester
    /// cycles, both random streams), so two simulations with equal hashes cannot diverge afterwards on the same
    /// commands. Power is derived from the objects and so is covered by them. Doubles are written in the invariant
    /// round-trip form.
    /// </summary>
    public string ComputeStateHash()
    {
        static string D(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        static string Id(RegionId? region) => region is { } r ? r.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-";
        StringBuilder sb = new();
        sb.Append(Time.Frame).Append('|').Append(MatchEnded).Append(':').Append(Winner?.Value).Append(':').Append(EndReason).Append('|')
          .Append(rng.State).Append(':').Append(combatRng.State).Append('|');
        foreach (SimPlayerState p in players.Values.OrderBy(p => p.Id.Value))
        {
            sb.Append(p.Id.Value).Append(':').Append(p.Credits).Append(':').Append(p.RejectedCommands).Append(':').Append(p.Defeated).Append(':')
              .Append(nextEntityIdByOwner.GetValueOrDefault(p.Id));
            foreach (QueueRuntime q in p.Queues.Values.OrderBy(static q => q.Kind))
            {
                sb.Append("/q").Append(q.Kind);
                foreach (QueueItemRuntime item in q.Items) sb.Append(',').Append(item.TypeId).Append('@').Append(D(item.Progress)).Append('$').Append(item.Paid).Append(item.AwaitingPlacement ? "!" : string.Empty);
            }
            foreach (PendingPlacement placement in p.PendingPlacements) sb.Append("/p").Append(placement.TypeId).Append('@').Append(placement.Queue);
            sb.Append(';');
        }
        sb.Append(nextProbeEntityId).Append('|');
        foreach (SimEntity e in entities.OrderBy(e => e.Id.Value))
        {
            sb.Append(e.Id.Value).Append(':').Append(e.Owner.Value).Append(':').Append(e.TypeId).Append(':')
              .Append(e.Health).Append('/').Append(e.MaxHealth).Append(':').Append(e.Position.X).Append(',').Append(e.Position.Y).Append(':')
              .Append(D(e.OffsetX)).Append(',').Append(D(e.OffsetY)).Append(':').Append(e.Region.Value).Append(':').Append(e.Deployed).Append(':')
              .Append(e.ExplicitTarget?.Value).Append(':').Append(string.Join(',', e.RemainingPath.Select(static r => r.Value))).Append(':')
              .Append(e.FinalDestination is { } dest ? $"{dest.X},{dest.Y}" : "-").Append(':').Append(e.HoldForCombat).Append(':')
              .Append(e.Phase).Append(':').Append(Id(e.AssignedOreRegion)).Append(':').Append(Id(e.TargetRefineryRegion)).Append(':')
              .Append(e.CarriedValue).Append(':').Append(D(e.PhaseSecondsRemaining)).Append(':').Append(e.RepairRequested).Append(':')
              .Append(D(e.SuperweaponCharge)).Append(';');
        }
        foreach ((RegionId region, double remaining) in oreRemaining.OrderBy(kv => kv.Key.Value))
        {
            sb.Append(region.Value).Append('=').Append(D(remaining)).Append(';');
        }
        foreach ((PlayerId player, GameCommand command) in pending)
        {
            sb.Append("cmd:").Append(player.Value).Append(':').Append(command).Append(';');
        }
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    // ----- hidden-state access for SimLeakageProbe only -----

    internal RegionId StartRegionOf(PlayerId player) => startRegionOwner.First(kv => kv.Value == player).Key;

    internal IReadOnlySet<RegionId> VisibleRegionsForProbe(PlayerId player) => VisibleRegionsFor(player);

    /// <summary>Whether a harvester is on its way to a refinery with a load; for focused economy tests.</summary>
    internal bool DebugIsReturningWithLoad(EntityId harvester) =>
        entities.Find(e => e.Id == harvester) is { Phase: HarvesterPhase.ToRefinery, CarriedValue: > 0 };

    internal void DebugAdjustCredits(PlayerId player, int delta) => players[player].Credits += delta;

    internal void DebugEnqueue(PlayerId player, QueueKind kind, string typeId)
    {
        SimPlayerState state = players[player];
        QueueRuntime queue = state.Queues.TryGetValue(kind, out QueueRuntime? existing) ? existing : state.Queues[kind] = new QueueRuntime { Kind = kind };
        // A debug item is prepaid: probes that enqueue directly test production, not the player's purse.
        queue.Items.Add(new QueueItemRuntime { TypeId = typeId, Paid = rules.TryGet(typeId, out UnitRule rule) ? Math.Max(0, rule.Cost) : 0 });
    }

    /// <summary>Spawns an entity without emitting a creation event, for probing fog in a region the caller has already checked is unseen.</summary>
    internal void DebugSpawnSilently(PlayerId owner, string typeId, RegionId region)
    {
        UnitRule rule = rules.Get(typeId);
        Cell position = RegionCenter(region);
        SimEntity entity = new()
        {
            Id = new EntityId(nextProbeEntityId++),
            Owner = owner,
            TypeId = typeId,
            Position = position,
            Region = region,
            Health = rule.Strength,
            MaxHealth = rule.Strength,
        };
        entity.SnapTo(position, CentreX, CentreY);
        entities.Add(entity);
    }

    /// <summary>
    /// Spawns an entity in a region and announces it through the normal event path (a completed build and a new
    /// object, as a factory there would), so each player receives the events only as its fog allows. The probe uses
    /// it to exercise event gating, which a silent spawn never reaches. Ids come from the probe range.
    /// </summary>
    internal EntityId DebugSpawnAnnounced(PlayerId owner, string typeId, RegionId region)
    {
        UnitRule rule = rules.Get(typeId);
        Cell position = RegionCenter(region);
        SimEntity entity = new()
        {
            Id = new EntityId(nextProbeEntityId++),
            Owner = owner,
            TypeId = typeId,
            Position = position,
            Region = region,
            Health = rule.Strength,
            MaxHealth = rule.Strength,
        };
        entity.SnapTo(position, CentreX, CentreY);
        entities.Add(entity);
        GameEvent[] announced =
        [
            new(GameEventKind.ProductionCompleted, Time, null, owner, typeId, position),
            new(GameEventKind.EntityCreated, Time, entity.Id, owner, typeId, position),
        ];
        frameEvents.AddRange(announced);
        foreach (SimPlayerState state in players.Values.OrderBy(static p => p.Id.Value))
        {
            HashSet<RegionId> visible = VisibleRegionsFor(state.Id);
            foreach (GameEvent gameEvent in announced)
            {
                if (EventForPlayer(gameEvent, state.Id, visible) is { } delivered) state.PendingEvents.Add(delivered);
            }
        }
        return entity.Id;
    }

    /// <summary>
    /// Wounds every enemy of <paramref name="observer"/> that stands in a region it cannot see (a tenth of full
    /// health, never below 1): hidden health is a fact the observer has no way to know.
    /// </summary>
    internal void DebugWoundHidden(PlayerId observer)
    {
        HashSet<RegionId> visible = VisibleRegionsFor(observer);
        foreach (SimEntity e in entities.Where(e => e.Alive && e.Owner != observer && !visible.Contains(e.Region)).OrderBy(static e => e.Id.Value))
        {
            e.Health = Math.Max(1, e.Health - Math.Max(1, e.MaxHealth / 10));
        }
    }

    /// <summary>
    /// Places a unit of <paramref name="owner"/> just inside the unseen region nearest to the observer's
    /// longest-range armed object, in a cell that object's weapon reaches when one does: the case where a leak
    /// needs combat (cross-border fire, a kill event) to show. The unit is the cheapest ground unit that weapon
    /// can hit. Returns the spawned id, or null when the observer has no armed object or every region is seen.
    /// </summary>
    internal EntityId? DebugSpawnAcrossBorder(PlayerId owner, PlayerId observer)
    {
        HashSet<RegionId> visible = VisibleRegionsFor(observer);
        List<Region> unseen = [.. map.Map.Regions.Where(r => !visible.Contains(r.Id)).OrderBy(static r => r.Id.Value)];
        SimEntity? gun = entities
            .Where(e => e.Alive && e.Owner == observer && rules.TryGet(e.TypeId, out UnitRule r) && r.Weapon is not (WeaponClass.None or WeaponClass.AntiAir) && r.Range > 0 && r.Damage > 0)
            .OrderByDescending(e => rules.Get(e.TypeId).Range).ThenBy(static e => e.Id.Value)
            .FirstOrDefault();
        if (gun is null || unseen.Count == 0) return null;
        UnitRule? victim = rules.All
            .Where(r => r.Kind is EntityKind.Infantry or EntityKind.Vehicle && r.Strength > 0 && rules.Effectiveness(gun.TypeId, r.TypeId) > 0)
            .OrderBy(static r => r.Cost).ThenBy(static r => r.TypeId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (victim is null) return null;

        Cell? best = null;
        double bestDistance = double.MaxValue;
        foreach (Region region in unseen)
        {
            // Walk from the region's centre toward the gun and keep the last cell still inside the region.
            Cell c = region.Center;
            double length = Math.Max(1, c.DistanceTo(gun.Position));
            Cell inside = c;
            for (int step = 1; step <= (int)length; step++)
            {
                Cell next = new(c.X + (int)Math.Round((gun.Position.X - c.X) * step / length), c.Y + (int)Math.Round((gun.Position.Y - c.Y) * step / length));
                if (map.Map.RegionOf(next)?.Id != region.Id) break;
                inside = next;
            }
            double distance = inside.DistanceTo(gun.Position);
            if (distance < bestDistance) { bestDistance = distance; best = inside; }
        }
        return best is { } cell ? DebugSpawnAt(owner, victim.TypeId, cell, probeId: true) : null;
    }

    /// <summary>Spawns an entity at an exact cell (its region is the cell's), without a creation event; for focused tests.</summary>
    internal EntityId DebugSpawnAt(PlayerId owner, string typeId, Cell cell, bool probeId = false)
    {
        UnitRule rule = rules.Get(typeId);
        Region region = map.Map.RegionOf(cell) ?? throw new ArgumentException("Cell is outside every region.", nameof(cell));
        SimEntity entity = new()
        {
            Id = probeId ? new EntityId(nextProbeEntityId++) : NextEntityId(owner),
            Owner = owner,
            TypeId = typeId,
            Position = cell,
            Region = region.Id,
            Health = rule.Strength,
            MaxHealth = rule.Strength,
        };
        entity.SnapTo(cell, CentreX, CentreY);
        entities.Add(entity);
        return entity.Id;
    }

    /// <summary>Sets an entity's health directly (a wound without a fight); for focused tests.</summary>
    internal void DebugSetHealth(EntityId id, int health)
    {
        SimEntity entity = entities.Single(e => e.Id == id && e.Alive);
        entity.Health = Math.Clamp(health, 1, entity.MaxHealth);
    }

    /// <summary>Health of a live entity, or null when there is none; for focused tests.</summary>
    internal int? DebugHealthOf(EntityId id) => entities.FirstOrDefault(e => e.Id == id && e.Alive)?.Health;

    // ----- setup helpers -----

    private EntityId NextEntityId(PlayerId owner)
    {
        uint next = nextEntityIdByOwner.GetValueOrDefault(owner) + 1;
        if (next >= EntityIdsPerPlayer) throw new InvalidOperationException($"Player {owner} ran out of entity ids.");
        nextEntityIdByOwner[owner] = next;
        return new EntityId(checked(((uint)owner.Value * EntityIdsPerPlayer) + next));
    }

    private Region RegionById(RegionId id) => map.Map.Regions.First(r => r.Id == id);

    private Cell RegionCenter(RegionId id) => RegionById(id).Center;

    private SimEntity SpawnEntity(PlayerId owner, string typeId, Cell position, RegionId region)
    {
        UnitRule rule = rules.Get(typeId);
        SimEntity entity = new()
        {
            Id = NextEntityId(owner),
            Owner = owner,
            TypeId = typeId,
            Position = position,
            Region = region,
            Health = rule.Strength,
            MaxHealth = rule.Strength,
        };
        entity.SnapTo(position, CentreX, CentreY);
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
            if (!ok)
            {
                state.RejectedCommands++;
                state.RecentRejections.Enqueue(command);
                if (state.RecentRejections.Count > 32) state.RecentRejections.Dequeue();
            }
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
        LaunchSuperweaponCommand c => ApplyLaunch(player, c),
        _ => false,
    };

    /// <summary>
    /// Fires a ready superweapon: every object within <see cref="SimSettings.SuperweaponRadiusCells"/> of the
    /// target (either side's) takes <see cref="SimSettings.SuperweaponDamage"/>, and every player is told of the
    /// launch, as RA2 announces it to all.
    /// </summary>
    private bool ApplyLaunch(PlayerId player, LaunchSuperweaponCommand c)
    {
        if (!OwnsAlive(player, c.Building, out SimEntity building)) return false;
        if (!rules.TryGet(building.TypeId, out UnitRule rule) || rule.Role != UnitRole.Superweapon) return false;
        if (building.SuperweaponCharge < settings.SuperweaponChargeSeconds) return false;
        if (c.Target.X < 0 || c.Target.Y < 0 || c.Target.X >= map.Map.Width || c.Target.Y >= map.Map.Height) return false;
        building.SuperweaponCharge = 0;
        frameEvents.Add(new GameEvent(GameEventKind.SuperweaponLaunched, Time, building.Id, player, building.TypeId, c.Target));
        foreach (SimEntity e in entities.Where(e => e.Alive && e.Position.DistanceTo(c.Target) <= settings.SuperweaponRadiusCells).OrderBy(static e => e.Id.Value).ToList())
        {
            e.Health = Math.Max(0, e.Health - settings.SuperweaponDamage);
            frameEvents.Add(new GameEvent(GameEventKind.UnderAttack, Time, e.Id, e.Owner, e.TypeId, e.Position));
            if (e.Health <= 0) Destroy(e, "superweapon", player);
        }
        return true;
    }

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
        // RA2 takes the money as the item builds, not on order; the sim still refuses an order the player could not
        // pay for right now, which keeps a broke player from stacking a queue it cannot fund.
        if (state.Credits < rule.Cost) return false;
        QueueRuntime queue = state.Queues.TryGetValue(c.Queue, out QueueRuntime? existing) ? existing : state.Queues[c.Queue] = new QueueRuntime { Kind = c.Queue };
        queue.Items.Add(new QueueItemRuntime { TypeId = c.TypeId });
        return true;
    }

    private bool ApplyCancel(SimPlayerState state, CancelProductionCommand c)
    {
        if (!state.Queues.TryGetValue(c.Queue, out QueueRuntime? queue)) return false;
        int index = queue.Items.FindLastIndex(i => i.TypeId == c.TypeId);
        if (index < 0) return false;
        // Refunds what the item has cost so far (RA2 debits production as it builds).
        state.Credits += queue.Items[index].Paid;
        // A finished building also waits in PendingPlacements; refunding it must take that placement away too, or
        // the refunded building could still be placed for free.
        if (queue.Items[index].AwaitingPlacement)
        {
            int placement = state.PendingPlacements.FindIndex(p => p.TypeId == c.TypeId && p.Queue == c.Queue);
            if (placement >= 0) state.PendingPlacements.RemoveAt(placement);
        }
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
        bool overlaps = entities.Any(e => e.Alive && rules.TryGet(e.TypeId, out UnitRule r) && r.Kind == EntityKind.Building
                                          && e.Position.DistanceTo(c.Cell) < PlacementClearanceCells);
        if (overlaps) return false;
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
        if (settings.FreeHarvesterWithRefinery && rules.TryGet(c.TypeId, out UnitRule placed) && placed.Kind == EntityKind.Building && placed.Role == UnitRole.Economy)
        {
            SpawnFreeHarvester(player, state.Faction, c.Cell, region.Id);
        }
        return true;
    }

    /// <summary>
    /// RA2's refinery <c>FreeUnit=</c>: the owner's cheapest harvester, next to the refinery, at no cost and without
    /// its usual prerequisites. It announces itself like any new unit and starts harvesting on its own.
    /// </summary>
    private void SpawnFreeHarvester(PlayerId player, Faction faction, Cell refinery, RegionId region)
    {
        UnitRule? harvester = rules.All
            .Where(r => r.Role == UnitRole.Harvester && r.Kind != EntityKind.Building && r.Factions.Contains(faction))
            .OrderBy(static r => r.Cost).ThenBy(static r => r.TypeId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (harvester is null) return;
        Cell beside = Beside(refinery, 1, 1);
        Cell cell = map.Map.RegionOf(beside)?.Id == region && beside.X >= 0 && beside.Y >= 0 && beside.X < map.Map.Width && beside.Y < map.Map.Height ? beside : refinery;
        SpawnEntity(player, harvester.TypeId, cell, region);
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

    /// <summary>
    /// An attack order names an enemy the issuer can see now (RA2 cannot target an object in fog); otherwise it is
    /// rejected. Accepting it would walk the units to the target's true, hidden region, so a belief-mode bot could
    /// follow enemies through fog, or find objects it never saw by guessing their ids.
    /// </summary>
    private bool ApplyAttack(PlayerId player, AttackCommand c)
    {
        SimEntity? target = entities.Find(e => e.Id == c.Target && e.Alive);
        if (target is null) return false;
        if (target.Owner != player && !IsVisibleTo(target, VisibleRegionsFor(player))) return false;
        bool any = false;
        foreach (EntityId id in c.Units)
        {
            if (!OwnsAlive(player, id, out SimEntity entity)) continue;
            entity.ExplicitTarget = target.Id;
            // A target already within weapon range across a region border is fired on from where the unit
            // stands (RA2 units do not close in on what they can already hit); otherwise the unit walks over.
            bool inRange = rules.TryGet(entity.TypeId, out UnitRule attackerRule) && attackerRule.Range > 0
                && entity.Position.DistanceTo(target.Position) <= attackerRule.Range;
            if (inRange && entity.Region != target.Region)
            {
                entity.RemainingPath.Clear();
                entity.FinalDestination = null;
                entity.HoldForCombat = false;
            }
            else if (entity.Region != target.Region)
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

    /// <summary>
    /// A repair order sends the unit to an own service depot (the one named, or the nearest), where
    /// <see cref="AdvanceRepair"/> heals it; without a depot there is no repair (RA2).
    /// </summary>
    private bool ApplyRepair(PlayerId player, RepairCommand c)
    {
        if (!OwnsAlive(player, c.Unit, out SimEntity entity)) return false;
        SimEntity? depot = c.Depot is { } named
            ? (OwnsAlive(player, named, out SimEntity d) && IsDepot(d) ? d : null)
            : entities.Where(e => e.Owner == player && e.Alive && IsDepot(e))
                      .OrderBy(e => e.Position.DistanceTo(entity.Position)).ThenBy(static e => e.Id.Value).FirstOrDefault();
        if (depot is null) return false;
        if (entity.Position.DistanceTo(depot.Position) > RepairRangeCells
            && !ApplyMove(player, c.Controller, [entity.Id], depot.Position, attackMove: false))
        {
            return false;
        }
        entity.RepairRequested = true;
        return true;
    }

    private bool IsDepot(SimEntity e) => rules.TryGet(e.TypeId, out UnitRule rule) && rule.Kind == EntityKind.Building && rule.Repairs;

    private bool NearOwnDepot(SimEntity unit) =>
        entities.Any(e => e.Owner == unit.Owner && e.Alive && IsDepot(e) && e.Position.DistanceTo(unit.Position) <= RepairRangeCells);

    private bool ApplyHarvest(PlayerId player, HarvestCommand c)
    {
        if (!OwnsAlive(player, c.Harvester, out SimEntity entity)) return false;
        Region? region = map.Map.RegionOf(c.Ore);
        if (region is null || !region.HasOre || oreRemaining.GetValueOrDefault(region.Id) <= 0) return false;
        // Re-ordering a harvester to the field it already works is a no-op, as in RA2: its cycle continues.
        if (entity.AssignedOreRegion == region.Id && entity.Phase != HarvesterPhase.Idle) return true;
        entity.AssignedOreRegion = region.Id;
        entity.Phase = HarvesterPhase.Idle;
        entity.RemainingPath.Clear();
        return true;
    }

    // ----- movement -----

    private double CentreX => map.Map.Width / 2.0;

    private double CentreY => map.Map.Height / 2.0;

    // Rounds the offset, not the absolute coordinate, so mirrored offsets land in mirrored cells (banker's rounding
    // is symmetric under negation); with an odd dimension the centre is a half-cell and no rounding can be.
    private static int ToCell(double offset, double centre) =>
        centre == Math.Floor(centre) ? (int)(Math.Round(offset) + centre) : (int)Math.Round(offset + centre);

    private void AdvanceMovement()
    {
        foreach (SimEntity e in entities)
        {
            if (!e.Alive || e.RemainingPath.Count == 0) continue;
            if (!rules.TryGet(e.TypeId, out UnitRule rule) || rule.Speed <= 0) { e.RemainingPath.Clear(); continue; }
            if (e.HoldForCombat && HasHostilesInRegion(e)) continue;

            double stepCells = rule.Speed / GameTime.FramesPerSecond;
            Cell target = e.RemainingPath.Count == 1 && e.FinalDestination is { } dest ? dest : RegionCenter(e.RemainingPath[0]);
            double dx = (target.X - CentreX) - e.OffsetX, dy = (target.Y - CentreY) - e.OffsetY;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance <= stepCells || distance == 0)
            {
                e.SnapTo(target, CentreX, CentreY);
                e.Region = e.RemainingPath[0];
                e.RemainingPath.RemoveAt(0);
                if (e.RemainingPath.Count == 0) e.HoldForCombat = false;
            }
            else
            {
                double t = stepCells / distance;
                e.OffsetX += dx * t;
                e.OffsetY += dy * t;
                e.Position = new Cell(ToCell(e.OffsetX, CentreX), ToCell(e.OffsetY, CentreY));
                // A unit is in the region it stands in from the moment it crosses the border, not when it reaches
                // the next region's centre: fog, combat and sight all work by region, and a lagging region let a
                // unit that had walked into fog still be seen (and shot at) where it no longer was.
                if (map.Map.RegionOf(e.Position) is { } now) e.Region = now.Id;
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
            StepHarvester(e, state);
        }
    }

    /// <summary>
    /// One second of a harvester's cycle. Trips to the ore and back are driven by the normal movement system, so
    /// the harvester is present in every region on its route, where it can be seen and shot. A load is paid out
    /// only at a refinery that still stands; with none, the harvester keeps its load and waits, and it does not
    /// start a new load while its owner has no refinery at all. A harvester under a move order finishes the move
    /// before resuming its cycle.
    /// </summary>
    private void StepHarvester(SimEntity e, SimPlayerState state)
    {
        switch (e.Phase)
        {
            case HarvesterPhase.Idle:
                if (e.RemainingPath.Count > 0) return;
                if (e.CarriedValue > 0)
                {
                    StartToRefinery(e);
                    return;
                }
                if (NearestRefineryRegion(e.Owner, e.Region) is null) return;
                RegionId? oreRegion = e.AssignedOreRegion is { } assigned && oreRemaining.GetValueOrDefault(assigned) > 0
                    ? assigned
                    : NearestOreRegion(e.Region);
                if (oreRegion is null || !RouteTo(e, oreRegion.Value)) return;
                e.AssignedOreRegion = oreRegion;
                e.Phase = HarvesterPhase.ToOre;
                break;

            case HarvesterPhase.ToOre:
                if (e.RemainingPath.Count > 0) return;
                if (e.AssignedOreRegion is not { } target || e.Region != target) { e.Phase = HarvesterPhase.Idle; return; }
                e.Phase = HarvesterPhase.Harvesting;
                e.PhaseSecondsRemaining = HarvesterHarvestSeconds;
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
                    if (load > 0) StartToRefinery(e);
                }
                break;

            case HarvesterPhase.ToRefinery:
                if (e.RemainingPath.Count > 0) return;
                if (e.TargetRefineryRegion is not { } refinery || e.Region != refinery || !HasRefineryIn(e.Owner, refinery))
                {
                    // Stopped, or the refinery it was heading for is gone: look again next second.
                    e.Phase = HarvesterPhase.Idle;
                    return;
                }
                e.Phase = HarvesterPhase.Unloading;
                e.PhaseSecondsRemaining = HarvesterUnloadSeconds;
                break;

            case HarvesterPhase.Unloading:
                e.PhaseSecondsRemaining -= 1;
                if (e.PhaseSecondsRemaining <= 0)
                {
                    e.Phase = HarvesterPhase.Idle;
                    if (e.TargetRefineryRegion is not { } at || !HasRefineryIn(e.Owner, at)) return; // keeps the load
                    state.Credits += (int)Math.Round(e.CarriedValue * state.IncomeMultiplier);
                    e.CarriedValue = 0;
                }
                break;
        }
    }

    /// <summary>Sends a loaded harvester to its owner's nearest refinery; with none, it stays idle and keeps the load.</summary>
    private void StartToRefinery(SimEntity e)
    {
        RegionId? refineryRegion = NearestRefineryRegion(e.Owner, e.Region);
        if (refineryRegion is null || !RouteTo(e, refineryRegion.Value)) return;
        e.TargetRefineryRegion = refineryRegion;
        e.Phase = HarvesterPhase.ToRefinery;
    }

    /// <summary>Paths a unit to a region's centre through the regions between; false when it cannot get there.</summary>
    private bool RouteTo(SimEntity e, RegionId destination)
    {
        IReadOnlyList<RegionId> path = Graph.Path(e.Region, destination);
        if (path.Count == 0) return false;
        e.RemainingPath.Clear();
        for (int i = 1; i < path.Count; i++) e.RemainingPath.Add(path[i]);
        e.FinalDestination = RegionCenter(destination);
        e.HoldForCombat = false;
        if (e.RemainingPath.Count == 0) e.RemainingPath.Add(destination); // walk to the centre of the region it is in
        return true;
    }

    private bool HasRefineryIn(PlayerId owner, RegionId region) =>
        entities.Any(e => e.Owner == owner && e.Alive && e.Region == region && rules.TryGet(e.TypeId, out UnitRule r) && r.Role == UnitRole.Economy);

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
            foreach (QueueRuntime queue in state.Queues.Values.OrderBy(q => q.Kind))
            {
                // A queue runs on the factories of its own kind only, and pauses while it has none (RA2).
                int factories = CountFactories(state, queue.Kind);
                double multiplier = factories <= 0 ? 0 : Math.Sqrt(factories) * (power.LowPower ? 0.5 : 1.0);
                if (queue.Items.Count == 0 || multiplier <= 0) continue;
                QueueItemRuntime active = queue.Items[0];
                if (active.AwaitingPlacement) continue;
                if (!rules.TryGet(active.TypeId, out UnitRule rule) || rule.BuildSeconds <= 0) { queue.Items.RemoveAt(0); continue; }
                double next = Math.Min(1.0, active.Progress + multiplier / rule.BuildSeconds);
                int cost = Math.Max(0, rule.Cost);
                int owed = Math.Max(0, (int)Math.Round(cost * next) - active.Paid);
                if (owed > state.Credits)
                {
                    // Out of money: RA2 builds only as far as the credits on hand pay for, then waits.
                    owed = Math.Max(0, state.Credits);
                    next = Math.Max(active.Progress, Math.Min(next, (active.Paid + owed) / (double)cost));
                }
                state.Credits -= owed;
                active.Paid += owed;
                active.Progress = next;
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
                        Cell spawnAt = RallyPointFor(state.Id, queue.Kind);
                        Region region = map.Map.RegionOf(spawnAt) ?? RegionById(map.StartRegions[0]);
                        SpawnEntity(state.Id, rule.TypeId, spawnAt, region.Id);
                    }
                }
            }
        }
    }

    /// <summary>Charges every powered superweapon by one second (RA2 pauses the countdown on low power).</summary>
    private void AdvanceSuperweapons()
    {
        foreach (SimEntity e in entities.OrderBy(static e => e.Id.Value))
        {
            if (!e.Alive || !rules.TryGet(e.TypeId, out UnitRule rule) || rule.Role != UnitRole.Superweapon) continue;
            if (ComputePower(players[e.Owner]).LowPower) continue;
            e.SuperweaponCharge = Math.Min(settings.SuperweaponChargeSeconds, e.SuperweaponCharge + 1);
        }
    }

    /// <summary>
    /// Every superweapon timer, as RA2 shows them to all players: the owner also learns which building it is;
    /// another player sees only owner, type and countdown (oracle frames carry the building too).
    /// </summary>
    private List<SuperweaponStatus> SuperweaponTimers(PlayerId player, ObservationMode mode)
    {
        List<SuperweaponStatus> timers = [];
        foreach (SimEntity e in entities.OrderBy(static e => e.Id.Value))
        {
            if (!e.Alive || !rules.TryGet(e.TypeId, out UnitRule rule) || rule.Role != UnitRole.Superweapon) continue;
            double remaining = Math.Max(0, settings.SuperweaponChargeSeconds - e.SuperweaponCharge);
            EntityId? building = e.Owner == player || mode == ObservationMode.Oracle ? e.Id : null;
            timers.Add(new SuperweaponStatus(e.Owner, e.TypeId, building, settings.SuperweaponChargeSeconds, remaining, remaining <= 0));
        }
        return timers;
    }

    /// <summary>
    /// A cell offset from <paramref name="origin"/> by the given distances on the side facing the map centre. A fixed
    /// +x, +y offset put a west base's new units and harvesters on its front side and an east base's behind it,
    /// which no mirror image of a map can cancel; facing the centre, both starts of a mirrored map get the same.
    /// </summary>
    private Cell Beside(Cell origin, int dx, int dy)
    {
        int sx = origin.X * 2 <= map.Map.Width ? 1 : -1;
        int sy = origin.Y * 2 <= map.Map.Height ? 1 : -1;
        return new Cell(origin.X + (sx * dx), origin.Y + (sy * dy));
    }

    /// <summary>Where a finished unit appears: beside the newest factory of its queue (any building if it has none).</summary>
    private Cell RallyPointFor(PlayerId owner, QueueKind queue)
    {
        SimEntity? factory = entities.Where(e => e.Owner == owner && e.Alive && IsFactoryFor(e.TypeId, queue)).OrderBy(e => e.Id.Value).LastOrDefault()
            ?? entities.Where(e => e.Owner == owner && e.Alive && rules.TryGet(e.TypeId, out UnitRule r) && r.Kind == EntityKind.Building)
                       .OrderBy(e => e.Id.Value).LastOrDefault();
        if (factory is not null) return Beside(factory.Position, 1 + rng.NextInt(3), 1 + rng.NextInt(3));
        int index = players.Keys.OrderBy(p => p.Value).ToList().IndexOf(owner);
        return RegionCenter(map.StartRegions[Math.Max(0, index)]);
    }

    private int CountFactories(SimPlayerState state, QueueKind queue) =>
        entities.Count(e => e.Owner == state.Id && e.Alive && IsFactoryFor(e.TypeId, queue));

    /// <summary>
    /// Whether a type is a factory for a queue. Where the rules say (<see cref="UnitRule.Produces"/>), exactly the
    /// buildings that declare the queue. Otherwise it is derived: a production building listed among the
    /// prerequisites of that queue's items (barracks for infantry, war factory for vehicles), and for the building
    /// and defense queues the production buildings that serve no unit queue (the construction yard); a queue with
    /// neither falls back to every production building.
    /// </summary>
    private bool IsFactoryFor(string typeId, QueueKind queue)
    {
        if (!rules.TryGet(typeId, out UnitRule r) || r.Kind != EntityKind.Building) return false;
        if (declaredFactoryQueues.Contains(queue)) return r.Produces is { } produces && produces.Contains(queue);
        return r.Role == UnitRole.Production
            && (!factoryTypes.TryGetValue(queue, out HashSet<string>? types) || types.Count == 0 || types.Contains(typeId));
    }

    private static Dictionary<QueueKind, HashSet<string>> FactoryTypesByQueue(IRulesDatabase rules)
    {
        HashSet<string> production = rules.All.Where(static r => r.Role == UnitRole.Production && r.Kind == EntityKind.Building)
                                              .Select(static r => r.TypeId).ToHashSet(StringComparer.Ordinal);
        Dictionary<QueueKind, HashSet<string>> byQueue = [];
        foreach (QueueKind queue in Enum.GetValues<QueueKind>())
        {
            if (queue is QueueKind.Building or QueueKind.Defense) continue;
            byQueue[queue] = rules.All.Where(r => r.Queue == queue)
                                      .SelectMany(static r => r.Prerequisites.SelectMany(static g => g))
                                      .Where(production.Contains).ToHashSet(StringComparer.Ordinal);
        }
        HashSet<string> yards = production.Where(t => !byQueue.Values.Any(s => s.Contains(t))).ToHashSet(StringComparer.Ordinal);
        byQueue[QueueKind.Building] = yards;
        byQueue[QueueKind.Defense] = yards;
        return byQueue;
    }

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

    /// <summary>
    /// Resolves one second of fire. Every armed unit fires at one target: an enemy in its own region when there is
    /// one it can hit (the region is the engagement zone, whatever the distance inside it), otherwise an enemy in
    /// another region within its weapon range in cells. The second rule is what lets long-range artillery bombard
    /// across a border, and a defense reach only as far as its range. Damage is applied per region of the target,
    /// in region order, then target id order. Cross-border fire needs the target's region to be visible to the
    /// attacker's owner, as RA2 cannot acquire a target in fog: otherwise a long-range unit would kill enemies its
    /// owner never saw, and the kill event would tell the owner their type and cell. A building that drains power
    /// does not fire while its owner is on low power.
    /// </summary>
    private void ResolveCombat()
    {
        Dictionary<PlayerId, HashSet<RegionId>> visibleTo = [];
        HashSet<RegionId> VisibleTo(PlayerId player) =>
            visibleTo.TryGetValue(player, out HashSet<RegionId>? set) ? set : visibleTo[player] = VisibleRegionsFor(player);

        // Low power takes powered base defenses offline, as in RA2 (the usual way to break a turtle).
        Dictionary<PlayerId, bool> lowPower = players.Values.ToDictionary(static p => p.Id, p => ComputePower(p).LowPower);

        Dictionary<RegionId, List<SimEntity>> byRegion = [];
        foreach (SimEntity e in entities)
        {
            if (!e.Alive) continue;
            (byRegion.TryGetValue(e.Region, out List<SimEntity>? list) ? list : byRegion[e.Region] = []).Add(e);
        }

        Dictionary<RegionId, Dictionary<EntityId, double>> damageByRegion = [];
        Dictionary<EntityId, (EntityId Attacker, double Amount)> topAttacker = [];
        List<SimEntity> alive = [.. entities.Where(static e => e.Alive).OrderBy(static e => e.Id.Value)];
        foreach ((RegionId region, List<SimEntity> present) in byRegion.OrderBy(kv => kv.Key.Value))
        {
            foreach (SimEntity attacker in present.OrderBy(e => e.Id.Value))
            {
                if (!rules.TryGet(attacker.TypeId, out UnitRule rule) || rule.Weapon == WeaponClass.None || rule.Range <= 0 || rule.Damage <= 0) continue;
                if (rule.Kind == EntityKind.Building && rule.Power < 0 && lowPower.GetValueOrDefault(attacker.Owner)) continue;
                SimEntity? target = ChooseTarget(attacker, rule, present) ?? ChooseTargetInRange(attacker, rule, alive, VisibleTo(attacker.Owner));
                if (target is null) continue;
                double amount = rule.Damage * rules.Effectiveness(attacker.TypeId, target.TypeId);
                if (amount <= 0) continue;
                if (settings.CombatNoise > 0) amount *= 1 + settings.CombatNoise * (2 * combatRng.NextDouble() - 1);
                Dictionary<EntityId, double> totalDamage = damageByRegion.TryGetValue(target.Region, out Dictionary<EntityId, double>? d) ? d : damageByRegion[target.Region] = [];
                totalDamage[target.Id] = totalDamage.GetValueOrDefault(target.Id) + amount;
                if (!topAttacker.TryGetValue(target.Id, out (EntityId Attacker, double Amount) best) || amount > best.Amount)
                {
                    topAttacker[target.Id] = (attacker.Id, amount);
                }
            }
        }

        foreach ((RegionId region, Dictionary<EntityId, double> totalDamage) in damageByRegion.OrderBy(kv => kv.Key.Value))
        {
            foreach ((EntityId targetId, double damage) in totalDamage.OrderBy(kv => kv.Key.Value))
            {
                SimEntity target = byRegion[region].First(e => e.Id == targetId);
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

    /// <summary>
    /// Clears an explicit target that has died or left its owner's sight: the order ends there, as in RA2, and the
    /// unit keeps whatever path it had toward where the target was last seen instead of tracking it through fog.
    /// </summary>
    private void DropTargetsInFog()
    {
        Dictionary<PlayerId, HashSet<RegionId>> visibleTo = [];
        foreach (SimEntity e in entities)
        {
            if (!e.Alive || e.ExplicitTarget is not { } targetId) continue;
            SimEntity? target = entities.Find(t => t.Id == targetId);
            if (target is null || !target.Alive) { e.ExplicitTarget = null; continue; }
            if (target.Owner == e.Owner) continue;
            HashSet<RegionId> visible = visibleTo.TryGetValue(e.Owner, out HashSet<RegionId>? set) ? set : visibleTo[e.Owner] = VisibleRegionsFor(e.Owner);
            if (!IsVisibleTo(target, visible)) e.ExplicitTarget = null;
        }
    }

    /// <summary>Whether an object stands in a region the given visible set covers, judged by its cell, as Observe does.</summary>
    private bool IsVisibleTo(SimEntity e, HashSet<RegionId> visible) => map.Map.RegionOf(e.Position) is { } at && visible.Contains(at.Id);

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

    /// <summary>
    /// An enemy outside the attacker's region, in a region its owner can see, and within its range in cells: the
    /// explicit target first, else the weakest.
    /// </summary>
    private SimEntity? ChooseTargetInRange(SimEntity attacker, UnitRule attackerRule, List<SimEntity> alive, HashSet<RegionId> ownerVisible)
    {
        bool InRange(SimEntity e) => e.Alive && e.Owner != attacker.Owner && e.Region != attacker.Region && ownerVisible.Contains(e.Region)
            && attacker.Position.DistanceTo(e.Position) <= attackerRule.Range && CanTarget(attackerRule, e);
        if (attacker.ExplicitTarget is { } explicitId && alive.Find(e => e.Id == explicitId) is { } explicitTarget && InRange(explicitTarget))
        {
            return explicitTarget;
        }
        return alive.Where(InRange).OrderBy(static e => e.Health).ThenBy(static e => e.Id.Value).FirstOrDefault();
    }

    /// <summary>
    /// What can hit what: an anti-air weapon connects only with aircraft; an aircraft can be hit only by an
    /// attacker with the dual-purpose <see cref="UnitRule.AntiAir"/> flag or a general weapon (the flag adds air
    /// targets to a ground weapon rather than replacing them); and the rules must give a positive damage multiplier.
    /// </summary>
    private bool CanTarget(UnitRule attackerRule, SimEntity target)
    {
        if (!rules.TryGet(target.TypeId, out UnitRule targetRule)) return false;
        if (attackerRule.Weapon == WeaponClass.AntiAir) return targetRule.Kind == EntityKind.Aircraft;
        if (targetRule.Kind == EntityKind.Aircraft && !attackerRule.AntiAir && attackerRule.Weapon != WeaponClass.General) return false;
        return rules.Effectiveness(attackerRule.TypeId, target.TypeId) > 0;
    }

    private void AdvanceRepair()
    {
        foreach (SimEntity e in entities)
        {
            if (!e.Alive || !e.RepairRequested) continue;
            if (!rules.TryGet(e.TypeId, out UnitRule rule)) continue;
            if (e.Health >= e.MaxHealth) { e.RepairRequested = false; continue; }
            if (!NearOwnDepot(e)) continue; // still driving there, or the depot is gone
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

    /// <summary>
    /// The event as this player may receive it, or null when it may not. A kill is the killer's event, but it
    /// describes the victim, so it is delivered only when the victim's cell is visible (a superweapon can kill in
    /// fog). A superweapon launch is announced to every player, but only its owner learns which building fired.
    /// </summary>
    private GameEvent? EventForPlayer(GameEvent gameEvent, PlayerId player, HashSet<RegionId> visible)
    {
        bool PositionVisible() => gameEvent.Position is { } position && map.Map.RegionOf(position) is { } region && visible.Contains(region.Id);
        return gameEvent.Kind switch
        {
            GameEventKind.SuperweaponLaunched => gameEvent.Owner == player ? gameEvent : gameEvent with { Entity = null },
            GameEventKind.EntityKilledByUs => gameEvent.Owner == player && PositionVisible() ? gameEvent : null,
            _ => gameEvent.Owner == player || PositionVisible() ? gameEvent : null,
        };
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
            // Timeout: the highest asset value wins; an exact tie at the top is a draw, not a win for the lower id.
            MatchEnded = true;
            List<(PlayerId Id, int Assets)> ranked = alive.Select(p => (p.Id, AssetValue(p.Id))).OrderByDescending(static p => p.Item2).ToList();
            Winner = ranked.Count > 1 && ranked[0].Assets == ranked[1].Assets ? null : ranked[0].Id;
            EndReason = "timeout";
        }
    }
}
