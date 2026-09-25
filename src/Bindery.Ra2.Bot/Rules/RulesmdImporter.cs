// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Bindery.Ra2.Bot.Rules;

/// <summary>
/// Parses an operator-supplied <c>rulesmd.ini</c> (Red Alert 2 / Yuri's
/// Revenge INI format) into a <see cref="RulesDocument"/>. The importer never
/// reads or writes real game files itself; callers hand it a <see cref="TextReader"/>
/// (over whatever source they chose, kept outside this repo per
/// <c>ci/verify-no-assets.ps1</c>) and the SHA-256 of that source, which becomes
/// part of the resulting <see cref="RulesDocument.RulesetId"/> so a ruleset can
/// always be traced back to the exact file it came from.
/// </summary>
/// <remarks>
/// INI conventions handled: <c>;</c> line comments, duplicate keys (last write
/// wins within a section), and repeated section headers (later occurrences
/// merge into the first, again last-write-wins per key) — all common in
/// community rulesmd.ini variants. Everything this importer infers rather than
/// reads verbatim (weapon class, build time, unit role) is a documented
/// heuristic; see the method-level doc comments below for each table.
/// </remarks>
public static class RulesmdImporter
{
    /// <summary>
    /// Country -> faction mapping, per the spec's list. A country not in this
    /// table (Yuri sub-factions beyond <c>YuriCountry</c>, or an unknown mod
    /// addition) is simply not recognised as belonging to any faction; a unit
    /// whose entire <c>Owner=</c> list falls outside this table is dropped
    /// (see <see cref="ParseFactions"/>).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Faction> CountryToFaction = new Dictionary<string, Faction>(StringComparer.OrdinalIgnoreCase)
    {
        ["British"] = Faction.Allied,
        ["French"] = Faction.Allied,
        ["Germans"] = Faction.Allied,
        ["Americans"] = Faction.Allied,
        ["Alliance"] = Faction.Allied,
        ["Russians"] = Faction.Soviet,
        ["Confederation"] = Faction.Soviet,
        ["Africans"] = Faction.Soviet,
        ["Arabs"] = Faction.Soviet,
        ["YuriCountry"] = Faction.Yuri,
    };

    /// <summary>The four type-list sections this importer reads, and the entity/queue kind each implies by default.</summary>
    private static readonly (string Section, EntityKind Kind, QueueKind Queue)[] TypeLists =
    [
        ("BuildingTypes", EntityKind.Building, QueueKind.Building),
        ("InfantryTypes", EntityKind.Infantry, QueueKind.Infantry),
        ("VehicleTypes", EntityKind.Vehicle, QueueKind.Vehicle),
        ("AircraftTypes", EntityKind.Aircraft, QueueKind.Aircraft),
    ];

    /// <summary>
    /// Fallback weapon-class-versus-armor-class multipliers used for every
    /// imported ruleset, since deriving true per-warhead-versus-armor
    /// percentages from <c>Verses=</c> lists into our coarser
    /// <see cref="ArmorClass"/> buckets is out of scope for a heuristic
    /// importer. It is deliberately identical in shape to the hand-authored
    /// fixture's matrix (<c>Data/bindery-sim-approx.json</c>) so downstream
    /// consumers see consistent behaviour regardless of ruleset source.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> DefaultEffectiveness = BuildDefaultEffectiveness();

    /// <summary>Parses <paramref name="ini"/> into a <see cref="RulesDocument"/> tagged with <paramref name="sourceSha256"/>.</summary>
    public static RulesDocument Import(TextReader ini, string sourceSha256)
    {
        ArgumentNullException.ThrowIfNull(ini);
        ArgumentNullException.ThrowIfNull(sourceSha256);

        Dictionary<string, IniSection> sections = ParseSections(ini);
        Dictionary<string, IReadOnlyList<string>> genericPrereqs = ParseGenericPrerequisites(sections);

        List<UnitRule> units = [];
        foreach ((string sectionName, EntityKind kind, QueueKind queue) in TypeLists)
        {
            if (!sections.TryGetValue(sectionName, out IniSection? list)) continue;
            foreach (string typeId in list.ValuesInOrder)
            {
                UnitRule? unit = BuildUnit(typeId, kind, queue, sections, genericPrereqs);
                if (unit is not null) units.Add(unit);
            }
        }
        // Deterministic output order, independent of the INI's own layout.
        units.Sort(static (a, b) => string.CompareOrdinal(a.TypeId, b.TypeId));

        return new RulesDocument(
            $"rulesmd-sha256:{sourceSha256}",
            "Imported from an operator-supplied rulesmd.ini via RulesmdImporter. Weapon class, unit role and build " +
            "time are heuristic estimates documented on RulesmdImporter, not a retail-accurate damage simulation.",
            units,
            DefaultEffectiveness);
    }

