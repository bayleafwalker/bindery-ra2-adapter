// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot;

/// <summary>
/// Authoritative facts about one buildable type. The strategist is never asked
/// to remember any of these; they are always looked up.
/// </summary>
/// <param name="Power">Positive when the type produces power, negative when it drains it.</param>
/// <param name="Prerequisites">
/// Building type groups; each inner list is satisfied by owning any one of its
/// members (RA2 prerequisite alternatives such as either barracks).
/// </param>
/// <param name="Strength">Hit points.</param>
/// <param name="Damage">Nominal damage per second against its preferred target class.</param>
/// <param name="Range">Weapon range in cells; zero when unarmed.</param>
/// <param name="Speed">Movement speed in cells per second; zero for buildings.</param>
public sealed record UnitRule(
    string TypeId,
    string Name,
    IReadOnlyList<Faction> Factions,
    EntityKind Kind,
    UnitRole Role,
    QueueKind Queue,
    int Cost,
    double BuildSeconds,
    int Power,
    IReadOnlyList<IReadOnlyList<string>> Prerequisites,
    int TechLevel,
    int Strength,
    ArmorClass Armor,
    double Damage,
    WeaponClass Weapon,
    double Range,
    double Speed,
    int Sight,
    bool AntiAir,
    bool Deployable);

public enum ArmorClass { None, Flak, Plate, Light, Medium, Heavy, Wood, Steel, Concrete, Special }

public enum WeaponClass { None, AntiInfantry, AntiArmor, AntiStructure, AntiAir, Artillery, General }

/// <summary>
/// Rules lookup. <see cref="RulesetId"/> names where the facts came from:
/// an operator-imported rulesmd.ini (hash-qualified) or the simulator's
/// approximate fixture, which is not retail-accurate.
/// </summary>
public interface IRulesDatabase
{
    string RulesetId { get; }

    IReadOnlyCollection<UnitRule> All { get; }

    bool TryGet(string typeId, out UnitRule rule);

    UnitRule Get(string typeId);

    bool CanBuild(Faction faction, IReadOnlySet<string> ownedBuildingTypes, string typeId);

    /// <summary>
    /// Shortest ordered list of buildings, missing from <paramref name="ownedBuildingTypes"/>,
    /// needed before <paramref name="typeId"/> becomes buildable; null when unreachable for the faction.
    /// </summary>
    IReadOnlyList<string>? PathTo(Faction faction, IReadOnlySet<string> ownedBuildingTypes, string typeId);

    /// <summary>Expected damage multiplier of <paramref name="attacker"/> against <paramref name="defender"/> (1 = neutral).</summary>
    double Effectiveness(string attacker, string defender);
}
