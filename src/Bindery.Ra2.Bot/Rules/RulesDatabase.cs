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
    private readonly Dictionary<string, UnitRule> byTypeId;
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> effectivenessMatrix;

    private RulesDatabase(RulesDocument document)
    {
        Validate(document);
        RulesetId = document.RulesetId;
        Provenance = document.Provenance ?? string.Empty;
        byTypeId = new Dictionary<string, UnitRule>(StringComparer.Ordinal);
        foreach (UnitRule unit in document.Units) byTypeId.Add(unit.TypeId, unit);
        // Sorted by type id: All must not depend on the document's array order,
        // which would make consumers that enumerate it non-deterministic.
        All = document.Units.OrderBy(static u => u.TypeId, StringComparer.Ordinal).ToList();
        effectivenessMatrix = document.Effectiveness;
    }

    /// <summary>
    /// Rejects a document that would only fail later. System.Text.Json leaves a missing collection null rather than
    /// failing, so without this check a truncated file loads and then throws a NullReferenceException deep inside
    /// <see cref="CanBuild"/> or the planner; and a duplicated type id would sit twice in <see cref="All"/> while
    /// <see cref="TryGet"/> saw only one of the two.
    /// </summary>
    private static void Validate(RulesDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.RulesetId)) throw new InvalidDataException("Rules document has no rulesetId.");
        if (document.Units is null) throw new InvalidDataException($"Rules document '{document.RulesetId}' has no units array.");
        if (document.Effectiveness is null) throw new InvalidDataException($"Rules document '{document.RulesetId}' has no effectiveness matrix.");
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int i = 0; i < document.Units.Count; i++)
        {
            UnitRule? unit = document.Units[i];
            if (unit is null || string.IsNullOrWhiteSpace(unit.TypeId)) throw new InvalidDataException($"Rules unit #{i} is null or has no typeId.");
            if (!seen.Add(unit.TypeId)) throw new InvalidDataException($"Rules unit '{unit.TypeId}' appears more than once.");
            if (unit.Factions is null) throw new InvalidDataException($"Rules unit '{unit.TypeId}' has no factions array.");
            if (unit.Prerequisites is null || unit.Prerequisites.Any(static g => g is null || g.Any(static id => id is null)))
            {
                throw new InvalidDataException($"Rules unit '{unit.TypeId}' has a missing or null prerequisites entry.");
            }
        }
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
    public static RulesDatabase LoadEmbeddedFixture() => LoadJson(EmbeddedFixtureJson(FixtureFile));

    /// <summary>File name of the committed approximate fixture under <c>Data/</c>.</summary>
    public const string FixtureFile = "bindery-sim-approx.json";

    /// <summary>
    /// File name of the committed roster variant under <c>Data/</c> (<c>bindery-sim-variant-roster</c>): the approximate
    /// fixture with a unit renamed, two removed, four re-costed and one added, for testing how the bot copes with a
    /// changed unit roster (see its provenance).
    /// </summary>
    public const string VariantFixtureFile = "bindery-sim-variant-roster.json";

    /// <summary>Loads the committed roster variant (<see cref="VariantFixtureFile"/>).</summary>
    public static RulesDatabase LoadEmbeddedVariantFixture() => LoadJson(EmbeddedFixtureJson(VariantFixtureFile));

    /// <summary>The JSON text of an embedded <c>Data/</c> rules fixture, for tools that pass rules as files.</summary>
    public static string EmbeddedFixtureJson(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        Assembly assembly = typeof(RulesDatabase).Assembly;
        string resource = "Bindery.Ra2.Bot.Data." + fileName;
        using Stream? stream = assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            string available = string.Join(", ", assembly.GetManifestResourceNames());
            throw new InvalidOperationException(
                $"Embedded fixture '{resource}' was not found. Available resources: {available}");
        }
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
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
                // A candidate can resolve some of its own prerequisites before a later group of it fails; those
                // buildings served only the abandoned candidate, so they are rolled back rather than left in the
                // plan, where the planner would build them first for nothing.
                int planMark = plan.Count;
                if (TryAdd(candidate, faction, trial, plan, resolving))
                {
                    satisfied = true;
                    break;
                }
                for (int i = plan.Count - 1; i >= planMark; i--)
                {
                    trial.Remove(plan[i]);
                    plan.RemoveAt(i);
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
