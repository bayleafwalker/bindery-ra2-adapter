// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Arena;

/// <summary>
/// A minimal, faction-symmetric <see cref="IRulesDatabase"/> the arena builds
/// itself so matches can run end-to-end before package B's
/// <c>bindery-sim-approx</c> fixture and importer exist. It is intentionally
/// small (one tech tier, no naval or superweapons) and not tied to either
/// faction's real asymmetry; the integrator is expected to point the arena
/// at the real <c>RulesDatabase</c> once package B lands, at which point this
/// class becomes unused.
/// </summary>
public sealed class ArenaRuleset : IRulesDatabase
{
    public const string Mcv = "mcv";
    public const string ConstructionYard = "conyard";
    public const string PowerPlant = "power";
    public const string Refinery = "refinery";
    public const string Barracks = "barracks";
    public const string WarFactory = "warfactory";
    public const string Pillbox = "pillbox";
    public const string Harvester = "harvester";
    public const string Rifleman = "rifle";
    public const string Tank = "tank";
    public const string FlakTrack = "flak";

    private static readonly IReadOnlyList<Faction> BothFactions = [Faction.Allied, Faction.Soviet, Faction.Yuri];

    private readonly Dictionary<string, UnitRule> byType;

    public ArenaRuleset()
    {
        List<UnitRule> rules =
        [
            Rule(Mcv, UnitRole.Mcv, EntityKind.Vehicle, QueueKind.Vehicle, cost: 2500, seconds: 1, power: 0, prereq: [], tech: 0,
                 strength: 600, armor: ArmorClass.Heavy, damage: 0, weapon: WeaponClass.None, range: 0, speed: 4, sight: 6, deployable: true),
            Rule(ConstructionYard, UnitRole.Production, EntityKind.Building, QueueKind.Building, cost: 0, seconds: 1, power: 0, prereq: [], tech: 0,
                 strength: 1000, armor: ArmorClass.Concrete, damage: 0, weapon: WeaponClass.None, range: 0, speed: 0, sight: 6),
            Rule(PowerPlant, UnitRole.Power, EntityKind.Building, QueueKind.Building, cost: 800, seconds: 12, power: 200, prereq: [[ConstructionYard]], tech: 1,
                 strength: 400, armor: ArmorClass.Concrete, damage: 0, weapon: WeaponClass.None, range: 0, speed: 0, sight: 4),
            Rule(Refinery, UnitRole.Economy, EntityKind.Building, QueueKind.Building, cost: 2100, seconds: 20, power: -30, prereq: [[ConstructionYard]], tech: 1,
                 strength: 900, armor: ArmorClass.Concrete, damage: 0, weapon: WeaponClass.None, range: 0, speed: 0, sight: 5),
            Rule(Barracks, UnitRole.Production, EntityKind.Building, QueueKind.Building, cost: 500, seconds: 10, power: -20, prereq: [[ConstructionYard]], tech: 1,
                 strength: 400, armor: ArmorClass.Concrete, damage: 0, weapon: WeaponClass.None, range: 0, speed: 0, sight: 4),
            Rule(WarFactory, UnitRole.Production, EntityKind.Building, QueueKind.Building, cost: 2000, seconds: 18, power: -30, prereq: [[ConstructionYard], [PowerPlant]], tech: 2,
                 strength: 700, armor: ArmorClass.Concrete, damage: 0, weapon: WeaponClass.None, range: 0, speed: 0, sight: 5),
            Rule(Pillbox, UnitRole.Defense, EntityKind.Building, QueueKind.Defense, cost: 600, seconds: 8, power: -10, prereq: [[Barracks]], tech: 1,
                 strength: 400, armor: ArmorClass.Concrete, damage: 15, weapon: WeaponClass.General, range: 5, speed: 0, sight: 5),
            Rule(Harvester, UnitRole.Harvester, EntityKind.Vehicle, QueueKind.Vehicle, cost: 1400, seconds: 14, power: 0, prereq: [[Refinery]], tech: 1,
                 strength: 600, armor: ArmorClass.Medium, damage: 0, weapon: WeaponClass.None, range: 0, speed: 6, sight: 5),
            Rule(Rifleman, UnitRole.AntiInfantry, EntityKind.Infantry, QueueKind.Infantry, cost: 100, seconds: 3, power: 0, prereq: [[Barracks]], tech: 1,
                 strength: 100, armor: ArmorClass.None, damage: 8, weapon: WeaponClass.AntiInfantry, range: 4, speed: 3, sight: 5),
            Rule(Tank, UnitRole.AntiArmor, EntityKind.Vehicle, QueueKind.Vehicle, cost: 700, seconds: 9, power: 0, prereq: [[WarFactory]], tech: 2,
                 strength: 400, armor: ArmorClass.Medium, damage: 25, weapon: WeaponClass.AntiArmor, range: 5, speed: 6, sight: 6),
            Rule(FlakTrack, UnitRole.AntiAir, EntityKind.Vehicle, QueueKind.Vehicle, cost: 600, seconds: 8, power: 0, prereq: [[WarFactory]], tech: 2,
                 strength: 300, armor: ArmorClass.Light, damage: 20, weapon: WeaponClass.AntiAir, range: 5, speed: 5, sight: 5, antiAir: true),
        ];
        byType = rules.ToDictionary(r => r.TypeId);
    }

