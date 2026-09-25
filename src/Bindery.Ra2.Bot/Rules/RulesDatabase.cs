// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using System.Text.Json;

namespace Bindery.Ra2.Bot.Rules;

/// <summary>
/// In-memory rules lookup loaded from a <see cref="RulesDocument"/>. Two sources
/// populate it in practice: an operator-run <see cref="RulesmdImporter"/> over
/// their own <c>rulesmd.ini</c> (its JSON output stays outside this repo and
/// carries a hash-qualified <see cref="RulesetId"/>), or the committed
/// approximate fixture used by the simulator and unit tests
/// (<see cref="LoadEmbeddedFixture"/>).
/// </summary>
public sealed class RulesDatabase : IRulesDatabase
{
    /// <summary>
    /// Manifest resource name for the embedded <c>Data/bindery-sim-approx.json</c>
    /// fixture, per the default MSBuild embedded-resource naming convention
    /// (root namespace + folder path + file name).
    /// </summary>
    private const string FixtureResourceName = "Bindery.Ra2.Bot.Data.bindery-sim-approx.json";

    private readonly Dictionary<string, UnitRule> byTypeId;
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> effectivenessMatrix;

    private RulesDatabase(RulesDocument document)
    {
        RulesetId = document.RulesetId;
        Provenance = document.Provenance;
        byTypeId = new Dictionary<string, UnitRule>(StringComparer.Ordinal);
        foreach (UnitRule unit in document.Units) byTypeId[unit.TypeId] = unit;
        // Sorted by type id: All must not depend on the document's array order,
        // which would make consumers that enumerate it non-deterministic.
        All = document.Units.OrderBy(static u => u.TypeId, StringComparer.Ordinal).ToList();
        effectivenessMatrix = document.Effectiveness;
    }

    public string RulesetId { get; }

    /// <summary>Human-readable statement of where these facts came from; see <see cref="RulesDocument"/>.</summary>
    public string Provenance { get; }

    public IReadOnlyCollection<UnitRule> All { get; }

