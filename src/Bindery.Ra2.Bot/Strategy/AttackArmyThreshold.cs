// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Strategy;

/// <summary>
/// Keeps an intent's army-value attack condition in step with its <c>attackArmyValue</c> parameter. The planner
/// attacks only when every attack condition holds and army value reaches the parameter, and the timing playbooks
/// also carry the parameter's default as an <c>OwnArmyValue</c> attack condition. Wherever a strategist sets
/// parameters but not conditions (the deterministic strategists through <see cref="IntentComposer"/>, and the LLM's
/// Parameters vocabulary tier and Refine mode), the conditions come from elsewhere, so a lowered parameter would be
/// masked by the old condition and silently do nothing.
/// </summary>
public static class AttackArmyThreshold
{
    /// <summary>The playbook parameter the planner reads as the attack's army-value threshold.</summary>
    public const string Parameter = "attackArmyValue";

    /// <summary>
    /// The conditions with every <c>OwnArmyValue</c> lower bound (<c>Ge</c>/<c>Gt</c>) at exactly
    /// <paramref name="from"/> moved to <paramref name="to"/>. A bound at any other value was set on its own
    /// account (by a strategist that wrote conditions) and is left alone, as is everything when either value is
    /// unknown or they are equal.
    /// </summary>
    public static IReadOnlyList<Condition> Retarget(IReadOnlyList<Condition> conditions, double? from, double? to)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        if (from is not { } old || to is not { } target || old.Equals(target)) return conditions;
        return conditions
            .Select(c => c.Metric == ConditionMetric.OwnArmyValue && c.Op is Comparison.Ge or Comparison.Gt && c.Threshold.Equals(old)
                ? c with { Threshold = target }
                : c)
            .ToList();
    }

    /// <summary>The <see cref="Parameter"/> value of a parameter map, or null when it has none.</summary>
    public static double? Of(IReadOnlyDictionary<string, double> parameters) =>
        parameters.TryGetValue(Parameter, out double value) ? value : null;
}
