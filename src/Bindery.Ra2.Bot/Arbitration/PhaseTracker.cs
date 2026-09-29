// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Strategy;

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
/// <remarks>
/// A phase is first evaluated by the arbiter's next <c>Update</c> after the intent is activated, so an intent that
/// activates with a later phase's conditions already holding runs one tick of planning at phase 0 before skipping
/// ahead. A phase's army-value attack condition follows the intent's <c>attackArmyValue</c> parameter exactly as
/// the playbook's own does (<see cref="AttackArmyThreshold"/>).
/// </remarks>
public sealed class PhaseTracker
{
    private IReadOnlyList<PlaybookPhase> phases = [];
    private double? attackArmyValueDefault;
    private StrategicIntent? cachedFor;
    private int cachedIndex = -1;
    private StrategicIntent? cached;

    /// <summary>Index of the current phase; 0 when the intent has no phases.</summary>
    public int Index { get; private set; }

    /// <summary>Number of phases; 0 when the intent's playbook has none.</summary>
    public int Count => phases.Count;

    /// <summary>Name of the current phase, or null when the intent's playbook has no phases.</summary>
    public string? Name => phases.Count > 0 ? phases[Index].Name : null;

    /// <summary>Starts tracking a newly activated intent at phase 0 (no intent: stops tracking).</summary>
    public void Reset(StrategicIntent? intent, IPlaybookLibrary playbooks)
    {
        Index = 0;
        cachedFor = null;
        cached = null;
        cachedIndex = -1;
        attackArmyValueDefault = null;
        phases = intent is not null && playbooks.TryGet(intent.PlaybookId, out Playbook playbook) && playbook.Phases is { Count: > 0 } declared
            ? declared
            : [];
        if (phases.Count > 0)
        {
            attackArmyValueDefault = playbooks.TryGet(intent!.PlaybookId, out Playbook owner)
                ? owner.Parameters.FirstOrDefault(static p => p.Name == AttackArmyThreshold.Parameter)?.Default
                : null;
        }
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
            AttackConditions = phase.AttackConditions is { } attack
                ? AttackArmyThreshold.Retarget(attack, attackArmyValueDefault, AttackArmyThreshold.Of(intent.PlaybookParameters))
                : intent.AttackConditions,
        };
        cachedFor = intent;
        cachedIndex = Index;
        return cached;
    }

    /// <summary>
    /// Folds a same-playbook <paramref name="challenger"/> that mirrors the phase in force back onto the
    /// <paramref name="incumbent"/>'s own values. A strategist shown the effective plan may echo the phase's posture,
    /// budget, composition or attack conditions; each field that equals the current phase's effective value is
    /// replaced by the incumbent's underlying one, so that echo is not mistaken for a change and the phase's
    /// overrides stay the phase's, not the intent's. Fields that differ are the challenger's own and kept.
    /// Returns null when the challenger's posture is not the effective posture (a genuine posture change) or no
    /// later phase is in force.
    /// </summary>
    public StrategicIntent? FoldMirror(StrategicIntent challenger, StrategicIntent incumbent)
    {
        if (phases.Count == 0 || Index == 0) return null;
        if (!string.Equals(challenger.PlaybookId, incumbent.PlaybookId, StringComparison.Ordinal)) return null;
        StrategicIntent effective = Effective(incumbent);
        if (challenger.Posture != effective.Posture) return null;
        return challenger with
        {
            Posture = incumbent.Posture,
            Budget = challenger.Budget == effective.Budget ? incumbent.Budget : challenger.Budget,
            Composition = challenger.Composition.SequenceEqual(effective.Composition) ? incumbent.Composition : challenger.Composition,
            AttackConditions = challenger.AttackConditions.SequenceEqual(effective.AttackConditions) ? incumbent.AttackConditions : challenger.AttackConditions,
        };
    }
}
