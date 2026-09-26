// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Rules;

namespace Bindery.Ra2.Bot.Playbooks;

/// <summary>One tech goal a roster changed: its replacement, or null when the playbook was dropped.</summary>
/// <param name="Reason">Why, in rule facts (for the arena report and the decision log reader).</param>
public sealed record RosterChange(string PlaybookId, string TechGoal, string? Replacement, string Reason);

/// <summary>A playbook library fitted to a roster, and every change made to fit it.</summary>
public sealed record RosterAdaptation(PlaybookLibrary Library, IReadOnlyList<RosterChange> Changes);

/// <summary>
/// Fits authored playbooks to the roster the bot actually plays (a mod or a changed unit list). Composition targets
/// name roles and adapt by themselves; tech goals name type ids and are the only part a roster change can break.
/// Each tech goal the roster no longer defines (or no longer lets every playbook faction reach) is replaced through
/// <see cref="RosterSubstitution"/>; a playbook with a goal nothing can replace is dropped, because its plan depends
/// on a unit the roster does not have, and the change says so.
/// </summary>
/// <remarks>
/// Without adaptation the authored playbooks still fail safe: the validator rejects an intent whose tech goal the
/// rules do not define (<c>type.unknown</c>) and the arbiter keeps the fallback. Adaptation is what keeps them usable.
/// A playbook the roster leaves untouched is returned as the same instance, so adapting to the authoring roster is
/// an identity.
/// </remarks>
public static class PlaybookRosterAdapter
{
    public static RosterAdaptation Adapt(IReadOnlyList<Playbook> playbooks, IRulesDatabase rules, IRulesDatabase authoredAgainst)
    {
        ArgumentNullException.ThrowIfNull(playbooks);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(authoredAgainst);
        List<Playbook> kept = [];
        List<RosterChange> changes = [];
        foreach (Playbook playbook in playbooks.OrderBy(static p => p.Id, StringComparer.Ordinal))
        {
            List<string> goals = [];
            List<RosterChange> own = [];
            string? missing = null;
            foreach (string goal in playbook.TechGoals)
            {
                string? resolved = RosterSubstitution.Resolve(goal, playbook.Factions, rules, authoredAgainst);
                if (resolved is null)
                {
                    missing = goal;
                    break;
                }
                if (resolved != goal)
                {
                    UnitRule was = authoredAgainst.Get(goal), now = rules.Get(resolved);
                    own.Add(new RosterChange(playbook.Id, goal, resolved,
                        $"{goal} ({was.Role} {was.Kind}, {was.Cost}) is not in {rules.RulesetId}; {resolved} is the nearest-cost {now.Role} {now.Kind} ({now.Cost})"));
                }
                if (!goals.Contains(resolved, StringComparer.Ordinal)) goals.Add(resolved);
            }
            if (missing is not null)
            {
                string role = authoredAgainst.TryGet(missing, out UnitRule m) ? $"{m.Role} {m.Kind}" : "unknown type";
                changes.Add(new RosterChange(playbook.Id, missing, null,
                    $"dropped: tech goal {missing} ({role}) is not in {rules.RulesetId} and no {string.Join("/", playbook.Factions)} unit of that role is buildable"));
                continue;
            }
            changes.AddRange(own);
            kept.Add(own.Count == 0 && goals.Count == playbook.TechGoals.Count ? playbook : playbook with { TechGoals = goals });
        }
        return new RosterAdaptation(new PlaybookLibrary(kept), changes);
    }
}