    /// <summary>
    /// Builds one <see cref="UnitRule"/> from its own INI section, or returns
    /// null when the type has no section (nothing to import), a negative
    /// <c>TechLevel</c> (unbuildable, per the spec), or no <c>Owner=</c> token
    /// this importer recognises (see <see cref="ParseFactions"/>) — such a type
    /// cannot be placed on any faction's build tree and is skipped rather than
    /// emitted with an empty faction list.
    /// </summary>
    private static UnitRule? BuildUnit(
        string typeId,
        EntityKind kind,
        QueueKind defaultQueue,
        IReadOnlyDictionary<string, IniSection> sections,
        IReadOnlyDictionary<string, IReadOnlyList<string>> genericPrereqs)
    {
        if (!sections.TryGetValue(typeId, out IniSection? section)) return null;

        int techLevel = GetInt(section, "TechLevel", 0);
        if (techLevel < 0) return null;

        IReadOnlyList<Faction> factions = ParseFactions(section);
        if (factions.Count == 0) return null;

        bool naval = GetBool(section, "Naval", false);
        EntityKind resolvedKind = naval ? EntityKind.Naval : kind;
        QueueKind queue = naval ? QueueKind.Naval : defaultQueue;

        int cost = GetInt(section, "Cost", 0);
        // BuildSeconds formula: cost / (1000 credits per 60 seconds), i.e. a
        // full-price (1000-credit) structure takes about a minute to complete
        // at normal game speed; clamped to a 2-second floor so free/near-free
        // items (a starting construction yard) still report a sane duration.
        // This is an explicitly approximate convention, not retail timing.
        double buildSeconds = Math.Max(2.0, cost / (1000.0 / 60.0));

        int strength = Math.Max(1, GetInt(section, "Strength", 1));
        int power = GetInt(section, "Power", 0);
        ArmorClass armor = Enum.TryParse(GetString(section, "Armor", "None"), true, out ArmorClass parsedArmor) ? parsedArmor : ArmorClass.None;
        double speed = GetDouble(section, "Speed", 0);
        int sight = GetInt(section, "Sight", 0);
        bool deployable = GetBool(section, "Deploys", false) || section.TryGet("DeploysInto", out _);

        IReadOnlyList<IReadOnlyList<string>> prerequisites = ParsePrerequisites(section, genericPrereqs);
        (WeaponClass weapon, double damage, double range, bool antiAirFlag) = ResolveWeapon(section, sections);

        UnitRole role = InferRole(resolvedKind, section, weapon, power, prerequisites.Count > 0, sight, speed);
        if (resolvedKind == EntityKind.Building && role == UnitRole.Defense) queue = QueueKind.Defense;

        string name = GetString(section, "UIName", GetString(section, "Name", typeId));

        return new UnitRule(
            typeId, name, factions, resolvedKind, role, queue, cost, buildSeconds, power, prerequisites, techLevel,
            strength, armor, damage, weapon, range, speed, sight, antiAirFlag, deployable);
    }

    /// <summary>Maps <c>Owner=</c> country tokens to factions via <see cref="CountryToFaction"/>, deduplicated and sorted for determinism.</summary>
    private static IReadOnlyList<Faction> ParseFactions(IniSection section)
    {
        if (!section.TryGet("Owner", out string owner)) return [];
        SortedSet<Faction> factions = [];
        foreach (string token in SplitList(owner))
        {
            if (CountryToFaction.TryGetValue(token, out Faction faction)) factions.Add(faction);
        }
        return factions.ToList();
    }