    /// <summary>Parses a <see cref="RulesDocument"/> from JSON text.</summary>
    public static RulesDatabase LoadJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        RulesDocument document = JsonSerializer.Deserialize<RulesDocument>(json, BotJson.Options)
            ?? throw new InvalidDataException("Rules document was empty or malformed.");
        return new RulesDatabase(document);
    }

    /// <summary>Parses a <see cref="RulesDocument"/> from a JSON stream (not closed by this call).</summary>
    public static RulesDatabase LoadJson(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        RulesDocument document = JsonSerializer.Deserialize<RulesDocument>(stream, BotJson.Options)
            ?? throw new InvalidDataException("Rules document was empty or malformed.");
        return new RulesDatabase(document);
    }

    /// <summary>
    /// Loads the committed approximate fixture (<c>bindery-sim-approx</c>),
    /// embedded in this assembly from <c>Data/bindery-sim-approx.json</c>.
    /// </summary>
    public static RulesDatabase LoadEmbeddedFixture()
    {
        Assembly assembly = typeof(RulesDatabase).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(FixtureResourceName);
        if (stream is null)
        {
            string available = string.Join(", ", assembly.GetManifestResourceNames());
            throw new InvalidOperationException(
                $"Embedded fixture '{FixtureResourceName}' was not found. Available resources: {available}");
        }
        return LoadJson(stream);
    }

    public bool TryGet(string typeId, out UnitRule rule) => byTypeId.TryGetValue(typeId, out rule!);

    public UnitRule Get(string typeId) =>
        byTypeId.TryGetValue(typeId, out UnitRule? rule) ? rule : throw new KeyNotFoundException($"Unknown type id '{typeId}'.");

    public bool CanBuild(Faction faction, IReadOnlySet<string> ownedBuildingTypes, string typeId)
    {
        ArgumentNullException.ThrowIfNull(ownedBuildingTypes);
        return TryGet(typeId, out UnitRule rule) && IsEligible(faction, rule) && PrerequisitesSatisfied(rule, ownedBuildingTypes);
    }

    public IReadOnlyList<string>? PathTo(Faction faction, IReadOnlySet<string> ownedBuildingTypes, string typeId)
    {
        ArgumentNullException.ThrowIfNull(ownedBuildingTypes);
        if (!TryGet(typeId, out UnitRule rule) || !IsEligible(faction, rule)) return null;

        HashSet<string> trial = new(ownedBuildingTypes, StringComparer.Ordinal);
        List<string> plan = [];
        return TryResolve(rule, faction, trial, plan, []) ? plan : null;
    }

    /// <summary>
    /// Expected damage multiplier of <paramref name="attacker"/>'s weapon
    /// against <paramref name="defender"/>'s armor. Two hard rules apply before
    /// the weapon/armor matrix is even consulted, matching how RA2 anti-air
    /// weapons behave: a <see cref="WeaponClass.AntiAir"/> weapon only ever
    /// connects with an <see cref="EntityKind.Aircraft"/> defender, and nothing
    /// without anti-air capability (a dedicated anti-air weapon, or the
    /// <see cref="UnitRule.AntiAir"/> dual-purpose flag such as a flak track's
    /// missile pod) can touch an aircraft at all. An attacker with
    /// <see cref="WeaponClass.None"/> (unarmed) always returns 0.
    /// </summary>
    public double Effectiveness(string attacker, string defender)
    {
        if (!TryGet(attacker, out UnitRule a) || a.Weapon == WeaponClass.None) return 0;
        if (!TryGet(defender, out UnitRule d)) return 0;

        bool canHitAircraft = a.Weapon == WeaponClass.AntiAir || a.AntiAir;
        if (d.Kind == EntityKind.Aircraft && !canHitAircraft) return 0;
        if (a.Weapon == WeaponClass.AntiAir && d.Kind != EntityKind.Aircraft) return 0;

        if (effectivenessMatrix.TryGetValue(a.Weapon.ToString(), out IReadOnlyDictionary<string, double>? row) &&
            row.TryGetValue(d.Armor.ToString(), out double multiplier))
        {
            return multiplier;
        }
        return 1.0;
    }

    private static bool IsEligible(Faction faction, UnitRule rule) => rule.TechLevel >= 0 && rule.Factions.Contains(faction);

    private static bool PrerequisitesSatisfied(UnitRule rule, IReadOnlySet<string> owned) =>
        rule.Prerequisites.All(group => group.Any(owned.Contains));

    /// <summary>
    /// Greedy build-order resolver. For every alternative group in
    /// <paramref name="rule"/>'s prerequisites that <paramref name="trial"/>
    /// does not already satisfy, it picks the cheapest reachable member
    /// (ties broken by ordinal type id, for determinism), first resolving that
    /// member's own prerequisites so the returned list is a valid build order.
    /// This is a documented approximation, not a proven global optimum: minimising
    /// buildings over shared prerequisites among several alternative groups is a
    /// weighted set-cover problem (NP-hard) in the general case. RA2's own
    /// prerequisite graphs are shallow (rarely more than two or three levels)
    /// and mostly share a small common base (construction yard, power), so the
    /// greedy choice coincides with the true shortest path in every case the
    /// fixture and imported rulesets are expected to produce.
    /// </summary>
    private bool TryResolve(UnitRule rule, Faction faction, HashSet<string> trial, List<string> plan, HashSet<string> resolving)
    {
        foreach (IReadOnlyList<string> group in rule.Prerequisites)
        {
            if (group.Any(trial.Contains)) continue;

            List<UnitRule> candidates = [];
            foreach (string memberId in group)
            {
                if (TryGet(memberId, out UnitRule member) && IsEligible(faction, member)) candidates.Add(member);
            }
            candidates.Sort(static (x, y) => x.Cost != y.Cost ? x.Cost.CompareTo(y.Cost) : string.CompareOrdinal(x.TypeId, y.TypeId));

            bool satisfied = false;
            foreach (UnitRule candidate in candidates)
            {
                if (TryAdd(candidate, faction, trial, plan, resolving))
                {
                    satisfied = true;
                    break;
                }
            }
            if (!satisfied) return false;
        }
        return true;
    }

    private bool TryAdd(UnitRule candidate, Faction faction, HashSet<string> trial, List<string> plan, HashSet<string> resolving)
    {
        if (trial.Contains(candidate.TypeId)) return true;
        if (!resolving.Add(candidate.TypeId)) return false; // cycle guard: this candidate is already being resolved higher up the stack.

        bool ok = TryResolve(candidate, faction, trial, plan, resolving);
        resolving.Remove(candidate.TypeId);
        if (!ok) return false;

        plan.Add(candidate.TypeId);
        trial.Add(candidate.TypeId);
        return true;
    }
}
