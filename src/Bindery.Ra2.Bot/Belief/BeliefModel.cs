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
    private readonly Dictionary<PlayerId, HashSet<RegionId>> enemyBuildingStartRegions = [];
    private readonly HashSet<RegionId> ownStartRegions = [];
    private readonly HashSet<RegionId> emptyScoutedStarts = [];
    private readonly Dictionary<RegionId, GameTime> regionLastSeen = [];
    private readonly List<GameEvent> recentEvents = [];

    private long version;
    private BeliefSnapshot? current;
    private Dictionary<RegionId, int>? oreLastSeen;

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

        List<EnemyContact> enemies = [.. contacts.Values.OrderBy(static c => c.Id.Value)];
        List<EnemyPlayerBelief> enemyPlayers = BuildEnemyPlayerBeliefs(frame, enemies);

        version++;
        BeliefSnapshot snapshot = new(
            version,
            frame.Time,
            frame.Mode,
            frame.Self,
            frame.Faction,
            frame.Credits,
            frame.Power,
            own,
            enemies,
            enemyPlayers,
            frame.Queues,
            new Dictionary<RegionId, GameTime>(regionLastSeen),
            [.. recentEvents],
            frame.Map,
            oreLastSeen is null ? null : new Dictionary<RegionId, int>(oreLastSeen),
            frame.Superweapons);

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
            if (e.Owner == frame.Self) continue;
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

            double elapsed = frame.Time.SecondsSince(contact.LastSeenAt);
            bool regionVisible = frame.VisibleRegions.Contains(contact.LastSeenRegion);
            double halfLife = regionVisible ? options.VacancyHalfLifeSeconds : options.ConfidenceHalfLifeSeconds;
            double confidence = halfLife <= 0 ? 0.0 : Math.Pow(0.5, elapsed / halfLife);

            if (confidence < options.ConfidenceFloor)
            {
                forgotten.Add(id);
                continue;
            }
            contacts[id] = contact with { Confidence = confidence };
        }
        foreach (EntityId id in forgotten) contacts.Remove(id);
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
            if (e.Owner == frame.Self) continue;
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
            if (!enemyBuildingStartRegions.TryGetValue(e.Owner, out HashSet<RegionId>? starts))
            {
                starts = [];
                enemyBuildingStartRegions[e.Owner] = starts;
            }
            starts.Add(region);
        }

        foreach (RegionId regionId in frame.VisibleRegions)
        {
            if (!RegionIsStart(frame.Map, regionId) || ownStartRegions.Contains(regionId)) continue;
            bool anyEnemyBuildingThere = enemyBuildingStartRegions.Values.Any(set => set.Contains(regionId));
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
        List<PlayerId> ordered = [.. players.OrderBy(static p => p.Value)];

        Dictionary<PlayerId, RegionId> directStarts = [];
        foreach (PlayerId p in ordered)
        {
            if (enemyBuildingStartRegions.TryGetValue(p, out HashSet<RegionId>? starts) && starts.Count > 0)
                directStarts[p] = starts.OrderBy(static r => r.Value).First();
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
