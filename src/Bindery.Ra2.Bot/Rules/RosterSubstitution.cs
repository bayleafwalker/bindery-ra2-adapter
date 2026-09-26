// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Rules;

/// <summary>
/// Maps a type id authored against one roster onto another roster (a mod, or a changed unit list), from rule facts
/// alone. Authored content (playbook tech goals, scripted build lists) names concrete type ids; when a roster renames
/// or removes one, what the author meant is still known from the roster it was written against: a unit of some
/// role and kind at some cost.
/// </summary>
/// <remarks>
/// The rule, in order: the id itself when the target roster defines it and every faction can reach it; else the
/// target roster's unit with the same role, eligible for and reachable by every faction, preferring the same kind
/// (a vehicle for a vehicle), then the nearest cost, then the ordinal type id, so the answer is deterministic.
/// Buildings substitute only for buildings. Null when nothing fits: the caller must drop what depended on it rather
/// than guess across roles.
/// </remarks>
public static class RosterSubstitution
{
    /// <summary>The type id in <paramref name="rules"/> standing in for <paramref name="typeId"/> as authored against <paramref name="authoredAgainst"/>, or null.</summary>
    /// <param name="factions">Factions that must all be able to build the answer; empty means any faction.</param>
    public static string? Resolve(string typeId, IReadOnlyList<Faction> factions, IRulesDatabase rules, IRulesDatabase authoredAgainst)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeId);
        ArgumentNullException.ThrowIfNull(factions);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(authoredAgainst);
        if (rules.TryGet(typeId, out UnitRule present) && Usable(present, factions, rules)) return typeId;
        if (!authoredAgainst.TryGet(typeId, out UnitRule authored)) return null;
        bool building = authored.Kind == EntityKind.Building;
        return rules.All
            .Where(r => r.Role == authored.Role && (r.Kind == EntityKind.Building) == building && Usable(r, factions, rules))
            .OrderBy(r => r.Kind == authored.Kind ? 0 : 1)
            .ThenBy(r => Math.Abs(r.Cost - authored.Cost))
            .ThenBy(static r => r.TypeId, StringComparer.Ordinal)
            .Select(static r => r.TypeId)
            .FirstOrDefault();
    }

    private static bool Usable(UnitRule rule, IReadOnlyList<Faction> factions, IRulesDatabase rules)
    {
        if (factions.Count == 0) return rule.Factions.Any(f => rules.PathTo(f, EmptyBase, rule.TypeId) is not null);
        return factions.All(f => rule.Factions.Contains(f) && rules.PathTo(f, EmptyBase, rule.TypeId) is not null);
    }

    private static readonly IReadOnlySet<string> EmptyBase = new HashSet<string>(StringComparer.Ordinal);
}
