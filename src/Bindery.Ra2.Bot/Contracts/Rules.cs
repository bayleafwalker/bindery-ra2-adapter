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
/// <param name="Repairs">
/// True for a structure that repairs vehicles parked beside it (RA2's service depot, <c>UnitRepair=yes</c>). The
/// role cannot say this: the importer gives a depot the Tech role (it has prerequisites and no factory).
/// </param>
/// <param name="Produces">
/// The production queues this structure is a factory for (RA2's <c>Factory=</c>): a construction yard for the
/// building and defense queues, a barracks for infantry, and so on. Null when the rules do not say, in which case a
/// consumer that needs factories derives them from prerequisites.
/// </param>
/// <param name="Verses">
/// The ground weapon's warhead <c>Verses=</c> as fractions, in RA2 armor order (none, flak, plate, light, medium, heavy,
/// wood, steel, concrete, special_1, special_2); when present it decides effectiveness by the defender's armor instead
/// of the weapon-class matrix. Null for hand-authored rules.
/// </param>
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
    bool Deployable,
    bool Repairs = false,
    IReadOnlyList<QueueKind>? Produces = null,
    IReadOnlyList<double>? Verses = null);

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

    /// <summary>
    /// RA2's <c>[General] MultipleFactory=</c>: each factory beyond the first multiplies a queue's build time by this.
    /// Null when the rules do not say (the hand-authored fixture), in which case the simulator keeps its own model.
    /// </summary>
    double? MultipleFactory => null;
}

/// <summary>
/// The RA2 multiple-factory speed-up, shared by the simulator and the feature compiler so a queue's active item
/// builds at the same rate in both: one item at a time per queue, sped up by its factory count.
/// </summary>
public static class ProductionRules
{
    /// <summary>
    /// Per-second build multiplier for a queue with <paramref name="factories"/> factories of its kind: RA2's
    /// <c>[General] MultipleFactory=</c> per extra factory when the rules give one, else <c>sqrt(factories)</c>
    /// (the hand-authored fixture's approximation). Zero factories pauses the queue (0); RA2 halves this on low
    /// power, which callers with power available apply on top.
    /// </summary>
    public static double FactorySpeed(IRulesDatabase rules, int factories)
    {
        if (factories <= 0) return 0;
        return rules.MultipleFactory is { } perExtra and > 0 ? Math.Pow(1 / perExtra, factories - 1) : Math.Sqrt(factories);
    }
}