    public string RulesetId => "bindery-arena-approx:v1";

    public IReadOnlyCollection<UnitRule> All => byType.Values;

    public bool TryGet(string typeId, out UnitRule rule) => byType.TryGetValue(typeId, out rule!);

    public UnitRule Get(string typeId) => byType[typeId];

    public bool CanBuild(Faction faction, IReadOnlySet<string> ownedBuildingTypes, string typeId)
    {
        if (!TryGet(typeId, out UnitRule rule) || !rule.Factions.Contains(faction)) return false;
        foreach (IReadOnlyList<string> group in rule.Prerequisites)
        {
            if (!group.Any(ownedBuildingTypes.Contains)) return false;
        }
        return true;
    }

    public IReadOnlyList<string>? PathTo(Faction faction, IReadOnlySet<string> ownedBuildingTypes, string typeId)
    {
        HashSet<string> owned = new(ownedBuildingTypes);
        HashSet<string> visiting = [];
        List<string> path = [];

        bool Resolve(string type, bool isTarget)
        {
            if (!TryGet(type, out UnitRule rule) || !rule.Factions.Contains(faction)) return false;
            if (owned.Contains(type)) return true;
            if (!visiting.Add(type)) return true;
            foreach (IReadOnlyList<string> group in rule.Prerequisites)
            {
                if (group.Any(owned.Contains)) continue;
                string? pick = group.Where(g => TryGet(g, out _))
                                     .OrderBy(g => Get(g).Cost).ThenBy(g => g, StringComparer.Ordinal)
                                     .FirstOrDefault();
                if (pick is null || !Resolve(pick, isTarget: false)) return false;
            }
            if (!isTarget && rule.Kind == EntityKind.Building && owned.Add(type)) path.Add(type);
            return true;
        }

        return Resolve(typeId, isTarget: true) ? path : null;
    }

    public double Effectiveness(string attacker, string defender)
    {
        if (!TryGet(attacker, out UnitRule a) || !TryGet(defender, out UnitRule d)) return 0;
        return (a.Weapon, d.Armor) switch
        {
            (WeaponClass.AntiInfantry, ArmorClass.None) => 1.5,
            (WeaponClass.AntiInfantry, ArmorClass.Light or ArmorClass.Medium) => 0.6,
            (WeaponClass.AntiInfantry, ArmorClass.Heavy or ArmorClass.Concrete) => 0.3,
            (WeaponClass.AntiArmor, ArmorClass.None) => 0.5,
            (WeaponClass.AntiArmor, ArmorClass.Light or ArmorClass.Medium) => 1.3,
            (WeaponClass.AntiArmor, ArmorClass.Heavy) => 1.1,
            (WeaponClass.AntiArmor, ArmorClass.Concrete) => 0.75,
            (WeaponClass.AntiAir, ArmorClass.Flak or ArmorClass.Light) => 1.5,
            (WeaponClass.AntiStructure, ArmorClass.Concrete) => 1.5,
            (WeaponClass.General, _) => 1.0,
            _ => 0.75,
        };
    }

    private static UnitRule Rule(
        string typeId, UnitRole role, EntityKind kind, QueueKind queue, int cost, double seconds, int power,
        IReadOnlyList<IReadOnlyList<string>> prereq, int tech, int strength, ArmorClass armor, double damage,
        WeaponClass weapon, double range, double speed, int sight, bool antiAir = false, bool deployable = false) =>
        new(typeId, typeId, BothFactions, kind, role, queue, cost, seconds, power, prereq, tech, strength, armor, damage, weapon, range, speed, sight, antiAir, deployable);
}
