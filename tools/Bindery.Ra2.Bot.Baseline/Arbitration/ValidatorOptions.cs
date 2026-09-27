// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Baseline.Arbitration;

/// <summary>
/// Tunables for <see cref="IntentValidator"/>. Defaults are the spec's values;
/// the scheduler's freshness options default to the same numbers so both layers
/// agree on what "late" means.
/// </summary>
/// <param name="MaxProposalAgeSeconds">A proposal issued more than this long before the current frame is stale (invariant 2).</param>
/// <param name="StaleEventSeverity">A strategic event at or above this severity after issue makes a proposal stale.</param>
/// <param name="MaxExpirySeconds">Longest lifetime an intent may claim; longer expiries are capped with a warning.</param>
/// <param name="BudgetSumTolerance">Budget sums within this of 1 pass untouched.</param>
/// <param name="BudgetNormaliseWindow">Budget sums within this of 1 are normalised with a warning; further off is rejected.</param>
public sealed record ValidatorOptions(
    double MaxProposalAgeSeconds = 15,
    double StaleEventSeverity = 0.7,
    double MaxExpirySeconds = 180,
    double BudgetSumTolerance = 0.01,
    double BudgetNormaliseWindow = 0.2)
{
    public static ValidatorOptions Default { get; } = new();
}
