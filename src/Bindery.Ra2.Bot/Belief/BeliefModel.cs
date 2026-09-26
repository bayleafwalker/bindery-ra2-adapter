// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Belief;

/// <summary>
/// Folds a sequence of <see cref="ObservationFrame"/>s into a
/// <see cref="BeliefSnapshot"/>: the player's own state exactly, and enemy
/// state as decaying memory. This is the only place fog-of-war memory is
/// implemented; every downstream layer only ever reads a snapshot.
///
/// In <see cref="ObservationMode.Oracle"/> mode no special path is taken:
/// oracle frames simply contain every entity and every region as visible, so
/// the same folding logic degenerates to "everything is always freshly seen".
/// </summary>
public sealed class BeliefModel : IBeliefModel
{
    private readonly IRulesDatabase rules;
    private readonly BeliefOptions options;

    private readonly Dictionary<EntityId, EnemyContact> contacts = [];
    private readonly Dictionary<PlayerId, HashSet<Faction>> factionCandidates = [];
    private readonly Dictionary<PlayerId, HashSet<string>> seenTech = [];
    private readonly Dictionary<PlayerId, Dictionary<string, GameTime>> techLastSeen = [];
    /// <summary>Per enemy player, each start region an enemy building was seen in, with when it was first seen there.</summary>
    private readonly Dictionary<PlayerId, Dictionary<RegionId, GameTime>> enemyBuildingStartRegions = [];
    private readonly HashSet<RegionId> ownStartRegions = [];
    private readonly HashSet<RegionId> emptyScoutedStarts = [];
    private readonly Dictionary<RegionId, GameTime> regionLastSeen = [];
    private readonly List<GameEvent> recentEvents = [];

    private long version;
    private GameTime? previousFrameTime;
    private BeliefSnapshot? current;
    private Dictionary<RegionId, int>? oreLastSeen;
    private int? lastCredits;
    private PowerState? lastPower;

