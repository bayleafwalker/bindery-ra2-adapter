// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Tests.Sim;

/// <summary>
/// A small, hand-written fake of <see cref="IRulesDatabase"/> for the Sim
/// package's own tests. Deliberately tiny: just enough unit/building
/// variety to exercise economy, production, prerequisites, combat and
/// victory without depending on package B's real rules database.
/// </summary>
internal sealed class TestRules : IRulesDatabase
{
    public const string Mcv = "test-mcv";
    public const string ConYard = "test-conyard";
    public const string Power = "test-power";
    public const string Refinery = "test-refinery";
    public const string Barracks = "test-barracks";
    public const string WarFactory = "test-warfactory";
    public const string Harvester = "test-harvester";
    public const string Weak = "test-weak";
    public const string Strong = "test-strong";
    public const string AntiAirOnly = "test-aa";
    public const string Aircraft = "test-aircraft";

    private static readonly IReadOnlyList<Faction> AnyFaction = [Faction.Allied, Faction.Soviet, Faction.Yuri];

    private readonly Dictionary<string, UnitRule> byType;

    public TestRules()
    {
        List<UnitRule> rules =
        [
            R(Mcv, UnitRole.Mcv, EntityKind.Vehicle, QueueKind.Vehicle, 1000, 1, 0, [], 0, 500, ArmorClass.Heavy, 0, WeaponClass.None, 0, 4, 6, deployable: true),
            R(ConYard, UnitRole.Production, EntityKind.Building, QueueKind.Building, 0, 1, 0, [], 0, 1000, ArmorClass.Concrete, 0, WeaponClass.None, 0, 0, 6),
            R(Power, UnitRole.Power, EntityKind.Building, QueueKind.Building, 300, 2, 100, [[ConYard]], 1, 300, ArmorClass.Concrete, 0, WeaponClass.None, 0, 0, 4),
            R(Refinery, UnitRole.Economy, EntityKind.Building, QueueKind.Building, 500, 2, -10, [[ConYard]], 1, 500, ArmorClass.Concrete, 0, WeaponClass.None, 0, 0, 5),
            R(Barracks, UnitRole.Production, EntityKind.Building, QueueKind.Building, 200, 2, -10, [[ConYard]], 1, 300, ArmorClass.Concrete, 0, WeaponClass.None, 0, 0, 4),
            R(WarFactory, UnitRole.Production, EntityKind.Building, QueueKind.Building, 400, 2, -10, [[ConYard]], 2, 400, ArmorClass.Concrete, 0, WeaponClass.None, 0, 0, 4),
            R(Harvester, UnitRole.Harvester, EntityKind.Vehicle, QueueKind.Vehicle, 300, 2, 0, [[Refinery]], 1, 300, ArmorClass.Medium, 0, WeaponClass.None, 0, 8, 5),
            R(Weak, UnitRole.AntiInfantry, EntityKind.Infantry, QueueKind.Infantry, 50, 1, 0, [[Barracks]], 1, 50, ArmorClass.None, 3, WeaponClass.AntiInfantry, 4, 3, 5),
            R(Strong, UnitRole.AntiArmor, EntityKind.Vehicle, QueueKind.Vehicle, 300, 1, 0, [[WarFactory]], 1, 400, ArmorClass.Medium, 40, WeaponClass.AntiArmor, 5, 5, 5),
            R(AntiAirOnly, UnitRole.AntiAir, EntityKind.Vehicle, QueueKind.Vehicle, 200, 1, 0, [[WarFactory]], 1, 150, ArmorClass.Light, 20, WeaponClass.AntiAir, 5, 5, 5, antiAir: true),
            R(Aircraft, UnitRole.AntiArmor, EntityKind.Aircraft, QueueKind.Aircraft, 200, 1, 0, [[WarFactory]], 1, 100, ArmorClass.Flak, 10, WeaponClass.General, 5, 10, 6),
        ];
        byType = rules.ToDictionary(r => r.TypeId);
    }

    public string RulesetId => "test-rules:v1";

    public IReadOnlyCollection<UnitRule> All => byType.Values;

    public bool TryGet(string typeId, out UnitRule rule) => byType.TryGetValue(typeId, out rule!);

    public UnitRule Get(string typeId) => byType[typeId];

    public bool CanBuild(Faction faction, IReadOnlySet<string> ownedBuildingTypes, string typeId)
    {
        if (!TryGet(typeId, out UnitRule rule) || !rule.Factions.Contains(faction)) return false;
        return rule.Prerequisites.All(group => group.Any(ownedBuildingTypes.Contains));
    }

    public IReadOnlyList<string>? PathTo(Faction faction, IReadOnlySet<string> ownedBuildingTypes, string typeId)
    {
        HashSet<string> owned = new(ownedBuildingTypes);
        List<string> path = [];
        bool Resolve(string type, bool isTarget)
        {
            if (!TryGet(type, out UnitRule rule) || !rule.Factions.Contains(faction)) return false;
            if (owned.Contains(type)) return true;
            foreach (IReadOnlyList<string> group in rule.Prerequisites)
            {
                if (group.Any(owned.Contains)) continue;
                string? pick = group.Where(g => TryGet(g, out _)).OrderBy(g => Get(g).Cost).FirstOrDefault();
                if (pick is null || !Resolve(pick, false)) return false;
            }
            if (!isTarget && rule.Kind == EntityKind.Building && owned.Add(type)) path.Add(type);
            return true;
        }
        return Resolve(typeId, true) ? path : null;
    }

    public double Effectiveness(string attacker, string defender) => 1.0;

    private static UnitRule R(
        string typeId, UnitRole role, EntityKind kind, QueueKind queue, int cost, double seconds, int power,
        IReadOnlyList<IReadOnlyList<string>> prereq, int tech, int strength, ArmorClass armor, double damage,
        WeaponClass weapon, double range, double speed, int sight, bool antiAir = false, bool deployable = false) =>
        new(typeId, typeId, AnyFaction, kind, role, queue, cost, seconds, power, prereq, tech, strength, armor, damage, weapon, range, speed, sight, antiAir, deployable);
}
