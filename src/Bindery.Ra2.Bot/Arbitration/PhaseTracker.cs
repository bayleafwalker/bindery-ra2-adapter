// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Arbitration;

/// <summary>A phase transition, as logged by <c>strategy.phase_changed</c>.</summary>
public sealed record PhaseChange(int FromIndex, string FromName, int ToIndex, string ToName);

/// <summary>
/// Tracks which <see cref="PlaybookPhase"/> of the active intent's playbook is current, and is the one place that
/// turns an intent plus its phase into the intent execution reads. Progression is forward only: each
/// <see cref="Advance"/> moves to the highest-index phase after the current one whose <see cref="PlaybookPhase.EnterWhen"/>
/// all hold (an empty list never enters), so a later phase whose conditions already hold is entered directly.
/// Deterministic from features; no clock. One tracker per match, owned by <see cref="IntentArbiter"/>.
/// </summary>
public sealed class PhaseTracker
{
    private IReadOnlyList<PlaybookPhase> phases = [];
    private StrategicIntent? cachedFor;
    private int cachedIndex = -1;
    private StrategicIntent? cached;

    /// <summary>Index of the current phase; 0 when the intent has no phases.</summary>
    public int Index { get; private set; }

    /// <summary>Name of the current phase, or null when the intent's playbook has no phases.</summary>
    public string? Name => phases.Count > 0 ? phases[Index].Name : null;

    /// <summary>Starts tracking a newly activated intent at phase 0 (no intent: stops tracking).</summary>
    public void Reset(StrategicIntent? intent, IPlaybookLibrary playbooks)
    {
        Index = 0;
        cachedFor = null;
        cached = null;
        cachedIndex = -1;
        phases = intent is not null && playbooks.TryGet(intent.PlaybookId, out Playbook playbook) && playbook.Phases is { Count: > 0 } declared
            ? declared
            : [];
    }

    /// <summary>Moves forward when a later phase's conditions hold; returns the transition, or null.</summary>
    public PhaseChange? Advance(StrategicFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);
        int target = Index;
        for (int i = phases.Count - 1; i > Index; i--)
        {
            IReadOnlyList<Condition> enter = phases[i].EnterWhen;
            if (enter.Count > 0 && ConditionEvaluator.AllOf(enter, features))
            {
                target = i;
                break;
            }
        }
        if (target == Index) return null;
        PhaseChange change = new(Index, phases[Index].Name, target, phases[target].Name);
        Index = target;
        return change;
    }

    /// <summary>
    /// <paramref name="intent"/> with the current phase's non-null fields applied; the intent itself when there are no
    /// phases or the start phase is current. Cached, so the same intent and phase give the same instance.
    /// </summary>
    public StrategicIntent Effective(StrategicIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (phases.Count == 0 || Index == 0) return intent;
        if (ReferenceEquals(cachedFor, intent) && cachedIndex == Index && cached is not null) return cached;
        PlaybookPhase phase = phases[Index];
        cached = intent with
        {
            Posture = phase.Posture ?? intent.Posture,
            Budget = phase.Budget ?? intent.Budget,
            Composition = phase.Composition ?? intent.Composition,
            AttackConditions = phase.AttackConditions ?? intent.AttackConditions,
        };
        cachedFor = intent;
        cachedIndex = Index;
        return cached;
    }
}
