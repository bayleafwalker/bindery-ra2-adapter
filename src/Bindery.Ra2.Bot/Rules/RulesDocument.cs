// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Rules;

/// <summary>
/// The on-disk / wire shape of <see cref="IRulesDatabase"/>: a ruleset id, its
/// provenance (operator-imported rulesmd.ini, hash-qualified, or the committed
/// approximate fixture), the buildable/produceable type table, and the
/// weapon-class-versus-armor-class effectiveness matrix. Serialised with
/// <see cref="BotJson.Options"/> (camelCase, string enums) so it round-trips
/// through <see cref="UnitRule"/> unchanged.
/// </summary>
/// <param name="Effectiveness">
/// Outer key is a <see cref="WeaponClass"/> name, inner key an
/// <see cref="ArmorClass"/> name; the value is the damage multiplier. A missing
/// entry means "no fixture opinion" and <see cref="RulesDatabase.Effectiveness"/>
/// treats it as neutral (1.0).
/// </param>
public sealed record RulesDocument(
    string RulesetId,
    string Provenance,
    IReadOnlyList<UnitRule> Units,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> Effectiveness);
