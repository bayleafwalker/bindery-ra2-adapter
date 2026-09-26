// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
namespace Bindery.Ra2.Bot.Features;

public sealed partial class FeatureCompiler
{
    /// <summary>
    /// Folds each event in <see cref="BeliefSnapshot.RecentEvents"/> exactly
    /// once ever (tracked in <see cref="processedEvents"/>), since the same
    /// window re-lists events across several consecutive compiles.
    /// </summary>
    private void ProcessRecentEvents(BeliefSnapshot snapshot, List<StrategicEvent> events)
    {
        foreach (GameEvent evt in snapshot.RecentEvents)
        {
            if (!processedEvents.Add(evt)) continue;

            switch (evt.Kind)
            {
                case GameEventKind.EntityDestroyed:
                    HandleEntityDestroyed(snapshot, evt, events);
                    break;
                case GameEventKind.EntityKilledByUs:
                    HandleEntityKilledByUs(snapshot, evt);
                    break;
                case GameEventKind.UnderAttack:
                    HandleUnderAttack(snapshot, evt, events);
                    break;
                case GameEventKind.SuperweaponLaunched when evt.Owner != snapshot.Self:
                    superweaponEverLaunched = true;
                    AddEvent(events, StrategicEventKind.SuperweaponDetected, evt.Time, 0.9,
                        $"launched:{evt.TypeId ?? "unknown"}", RegionForCell(snapshot.Map, evt.Position));
                    break;
            }
        }
    }

    private void HandleEntityDestroyed(BeliefSnapshot snapshot, GameEvent evt, List<StrategicEvent> events)
    {
        if (evt.Entity is not { } id || !ownEntityMemory.TryGetValue(id, out OwnEntityMemory info)) return;

        cumulativeLossesValue += info.Value;
        RegionId? region = RegionForCell(snapshot.Map, evt.Position) ?? info.Region;

        if (info.Role == UnitRole.Harvester)
            AddEvent(events, StrategicEventKind.MinerLost, evt.Time, 0.4, evt.TypeId ?? "harvester", region);
        else if (info.Role == UnitRole.Mcv)
            AddEvent(events, StrategicEventKind.McvLost, evt.Time, 0.9, evt.TypeId ?? "mcv", region);
        else if (info.Kind == EntityKind.Building)
            AddEvent(events, StrategicEventKind.BuildingLost, evt.Time, 0.5, evt.TypeId ?? "building", region);
    }

    /// <summary>
    /// Adds the victim's cost to the kills tally. The event's owner is the killer (<see cref="GameEvent.Owner"/>), so
    /// another player's kill (an oracle stream, a hand-built frame) is not ours; and as a second guard that holds even
    /// for a producer that got the owner wrong, a victim that was one of our own entities is a loss
    /// (<see cref="HandleEntityDestroyed"/>), never a kill.
    /// </summary>
    private void HandleEntityKilledByUs(BeliefSnapshot snapshot, GameEvent evt)
    {
        if (evt.Owner is { } killer && killer != snapshot.Self) return;
        if (evt.Entity is { } id && ownEntityMemory.ContainsKey(id)) return;
        if (evt.TypeId is { } typeId && rules.TryGet(typeId, out UnitRule rule)) cumulativeKillsValue += rule.Cost;
    }

    private void HandleUnderAttack(BeliefSnapshot snapshot, GameEvent evt, List<StrategicEvent> events)
    {
        if (evt.Owner != snapshot.Self) return;
        RegionId? region = RegionForCell(snapshot.Map, evt.Position);
        bool isBase = region is { } r && snapshot.Own.Any(e => e.Kind == EntityKind.Building && e.Region == r);
        if (isBase) EmitGated(events, StrategicEventKind.BaseUnderAttack, evt.Time, 0.6, evt.TypeId ?? "attack", region);
    }

    private void PruneProcessedEvents(GameTime now)
    {
        GameTime cutoff = now.Plus(-Math.Max(120.0, options.EventDedupWindowSeconds * 2));
        processedEvents.RemoveWhere(e => e.Time < cutoff);
    }

