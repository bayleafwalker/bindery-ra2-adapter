// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Operations;

/// <summary>
/// What the items already in production queues still owe. Where the game charges an item's cost as it builds
/// (retail RA2, and the simulator), the credits on hand do not yet show these commitments: a budget or a saving
/// decision made from credits alone would spend the same money twice.
/// </summary>
public static class ProductionDebt
{
    /// <summary>
    /// The sum over unfinished items of <c>cost × (1 − progress)</c>, rounded up; items of unknown type and
    /// finished items owe nothing.
    /// </summary>
    public static int Unpaid(IRulesDatabase rules, IEnumerable<ProductionQueueState> queues)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(queues);
        double owed = 0;
        foreach (ProductionQueueState queue in queues)
        {
            foreach (QueueItem item in queue.Items)
            {
                if (item.Ready || !rules.TryGet(item.TypeId, out UnitRule rule)) continue;
                double progress = double.IsFinite(item.Progress) ? Math.Clamp(item.Progress, 0, 1) : 0;
                owed += Math.Max(0, rule.Cost) * (1 - progress);
            }
        }
        return (int)Math.Min(int.MaxValue, Math.Ceiling(owed - 1e-9));
    }
}