    /// <summary>
    /// Expands <c>Prerequisite=</c> into alternative groups. Each comma-separated
    /// token is one AND-group; a token found in <c>[GenericPrerequisites]</c>
    /// (e.g. <c>POWER</c>, <c>FACTORY</c>, <c>BARRACKS</c>, <c>RADAR</c>,
    /// <c>TECH</c>) expands to that macro's OR-list of concrete building ids,
    /// while a plain building id becomes a singleton OR-group of itself.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<string>> ParsePrerequisites(
        IniSection section, IReadOnlyDictionary<string, IReadOnlyList<string>> genericPrereqs)
    {
        if (!section.TryGet("Prerequisite", out string raw) || raw.Length == 0) return [];
        List<IReadOnlyList<string>> groups = [];
        foreach (string token in SplitList(raw))
        {
            groups.Add(genericPrereqs.TryGetValue(token, out IReadOnlyList<string>? alternatives) ? alternatives : [token]);
        }
        return groups;
    }

    /// <summary>
    /// Resolves a unit's primary weapon into (weapon class, estimated DPS, range,
    /// explicit anti-air flag). DPS is estimated as <c>Damage * FramesPerSecond / ROF</c>
    /// (ROF is in frames, per RA2 convention); when ROF is missing or zero the
    /// raw damage value is used as a fallback DPS estimate. See
    /// <see cref="ClassifyByVerses"/> for how the weapon class itself is chosen.
    /// </summary>
    private static (WeaponClass WeaponClass, double Damage, double Range, bool AntiAir) ResolveWeapon(
        IniSection unitSection, IReadOnlyDictionary<string, IniSection> sections)
    {
        if (!unitSection.TryGet("Primary", out string weaponName) || weaponName.Length == 0 || !sections.TryGetValue(weaponName, out IniSection? weapon))
        {
            return (WeaponClass.None, 0, 0, false);
        }

        double damage = GetDouble(weapon, "Damage", 0);
        double rof = GetDouble(weapon, "ROF", 0);
        double range = GetDouble(weapon, "Range", 0);
        double dps = rof > 0 ? damage * GameTime.FramesPerSecond / rof : damage;
        bool explicitAntiAir = GetBool(weapon, "AA", false) || GetBool(unitSection, "AA", false);

        if (explicitAntiAir) return (WeaponClass.AntiAir, dps, range, true);

        if (weapon.TryGet("Warhead", out string warheadName) &&
            sections.TryGetValue(warheadName, out IniSection? warhead) &&
            TryParseVerses(warhead, out double[] verses))
        {
            return (ClassifyByVerses(verses, range), dps, range, false);
        }

        return (damage > 0 ? WeaponClass.General : WeaponClass.None, dps, range, false);
    }

    private static bool TryParseVerses(IniSection warhead, out double[] verses)
    {
        verses = [];
        if (!warhead.TryGet("Verses", out string raw) || raw.Length == 0) return false;
        string[] tokens = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return false;
        double[] parsed = new double[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            parsed[i] = double.TryParse(tokens[i].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 100;
        }
        verses = parsed;
        return true;
    }

    /// <summary>
    /// Heuristic weapon-class classification from a warhead's <c>Verses=</c>
    /// list, read in the order this importer assumes throughout:
    /// [none, wood, light, heavy, concrete, special_1, special_2, special_3].
    /// This is this importer's own convention for turning "how hard does it hit
    /// each armor class" into one of our coarse <see cref="WeaponClass"/>
    /// buckets — not a retail-accurate simulation. It averages the
    /// infantry-like entries (none, wood) against the vehicle-like entries
    /// (light, heavy) and the concrete (structure) entry, and assigns the class
    /// with the highest average; ties favour <see cref="WeaponClass.AntiArmor"/>,
    /// RA2's most common "generalist" class. A weapon that is anti-armor by
    /// this test but has range 8 cells or more is reclassified as
    /// <see cref="WeaponClass.Artillery"/>, matching how RA2 treats long-range
    /// direct-fire vehicles (V3, artillery pieces) as a distinct siege role.
    /// </summary>
    private static WeaponClass ClassifyByVerses(double[] verses, double range)
    {
        double At(int i) => i < verses.Length ? verses[i] : 100;
        double infantry = (At(0) + At(1)) / 2;
        double armor = (At(2) + At(3)) / 2;
        double structure = At(4);

        WeaponClass result = armor >= infantry && armor >= structure
            ? WeaponClass.AntiArmor
            : structure > infantry && structure > armor
                ? WeaponClass.AntiStructure
                : WeaponClass.AntiInfantry;

        return result == WeaponClass.AntiArmor && range >= 8 ? WeaponClass.Artillery : result;
    }

    /// <summary>
    /// Role-inference table (documented per the spec's requirement), applied in
    /// this priority order:
    /// <list type="number">
    /// <item>Non-building, <c>Harvester=yes</c> -&gt; <see cref="UnitRole.Harvester"/>.</item>
    /// <item>Non-building, <c>Deploys=yes</c> with a <c>DeploysInto=</c> target -&gt; <see cref="UnitRole.Mcv"/>.</item>
    /// <item>Non-building, <c>Engineer=yes</c> -&gt; <see cref="UnitRole.Engineer"/>.</item>
    /// <item>Building, <c>Refinery=yes</c> -&gt; <see cref="UnitRole.Economy"/>.</item>
    /// <item>Building, positive net <c>Power</c> and no weapon -&gt; <see cref="UnitRole.Power"/>.</item>
    /// <item>Building, <c>SuperWeapon=</c> present -&gt; <see cref="UnitRole.Superweapon"/>.</item>
    /// <item>Building, <c>Factory=</c> present (this importer's own convention for
    /// "what this structure produces": Infantry/Vehicle/Aircraft/Naval) -&gt; <see cref="UnitRole.Production"/>.</item>
    /// <item>Building with a resolved weapon and none of the above -&gt; <see cref="UnitRole.Defense"/>.</item>
    /// <item>Building with none of the above: <see cref="UnitRole.Tech"/> when it has
    /// prerequisites of its own (an upgrade/tech structure), else <see cref="UnitRole.Support"/>.</item>
    /// <item>Non-building, mapped from its resolved <see cref="WeaponClass"/>:
    /// AntiArmor/AntiInfantry/AntiAir/Artillery map to the role of the same name.</item>
    /// <item>Non-building with no weapon and sight at least double its speed
    /// (cheap, fast-seeing) -&gt; <see cref="UnitRole.Scout"/>; otherwise <see cref="UnitRole.Support"/>.</item>
    /// </list>
    /// </summary>
    private static UnitRole InferRole(
        EntityKind kind, IniSection section, WeaponClass weapon, int power, bool hasPrerequisites, int sight, double speed)
    {
        if (kind != EntityKind.Building)
        {
            if (GetBool(section, "Harvester", false)) return UnitRole.Harvester;
            if (GetBool(section, "Deploys", false) && section.TryGet("DeploysInto", out _)) return UnitRole.Mcv;
            if (GetBool(section, "Engineer", false)) return UnitRole.Engineer;
            return weapon switch
            {
                WeaponClass.AntiArmor => UnitRole.AntiArmor,
                WeaponClass.AntiInfantry => UnitRole.AntiInfantry,
                WeaponClass.AntiAir => UnitRole.AntiAir,
                WeaponClass.Artillery => UnitRole.Artillery,
                WeaponClass.None => sight > 0 && speed > 0 && sight >= speed * 2 ? UnitRole.Scout : UnitRole.Support,
                _ => UnitRole.Support,
            };
        }

        if (GetBool(section, "Refinery", false)) return UnitRole.Economy;
        if (power > 0 && weapon == WeaponClass.None) return UnitRole.Power;
        if (section.TryGet("SuperWeapon", out _)) return UnitRole.Superweapon;
        if (section.TryGet("Factory", out _)) return UnitRole.Production;
        if (weapon != WeaponClass.None) return UnitRole.Defense;
        return hasPrerequisites ? UnitRole.Tech : UnitRole.Support;
    }

    private static Dictionary<string, IniSection> ParseSections(TextReader reader)
    {
        Dictionary<string, IniSection> sections = new(StringComparer.OrdinalIgnoreCase);
        IniSection? current = null;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string trimmed = StripComment(line).Trim();
            if (trimmed.Length == 0) continue;

            if (trimmed[0] == '[' && trimmed[^1] == ']')
            {
                string name = trimmed[1..^1].Trim();
                if (!sections.TryGetValue(name, out current))
                {
                    current = new IniSection();
                    sections[name] = current;
                }
                continue;
            }

            int eq = trimmed.IndexOf('=');
            if (eq < 0 || current is null) continue;
            current.Set(trimmed[..eq].Trim(), trimmed[(eq + 1)..].Trim());
        }
        return sections;
    }

    private static Dictionary<string, IReadOnlyList<string>> ParseGenericPrerequisites(IReadOnlyDictionary<string, IniSection> sections)
    {
        Dictionary<string, IReadOnlyList<string>> result = new(StringComparer.OrdinalIgnoreCase);
        if (sections.TryGetValue("GenericPrerequisites", out IniSection? section))
        {
            foreach ((string key, string value) in section.EntriesInOrder) result[key] = SplitList(value);
        }
        return result;
    }

    private static string StripComment(string line)
    {
        int idx = line.IndexOf(';');
        return idx >= 0 ? line[..idx] : line;
    }

    private static IReadOnlyList<string> SplitList(string raw) =>
        raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static int GetInt(IniSection section, string key, int fallback) =>
        section.TryGet(key, out string value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : fallback;

    private static double GetDouble(IniSection section, string key, double fallback) =>
        section.TryGet(key, out string value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? result : fallback;

    private static bool GetBool(IniSection section, string key, bool fallback) =>
        section.TryGet(key, out string value) ? value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase) : fallback;

    private static string GetString(IniSection section, string key, string fallback) =>
        section.TryGet(key, out string value) && value.Length > 0 ? value : fallback;

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> BuildDefaultEffectiveness()
    {
        static IReadOnlyDictionary<string, double> Row(double none, double flak, double plate, double light, double medium, double heavy, double wood, double steel, double concrete, double special) =>
            new Dictionary<string, double>
            {
                [nameof(ArmorClass.None)] = none,
                [nameof(ArmorClass.Flak)] = flak,
                [nameof(ArmorClass.Plate)] = plate,
                [nameof(ArmorClass.Light)] = light,
                [nameof(ArmorClass.Medium)] = medium,
                [nameof(ArmorClass.Heavy)] = heavy,
                [nameof(ArmorClass.Wood)] = wood,
                [nameof(ArmorClass.Steel)] = steel,
                [nameof(ArmorClass.Concrete)] = concrete,
                [nameof(ArmorClass.Special)] = special,
            };

        return new Dictionary<string, IReadOnlyDictionary<string, double>>
        {
            [nameof(WeaponClass.AntiInfantry)] = Row(1.5, 1.0, 1.0, 0.5, 0.4, 0.25, 1.2, 0.3, 0.3, 0.8),
            [nameof(WeaponClass.AntiArmor)] = Row(0.5, 0.6, 0.8, 1.0, 1.1, 1.25, 0.6, 1.0, 0.5, 0.8),
            [nameof(WeaponClass.AntiStructure)] = Row(0.4, 0.4, 0.5, 0.6, 0.6, 0.7, 1.2, 0.8, 1.5, 0.6),
            [nameof(WeaponClass.AntiAir)] = Row(1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0),
            [nameof(WeaponClass.Artillery)] = Row(1.2, 1.0, 1.1, 1.0, 1.0, 1.0, 1.3, 0.9, 1.2, 0.9),
            [nameof(WeaponClass.General)] = Row(1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0),
        };
    }

    /// <summary>An INI section's key/value pairs, with last-write-wins semantics and stable insertion order for iteration.</summary>
    private sealed class IniSection
    {
        private readonly Dictionary<string, string> byKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> keyOrder = [];

        public void Set(string key, string value)
        {
            if (!byKey.ContainsKey(key)) keyOrder.Add(key);
            byKey[key] = value;
        }

        public bool TryGet(string key, out string value) => byKey.TryGetValue(key, out value!);

        public IEnumerable<string> ValuesInOrder => keyOrder.Select(k => byKey[k]);

        public IEnumerable<(string Key, string Value)> EntriesInOrder => keyOrder.Select(k => (k, byKey[k]));
    }
}
