// SPDX-License-Identifier: GPL-3.0-or-later
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
                    HandleEntityKilledByUs(evt);
                    break;
                case GameEventKind.UnderAttack:
                    HandleUnderAttack(snapshot, evt, events);
                    break;
                case GameEventKind.SuperweaponLaunched:
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

    private void HandleEntityKilledByUs(GameEvent evt)
    {
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
    private void DetectStateTransitionEvents(
        BeliefSnapshot snapshot, EnemyFeatures enemy, Trend armyValueTrend,
        IReadOnlyDictionary<RegionId, RegionControl> controlByRegion, List<StrategicEvent> events)
    {
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

            double baseline15 = armyValueTrend.Current - armyValueTrend.Delta15s;
            double fraction = baseline15 != 0
                ? Math.Abs(armyValueTrend.Delta15s) / Math.Abs(baseline15)
                : armyValueTrend.Delta15s != 0 ? 1.0 : 0.0;
            if (fraction > options.ArmyValueSwingThreshold)
                EmitGated(events, StrategicEventKind.ArmyValueSwing, snapshot.Time, Math.Min(1.0, fraction), $"{fraction:P0} in 15s");

            foreach (Region region in snapshot.Map.Regions.Where(static r => r.HasOre))
            {
                RegionControl current = controlByRegion[region.Id];
                RegionControl previous = previousOreControl.TryGetValue(region.Id, out RegionControl p) ? p : RegionControl.Unknown;
                if (previous != RegionControl.Own && current == RegionControl.Own)
                    AddEvent(events, StrategicEventKind.ExpansionTaken, snapshot.Time, 0.3, "ore region taken", region.Id);
                else if (previous != RegionControl.Enemy && current == RegionControl.Enemy)
                    AddEvent(events, StrategicEventKind.EnemyExpansionSeen, snapshot.Time, 0.3, "enemy ore region", region.Id);
            }

            if (snapshot.Power.LowPower)
            {
                double deficit = snapshot.Power.Drained - snapshot.Power.Produced;
                double severity = Math.Min(1.0, deficit / Math.Max(1.0, snapshot.Power.Produced));
                EmitGated(events, StrategicEventKind.LowPower, snapshot.Time, severity, $"deficit {deficit:0}");
            }
        }
        else
        {
            allSeenTechEver.UnionWith(enemy.KnownTech);
            previousKnownProduction.UnionWith(enemy.KnownProduction);
        }

        previousOreControl.Clear();
        foreach (Region region in snapshot.Map.Regions.Where(static r => r.HasOre))
            previousOreControl[region.Id] = controlByRegion[region.Id];
    }
}
