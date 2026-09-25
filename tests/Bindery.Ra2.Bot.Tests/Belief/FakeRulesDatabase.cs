// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Tests.Belief;

/// <summary>
/// Minimal in-memory <see cref="IRulesDatabase"/> for Belief and Features
/// tests. Package B (Rules & playbooks) owns the real implementation; this
/// fake exists only so package A's tests do not depend on it.
/// </summary>
internal sealed class FakeRulesDatabase : IRulesDatabase
{
    private readonly Dictionary<string, UnitRule> byTypeId;

    public FakeRulesDatabase(IEnumerable<UnitRule> rules)
    {
        byTypeId = rules.ToDictionary(static r => r.TypeId, StringComparer.Ordinal);
    }

    public string RulesetId => "test-fixture";

    public IReadOnlyCollection<UnitRule> All => byTypeId.Values;

    public bool TryGet(string typeId, out UnitRule rule) => byTypeId.TryGetValue(typeId, out rule!);

    public UnitRule Get(string typeId) => byTypeId[typeId];

    public bool CanBuild(Faction faction, IReadOnlySet<string> ownedBuildingTypes, string typeId) => true;

    public IReadOnlyList<string>? PathTo(Faction faction, IReadOnlySet<string> ownedBuildingTypes, string typeId) => [];

    public double Effectiveness(string attacker, string defender) => 1.0;

    public static UnitRule Rule(
        string typeId,
        Faction faction,
        EntityKind kind,
        UnitRole role,
        QueueKind queue,
        int cost,
        double buildSeconds = 10.0,
        int techLevel = 1,
        double speed = 4.0,
        int sight = 5,
        int power = 0) =>
        new(
            typeId, typeId, [faction], kind, role, queue, cost, buildSeconds, power, [],
            techLevel, 500, ArmorClass.Light, 10, WeaponClass.AntiArmor, 5, speed, sight,
            AntiAir: false, Deployable: role == UnitRole.Mcv);
}