    /// <summary>
    /// Events that are a function of a change in compiled state rather than a
    /// single raw <see cref="GameEvent"/>: new tech, production mix shifts,
    /// swings in own army value, expansions changing hands, and low power.
    /// Each is guarded by comparing to the previous compile's remembered
    /// state, so it fires once per genuine change, not once per frame.
    /// </summary>
    private void DetectStateTransitionEvents(BeliefSnapshot snapshot, EnemyFeatures enemy, double armyValueCurrent, List<StrategicEvent> events)
    {
        // Expansions change hands with buildings, not with whoever walks through: a harvester or a passing scout
        // flickers region control every few seconds and would re-announce the same field each time.
        HashSet<RegionId> oreRegions = [.. snapshot.Map.Regions.Where(static r => r.HasOre).Select(static r => r.Id)];
        HashSet<RegionId> ownExpansions = [.. snapshot.Own
            .Where(static e => e.Kind == EntityKind.Building)
            .Select(static e => e.Region)
            .Where(oreRegions.Contains)];
        HashSet<RegionId> enemyExpansions = [.. EnemyBuildingRegions(snapshot).Where(oreRegions.Contains)];

        if (hasCompiledBefore)
        {
            foreach (string tech in enemy.KnownTech.OrderBy(static t => t, StringComparer.Ordinal))
            {
                if (!allSeenTechEver.Add(tech)) continue;
                double severity = rules.TryGet(tech, out UnitRule rule) ? Math.Clamp(rule.TechLevel / 8.0, 0.1, 1.0) : 0.3;
                AddEvent(events, StrategicEventKind.NewEnemyTech, snapshot.Time, severity, tech);
            }

            bool hadKnownProductionBaseline = previousKnownProduction.Count > 0;
            foreach (string produced in enemy.KnownProduction.OrderBy(static t => t, StringComparer.Ordinal))
            {
                bool isNew = previousKnownProduction.Add(produced);
                if (isNew && hadKnownProductionBaseline)
                    AddEvent(events, StrategicEventKind.ProductionTransition, snapshot.Time, 0.3, produced);
            }

            // Measured over the configured window against a value floor: from an empty army, the first cheap unit
            // is +100% of nothing, which is not the sudden swing this event exists to flag.
            double window = options.ArmyValueSwingWindowSeconds;
            double baseline = Baseline(snapshot.Time, window, static s => s.ArmyValue);
            double change = armyValueCurrent - baseline;
            double fraction = Math.Abs(change) / Math.Max(Math.Abs(baseline), Math.Max(1.0, options.ArmyValueSwingMinValue));
            if (fraction > options.ArmyValueSwingThreshold)
                EmitGated(events, StrategicEventKind.ArmyValueSwing, snapshot.Time, Math.Min(1.0, fraction),
                    string.Create(CultureInfo.InvariantCulture, $"{(change < 0 ? "-" : "+")}{fraction * 100:0}% in {window:0}s"));

            foreach (RegionId region in ownExpansions.Where(r => !previousOwnExpansions.Contains(r)).OrderBy(static r => r.Value))
                AddEvent(events, StrategicEventKind.ExpansionTaken, snapshot.Time, 0.3, "ore region taken", region);
            foreach (RegionId region in enemyExpansions.Where(r => !previousEnemyExpansions.Contains(r)).OrderBy(static r => r.Value))
                AddEvent(events, StrategicEventKind.EnemyExpansionSeen, snapshot.Time, 0.3, "enemy ore region", region);

            if (snapshot.Power.LowPower)
            {
                double deficit = snapshot.Power.Drained - snapshot.Power.Produced;
                double severity = Math.Min(1.0, deficit / Math.Max(1.0, snapshot.Power.Produced));
                EmitGated(events, StrategicEventKind.LowPower, snapshot.Time, severity, string.Create(CultureInfo.InvariantCulture, $"deficit {deficit:0}"),
                    gapSeconds: options.LowPowerGraceSeconds);
            }
        }
        else
        {
            allSeenTechEver.UnionWith(enemy.KnownTech);
            previousKnownProduction.UnionWith(enemy.KnownProduction);
        }

        previousOwnExpansions.Clear();
        previousOwnExpansions.UnionWith(ownExpansions);
        previousEnemyExpansions.Clear();
        previousEnemyExpansions.UnionWith(enemyExpansions);
    }
}