    public BeliefModel(IRulesDatabase rules, BeliefOptions options)
    {
        this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>The latest folded snapshot. Throws until <see cref="Apply"/> has been called once.</summary>
    public BeliefSnapshot Current => current ?? throw new InvalidOperationException(
        "BeliefModel.Current was read before any frame was applied.");

    public BeliefSnapshot Apply(ObservationFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        UpdateRegionLastSeen(frame);
        if (frame.OreRemaining is { } ore)
        {
            oreLastSeen ??= [];
            foreach ((RegionId region, int remaining) in ore) oreLastSeen[region] = remaining;
        }
        List<OwnEntity> own = BuildOwnEntities(frame);
        UpdateEnemyContacts(frame);
        ApplyDestroyedEvents(frame);
        UpdateScoutingKnowledge(frame, own);
        UpdateRecentEvents(frame);

        // A source that has not sampled credits or power yet sends placeholders; the belief keeps the last value it
        // was told instead, and says whether it has ever been told.
        if (frame.CreditsKnown) lastCredits = frame.Credits;
        if (frame.PowerKnown) lastPower = frame.Power;
        List<EnemyContact> enemies = [.. contacts.Values.Where(c => frame.IsEnemy(c.Owner)).OrderBy(static c => c.Id.Value)];
        List<EnemyPlayerBelief> enemyPlayers = BuildEnemyPlayerBeliefs(frame, enemies);

        version++;
        BeliefSnapshot snapshot = new(
            version,
            frame.Time,
            frame.Mode,
            frame.Self,
            frame.Faction,
            lastCredits ?? frame.Credits,
            lastPower ?? frame.Power,
            own,
            enemies,
            enemyPlayers,
            frame.Queues,
            new Dictionary<RegionId, GameTime>(regionLastSeen),
            [.. recentEvents],
            frame.Map,
            oreLastSeen is null ? null : new Dictionary<RegionId, int>(oreLastSeen),
            frame.Superweapons,
            frame.QueuesKnown,
            lastCredits is not null,
            lastPower is not null);

        current = snapshot;
        return snapshot;
    }

    private void UpdateRegionLastSeen(ObservationFrame frame)
    {
        foreach (RegionId region in frame.VisibleRegions) regionLastSeen[region] = frame.Time;
    }

    private List<OwnEntity> BuildOwnEntities(ObservationFrame frame)
    {
        List<OwnEntity> result = [];
        foreach (ObservedEntity e in frame.Entities)
        {
            if (e.Owner != frame.Self) continue;
            (UnitRole role, EntityKind kind, int value) = ResolveTypeFacts(e.TypeId);
            RegionId region = RegionOf(frame.Map, e.Position);
            result.Add(new OwnEntity(e.Id, e.TypeId, role, kind, e.Position, region, e.HealthFraction, value, e.Deployed));
        }
        result.Sort(static (a, b) => a.Id.Value.CompareTo(b.Id.Value));
        return result;
    }

    /// <summary>
    /// Looks up role/kind/value from rules. Unknown types (not in the loaded
    /// ruleset) are still counted, per spec, as <see cref="UnitRole.Support"/>
    /// with zero value. Their <see cref="EntityKind"/> cannot be known from an
    /// <see cref="ObservedEntity"/> alone (it carries no kind field), so the
    /// convention here is <see cref="EntityKind.Vehicle"/> — the most common
    /// mobile army member — documented so downstream counts are explainable.
    /// </summary>
    private (UnitRole Role, EntityKind Kind, int Value) ResolveTypeFacts(string typeId) =>
        rules.TryGet(typeId, out UnitRule rule) ? (rule.Role, rule.Kind, rule.Cost) : (UnitRole.Support, EntityKind.Vehicle, 0);

    private static RegionId RegionOf(MapInfo map, Cell position) =>
        (map.RegionOf(position) ?? throw new InvalidOperationException("Map has no regions to place an entity in."))
        .Id;

    private void UpdateEnemyContacts(ObservationFrame frame)
    {
        HashSet<EntityId> seenThisFrame = [];
        foreach (ObservedEntity e in frame.Entities)
        {
            if (!frame.IsEnemy(e.Owner)) continue;
            seenThisFrame.Add(e.Id);
            (UnitRole role, EntityKind kind, int value) = ResolveTypeFacts(e.TypeId);
            RegionId region = RegionOf(frame.Map, e.Position);
            contacts[e.Id] = new EnemyContact(
                e.Id, e.Owner, e.TypeId, role, kind, e.Position, region, frame.Time, e.HealthFraction, value,
                Confidence: 1.0, ConfirmedDestroyed: false);

            if (rules.TryGet(e.TypeId, out UnitRule seenRule)) UpdateFactionKnowledge(e.Owner, seenRule.Factions);
        }

        List<EntityId> forgotten = [];
        foreach ((EntityId id, EnemyContact contact) in contacts)
        {
            if (seenThisFrame.Contains(id)) continue;
            if (contact.ConfirmedDestroyed) continue; // a confirmed fact does not decay.

            // Decay only over the interval since the previous frame, from the confidence it already had: the
            // half-life is chosen per interval by what that interval showed, so vacancy evidence gathered while
            // the spot was in view stays spent when it goes dark again, and confidence never rises unseen.
            GameTime since = previousFrameTime is { } p && p > contact.LastSeenAt ? p : contact.LastSeenAt;
            double elapsed = Math.Max(0, frame.Time.SecondsSince(since));
            bool regionVisible = frame.VisibleRegions.Contains(contact.LastSeenRegion);
            double confidence;
            if (regionVisible)
                confidence = options.VacancyHalfLifeSeconds <= 0 ? 0.0 : contact.Confidence * Math.Pow(0.5, elapsed / options.VacancyHalfLifeSeconds);
            else if (contact.Kind == EntityKind.Building)
                confidence = contact.Confidence; // a building cannot move: out of sight it stands where it was seen.
            else
                confidence = options.ConfidenceHalfLifeSeconds <= 0 ? 0.0 : contact.Confidence * Math.Pow(0.5, elapsed / options.ConfidenceHalfLifeSeconds);

            if (confidence < options.ConfidenceFloor)
            {
                forgotten.Add(id);
                continue;
            }
            contacts[id] = contact with { Confidence = confidence };
        }
        foreach (EntityId id in forgotten) contacts.Remove(id);
        previousFrameTime = frame.Time;
    }

    private void UpdateFactionKnowledge(PlayerId player, IReadOnlyList<Faction> observedFactions)
    {
        HashSet<Faction> observed = [.. observedFactions];
        if (factionCandidates.TryGetValue(player, out HashSet<Faction>? known)) known.IntersectWith(observed);
        else factionCandidates[player] = observed;
    }

    /// <summary>
    /// Confirms destruction only from an explicit destroyed event naming a
    /// known contact id, per spec: absence-from-a-visible-region reduces
    /// confidence (handled in <see cref="UpdateEnemyContacts"/>) but never by
    /// itself sets <see cref="EnemyContact.ConfirmedDestroyed"/>.
    /// </summary>
    private void ApplyDestroyedEvents(ObservationFrame frame)
    {
        foreach (GameEvent evt in frame.Events)
        {
            if (evt.Kind is not (GameEventKind.EntityDestroyed or GameEventKind.EntityKilledByUs)) continue;
            if (evt.Entity is not { } id) continue;
            if (!contacts.TryGetValue(id, out EnemyContact? contact)) continue;
            contacts[id] = contact with { Confidence = 1.0, ConfirmedDestroyed = true };
        }
    }

    private void UpdateScoutingKnowledge(ObservationFrame frame, IReadOnlyList<OwnEntity> own)
    {
        foreach (OwnEntity e in own)
        {
            if (e.Kind != EntityKind.Building) continue;
            if (RegionIsStart(frame.Map, e.Region)) ownStartRegions.Add(e.Region);
        }

        foreach (ObservedEntity e in frame.Entities)
        {
            if (!frame.IsEnemy(e.Owner)) continue;
            if (!rules.TryGet(e.TypeId, out UnitRule rule)) continue;

            // Every seen type is known tech, units included: a Harrier overhead proves the enemy can build
            // Harriers as surely as its airfield would, and counter-picking needs the units.
            if (!seenTech.TryGetValue(e.Owner, out HashSet<string>? tech))
            {
                tech = new HashSet<string>(StringComparer.Ordinal);
                seenTech[e.Owner] = tech;
            }
            tech.Add(e.TypeId);
            if (!techLastSeen.TryGetValue(e.Owner, out Dictionary<string, GameTime>? lastSeenByType))
            {
                lastSeenByType = new Dictionary<string, GameTime>(StringComparer.Ordinal);
                techLastSeen[e.Owner] = lastSeenByType;
            }
            lastSeenByType[e.TypeId] = frame.Time;
            if (rule.Kind != EntityKind.Building) continue;

            RegionId region = RegionOf(frame.Map, e.Position);
            if (!RegionIsStart(frame.Map, region)) continue;
            if (!enemyBuildingStartRegions.TryGetValue(e.Owner, out Dictionary<RegionId, GameTime>? starts))
            {
                starts = [];
                enemyBuildingStartRegions[e.Owner] = starts;
            }
            starts.TryAdd(region, frame.Time);
        }

        foreach (RegionId regionId in frame.VisibleRegions)
        {
            if (!RegionIsStart(frame.Map, regionId) || ownStartRegions.Contains(regionId)) continue;
            bool anyEnemyBuildingThere = enemyBuildingStartRegions.Values.Any(set => set.ContainsKey(regionId));
            if (anyEnemyBuildingThere) emptyScoutedStarts.Remove(regionId);
            else emptyScoutedStarts.Add(regionId);
        }
    }

    private static bool RegionIsStart(MapInfo map, RegionId regionId) =>
        map.Regions.FirstOrDefault(r => r.Id == regionId) is { IsStartLocation: true };

    private void UpdateRecentEvents(ObservationFrame frame)
    {
        recentEvents.AddRange(frame.Events);
        GameTime cutoff = frame.Time.Plus(-options.RecentEventsWindowSeconds);
        recentEvents.RemoveAll(e => e.Time < cutoff);
    }

    private List<EnemyPlayerBelief> BuildEnemyPlayerBeliefs(ObservationFrame frame, IReadOnlyList<EnemyContact> enemies)
    {
        HashSet<PlayerId> players = [];
        foreach (EnemyContact c in enemies) players.Add(c.Owner);
        foreach (PlayerId p in seenTech.Keys) players.Add(p);
        foreach (PlayerId p in enemyBuildingStartRegions.Keys) players.Add(p);
        foreach (PlayerId p in factionCandidates.Keys) players.Add(p);
        // A player this frame names as not an enemy (an ally, a neutral or civilian house) is no enemy player,
        // whatever was remembered about it before the source said so.
        List<PlayerId> ordered = [.. players.Where(frame.IsEnemy).OrderBy(static p => p.Value)];

        Dictionary<PlayerId, RegionId> directStarts = [];
        foreach (PlayerId p in ordered)
        {
            if (DirectStart(p, enemies) is { } direct) directStarts[p] = direct;
        }

        List<RegionId> allStartRegions = [.. frame.Map.Regions
            .Where(static r => r.IsStartLocation)
            .Select(static r => r.Id)
            .OrderBy(static r => r.Value)];

        List<EnemyPlayerBelief> result = [];
        foreach (PlayerId p in ordered)
        {
            RegionId? start = directStarts.TryGetValue(p, out RegionId direct)
                ? direct
                : SuspectStartByElimination(p, directStarts, allStartRegions);

            Faction? faction = factionCandidates.TryGetValue(p, out HashSet<Faction>? candidates) && candidates.Count == 1
                ? candidates.First()
                : null;

            IReadOnlySet<string> tech = seenTech.TryGetValue(p, out HashSet<string>? t)
                ? new HashSet<string>(t, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            GameTime? lastSeen = null;
            foreach (EnemyContact c in enemies)
            {
                if (c.Owner != p) continue;
                if (lastSeen is null || c.LastSeenAt > lastSeen.Value) lastSeen = c.LastSeenAt;
            }

            IReadOnlyDictionary<string, GameTime> techTimes = techLastSeen.TryGetValue(p, out Dictionary<string, GameTime>? times)
                ? new SortedDictionary<string, GameTime>(times, StringComparer.Ordinal)
                : new SortedDictionary<string, GameTime>(StringComparer.Ordinal);
            result.Add(new EnemyPlayerBelief(p, faction, start, tech, lastSeen, techTimes));
        }
        return result;
    }

    /// <summary>
    /// The start region an enemy building of this player says is its base. Our own start is never a candidate (a
    /// tower rush or captured building there is not the enemy's base), and among the rest the choice goes by
    /// evidence, not region id: a start still holding one of the player's live buildings beats one whose buildings
    /// are all destroyed or forgotten, then the earliest sighting (the base is normally found before any
    /// expansion into another empty start), then the lowest id for determinism. Null when no start qualifies.
    /// </summary>
    private RegionId? DirectStart(PlayerId player, IReadOnlyList<EnemyContact> enemies)
    {
        if (!enemyBuildingStartRegions.TryGetValue(player, out Dictionary<RegionId, GameTime>? starts)) return null;
        HashSet<RegionId> held = [.. enemies
            .Where(c => c.Owner == player && c.Kind == EntityKind.Building && !c.ConfirmedDestroyed)
            .Select(static c => c.LastSeenRegion)];
        return starts
            .Where(kv => !ownStartRegions.Contains(kv.Key))
            .OrderByDescending(kv => held.Contains(kv.Key))
            .ThenBy(static kv => kv.Value)
            .ThenBy(static kv => kv.Key.Value)
            .Select(static kv => (RegionId?)kv.Key)
            .FirstOrDefault();
    }

    /// <summary>
    /// When no enemy building has been seen for a player, guess their start by
    /// elimination: if exactly one start region is neither ours, scouted empty,
    /// nor another player's directly-observed start, that is the only region
    /// left it could be.
    /// </summary>
    private RegionId? SuspectStartByElimination(
        PlayerId player, IReadOnlyDictionary<PlayerId, RegionId> directStarts, IReadOnlyList<RegionId> allStartRegions)
    {
        HashSet<RegionId> excluded = [.. ownStartRegions, .. emptyScoutedStarts];
        foreach ((PlayerId other, RegionId otherStart) in directStarts)
        {
            if (other != player) excluded.Add(otherStart);
        }
        List<RegionId> candidates = [.. allStartRegions.Where(r => !excluded.Contains(r))];
        return candidates.Count == 1 ? candidates[0] : null;
    }
}
