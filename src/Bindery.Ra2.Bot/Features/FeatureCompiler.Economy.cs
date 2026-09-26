// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Features;

public sealed partial class FeatureCompiler
{
    /// <summary>
    /// Estimates economy figures. The spending rate is the sum of <c>Cost / BuildSeconds</c> over the items a queue
    /// is actually building: the first <see cref="ProductionQueueState.Factories"/> items that are neither ready nor
    /// on hold, since items waiting behind a busy factory cost nothing yet (the same cap utilization uses).
    ///
    /// Income is the credit change over a trailing <see cref="FeatureOptions.IncomeWindowSeconds"/> window with the
    /// spending over that window added back: <c>(creditsNow - creditsThen + integratedSpend) / window</c>. A
    /// one-compile derivative is unusable here: RA2 debits an item gradually, but the simulator debits its full cost
    /// on enqueue and a harvester unload lands as one lump, so a per-frame delta swings by the whole cost divided by
    /// one frame. Over a window the mismatch between debit timing and the modelled rate is bounded by the item's
    /// cost over the window, and it cancels once the item finishes inside the window. This is an estimator, not
    /// ground truth (repairs and sales are not modelled), which is why the field is named "income" rather than
    /// something implying it is exact.
    ///
    /// Cash runway is how long the bank lasts at the net burn (spending less income); with income covering spending
    /// it is <see cref="UnknownSeconds"/>, because the bank is not running out.
    /// </summary>
    private EconomyFeatures CompileEconomy(BeliefSnapshot snapshot, out double incomePerMinute, out double spendingPerMinute)
    {
        double spendingPerSecond = 0;
        foreach (ProductionQueueState queue in snapshot.Queues)
        {
            int building = 0;
            foreach (QueueItem item in queue.Items)
            {
                if (item.Ready || item.OnHold) continue;
                if (building++ >= queue.Factories) break;
                if (!rules.TryGet(item.TypeId, out UnitRule rule) || rule.BuildSeconds <= 0) continue;
                spendingPerSecond += rule.Cost / rule.BuildSeconds;
            }
        }
        spendingPerMinute = spendingPerSecond * 60.0;

        double incomePerSecond = WindowedIncomePerSecond(snapshot.Time, snapshot.Credits);
        incomePerMinute = incomePerSecond * 60.0;

        double netBurnPerSecond = spendingPerSecond - incomePerSecond;
        double cashRunwaySeconds = netBurnPerSecond <= 1e-9
            ? UnknownSeconds
            : Math.Min(UnknownSeconds, Math.Max(0, snapshot.Credits) / netBurnPerSecond);

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

    /// <summary>
    /// Credits gained per second over the trailing income window: the credit delta from the newest sample at or
    /// before the window start, plus the modelled spending over the same span (from the running
    /// <see cref="spentTotal"/>, which integrates every compile's rate, so the per-second sample cadence does not
    /// coarsen it). The divisor is never shorter than the window: in the opening seconds, before a full window of
    /// history exists, dividing by the short span seen so far turned one enqueue debit into a -270000/min reading.
    /// Holding the divisor at the window bounds the warm-up error exactly like a full window (an item's cost over
    /// the window) at the price of under-reading true income until the window fills. Zero before any history exists.
    /// </summary>
    private double WindowedIncomePerSecond(GameTime now, double credits)
    {
        if (history.Count == 0) return 0.0;
        GameTime cutoff = now.Plus(-options.IncomeWindowSeconds);
        int from = 0;
        for (int i = 0; i < history.Count; i++)
        {
            if (history[i].Time <= cutoff) from = i;
            else break;
        }
        Sample baseline = history[from];
        double span = Math.Max(now.SecondsSince(baseline.Time), options.IncomeWindowSeconds);
        if (span <= 0) return 0.0;
        return (credits - baseline.Credits + (spentTotal - baseline.SpentTotal)) / span;
    }
}
