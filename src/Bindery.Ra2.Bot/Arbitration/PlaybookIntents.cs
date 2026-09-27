// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Arbitration;

/// <summary>
/// Builds an intent that is exactly a playbook's defaults. The runtime uses it as
/// the emergency intent when no strategist (not even the fallback) produced a
/// valid proposal, so the planner is never left without direction; tests and
/// scripted strategists use it to make well-formed intents cheaply.
/// </summary>
public static class PlaybookIntents
{
    /// <summary>
    /// Deterministic playbook choice for a faction: <paramref name="preferredId"/> when
    /// it exists and serves the faction, otherwise the faction's playbook with the
    /// smallest id (ordinal), otherwise null.
    /// </summary>
    public static Playbook? Choose(IPlaybookLibrary playbooks, Faction faction, string? preferredId)
    {
        ArgumentNullException.ThrowIfNull(playbooks);
        if (preferredId is not null && playbooks.TryGet(preferredId, out Playbook preferred)
            && (preferred.Factions.Count == 0 || preferred.Factions.Contains(faction)))
        {
            return preferred;
        }
        return playbooks.For(faction).OrderBy(static p => p.Id, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>An intent carrying every default of <paramref name="playbook"/>, issued at the features' snapshot.</summary>
    public static StrategicIntent FromPlaybook(
        Playbook playbook,
        StrategicFeatures features,
        string intentId,
        IntentSource source,
        double lifetimeSeconds = 60,
        double confidence = 0.5,
        string? rationale = null)
    {
        ArgumentNullException.ThrowIfNull(playbook);
        ArgumentNullException.ThrowIfNull(features);
        SortedDictionary<string, double> parameters = new(StringComparer.Ordinal);
        foreach (PlaybookParameter parameter in playbook.Parameters) parameters[parameter.Name] = parameter.Default;
        List<Objective> objectives = playbook.TechGoals.Select(static (t, i) => new Objective(ObjectiveKind.TechTo, null, t, 10 + i)).ToList();
        return new StrategicIntent(
            intentId,
            source,
            features.SnapshotVersion,
            features.Time,
            features.Time.Plus(lifetimeSeconds),
            playbook.Posture,
            playbook.Id,
            parameters,
            objectives,
            playbook.Budget,
            playbook.Composition,
            [],
            playbook.AttackConditions,
            playbook.AbortTriggers,
            [],
            confidence,
            [],
            rationale);
    }
}
