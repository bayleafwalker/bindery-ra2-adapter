// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Baseline.Features;

public sealed partial class FeatureCompiler
{
    /// <summary>
    /// Estimates economy figures. RA2 debits a production item's cost
    /// gradually as it progresses, so the instantaneous spending rate is
    /// approximated as the sum, over every in-progress (not ready, not on
    /// hold) queue item, of its <c>Cost / BuildSeconds</c>. Income is then
    /// whatever credit change is left over once that estimated spend is
    /// backed out of the observed credit delta since the previous compile:
    /// <c>income = (creditsNow - creditsPrev) / dt + spendingRate</c>. This is
    /// an estimator, not ground truth (repair costs, sold buildings and
    /// harvester dumps are not modelled), which is why the field is named
    /// "income" rather than something implying it is exact.
    /// </summary>
    private EconomyFeatures CompileEconomy(BeliefSnapshot snapshot, out double incomePerMinute, out double spendingPerMinute)
    {
        double spendingPerSecond = 0;
        foreach (ProductionQueueState queue in snapshot.Queues)
        {
            foreach (QueueItem item in queue.Items)
            {
                if (item.Ready || item.OnHold) continue;
                if (!rules.TryGet(item.TypeId, out UnitRule rule) || rule.BuildSeconds <= 0) continue;
                spendingPerSecond += rule.Cost / rule.BuildSeconds;
            }
        }
        spendingPerMinute = spendingPerSecond * 60.0;

        if (history.Count > 0)
        {
            Sample previous = history[^1];
            double dt = snapshot.Time.SecondsSince(previous.Time);
            incomePerMinute = dt > 0
                ? ((snapshot.Credits - previous.Credits) / dt + spendingPerSecond) * 60.0
                : 0.0;
        }
        else
        {
            incomePerMinute = 0.0;
        }

        double cashRunwaySeconds = spendingPerSecond <= 0
            ? UnknownSeconds
            : Math.Min(UnknownSeconds, snapshot.Credits / spendingPerSecond);

        int totalFactories = 0, busyFactories = 0;
        foreach (ProductionQueueState queue in snapshot.Queues)
        {
            totalFactories += queue.Factories;
            busyFactories += Math.Min(queue.Factories, queue.Items.Count(static i => !i.Ready && !i.OnHold));
        }
        double utilization = totalFactories > 0 ? (double)busyFactories / totalFactories : 0.0;

        int harvesters = snapshot.Own.Count(static e => e.Role == UnitRole.Harvester);
        int refineries = snapshot.Own.Count(static e => e.Kind == EntityKind.Building && e.Role == UnitRole.Economy);

        // Ore last seen per region over the map's initial ore; a region never seen with an ore report counts
        // at its initial value (unknown is not depleted). Without any ore reports the fraction stays 1.
        double initial = snapshot.Map.OreFields.Sum(static o => (double)o.InitialValue);
        double oreRemainingFraction = 1.0;
        if (snapshot.OreLastSeen is { } seen && initial > 0)
        {
            double remaining = snapshot.Map.OreFields.GroupBy(static o => o.Region)
                .Sum(g => seen.TryGetValue(g.Key, out int left) ? left : g.Sum(static o => (double)o.InitialValue));
            oreRemainingFraction = Math.Clamp(remaining / initial, 0, 1);
        }

        return new EconomyFeatures(
            Trend.Flat(snapshot.Credits), Trend.Flat(incomePerMinute), Trend.Flat(spendingPerMinute),
            cashRunwaySeconds, utilization, harvesters, refineries, oreRemainingFraction, snapshot.Power);
    }
}
