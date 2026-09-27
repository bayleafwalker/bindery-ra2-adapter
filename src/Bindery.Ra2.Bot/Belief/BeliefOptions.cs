// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Belief;

/// <summary>
/// Tuning for <see cref="BeliefModel"/>. Defaults follow
/// <c>docs/architecture/strategic-bot.md</c> where the spec states a number
/// (the 60 s confidence half-life); the rest are conventional choices,
/// documented where they are used.
/// </summary>
/// <param name="ConfidenceHalfLifeSeconds">
/// Half-life, in game seconds, of an enemy contact's confidence while it is
/// out of sight and its last-seen region is not currently visible. Spec
/// default: 60 s. Buildings do not decay this way: a structure cannot move,
/// so out of sight it is remembered at its last confidence (as RA2's own fog
/// keeps showing it) until its spot is seen empty or a destroyed event
/// arrives. Decay is applied per frame interval from the previous
/// confidence, never recomputed from the sighting time, so confidence
/// never rises without a sighting.
/// </param>
/// <param name="VacancyHalfLifeSeconds">
/// A faster half-life applied instead of <see cref="ConfidenceHalfLifeSeconds"/>
/// while the contact's last-seen region is currently visible and the unit is
/// not there: we looked, it is not where we last saw it, so confidence in the
/// stale position should fall faster than ordinary fog-of-war forgetting. This
/// is the "not where last seen" case from the spec; it reduces confidence
/// rather than declaring the unit destroyed.
/// </param>
/// <param name="ConfidenceFloor">
/// Contacts whose confidence decays below this value are forgotten (dropped
/// from <see cref="BeliefSnapshot.Enemies"/>) rather than kept at a
/// near-zero confidence forever.
/// </param>
/// <param name="RecentEventsWindowSeconds">
/// How long a <see cref="GameEvent"/> stays in <see cref="BeliefSnapshot.RecentEvents"/>.
/// </param>
public sealed record BeliefOptions(
    double ConfidenceHalfLifeSeconds = 60.0,
    double VacancyHalfLifeSeconds = 10.0,
    double ConfidenceFloor = 0.05,
    double RecentEventsWindowSeconds = 30.0);
