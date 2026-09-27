// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Sim;

/// <summary>
/// What an object is worth for asset value and trade accounting. Its cost, except for a construction yard the
/// rules price at nothing: the yard is a deployed MCV, so it is worth the MCV it came from. Without this, deploying
/// the MCV would cut a player's asset value by the MCV's price, an undeployed MCV would win a timeout, and
/// destroying a yard would count as destroying nothing.
/// </summary>
public static class SimValuation
{
    public static int ValueOf(IRulesDatabase rules, string typeId)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (!rules.TryGet(typeId, out UnitRule rule)) return 0;
        if (rule.Cost > 0 || rule.Kind != EntityKind.Building || rule.Role != UnitRole.Production) return Math.Max(0, rule.Cost);
        return rules.All
            .Where(r => r.Role == UnitRole.Mcv && r.Factions.Any(rule.Factions.Contains))
            .Select(static r => r.Cost)
            .DefaultIfEmpty(0)
            .Min();
    }
}
