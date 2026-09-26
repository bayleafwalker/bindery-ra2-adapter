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

    /// <summary>
    /// Parses <paramref name="ini"/> into a <see cref="RulesDocument"/> tagged with <paramref name="sourceSha256"/>.
    /// </summary>
    /// <param name="country">
    /// The bot's own country (an <c>Owner=</c> token such as <c>Americans</c>), or null when it is not known. It only
    /// decides which country-restricted types the bot can build (see <see cref="IsCountryRestricted"/>); every type
    /// is still imported so an enemy's units resolve in belief and effectiveness lookups.
    /// </param>
    public static RulesDocument Import(TextReader ini, string sourceSha256, string? country = null)
    {
        ArgumentNullException.ThrowIfNull(ini);
        ArgumentNullException.ThrowIfNull(sourceSha256);

        Dictionary<string, IniSection> sections = ParseSections(ini);
        Dictionary<string, IReadOnlyList<string>> genericPrereqs = ParseGenericPrerequisites(sections);

        List<UnitRule> units = [];
        Dictionary<string, QueueKind> factories = new(StringComparer.Ordinal);
        SortedDictionary<string, SortedSet<string>> unknownArmor = new(StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach ((string sectionName, EntityKind kind, QueueKind queue) in TypeLists)
        {
            if (!sections.TryGetValue(sectionName, out IniSection? list)) continue;
            foreach (string rawTypeId in list.ValuesInOrder)
            {
                // RA2 matches ids case-insensitively; RulesDatabase and belief compare ordinally, so every id and
                // prerequisite token is upper-cased once here. A type listed twice is imported once.
                string typeId = NormaliseId(rawTypeId);
                if (typeId.Length == 0 || !seen.Add(typeId)) continue;
                UnitRule? unit = BuildUnit(typeId, kind, queue, sections, genericPrereqs, country, unknownArmor);
                if (unit is null) continue;
                units.Add(unit);
                if (kind == EntityKind.Building && TryProducedQueue(sections[typeId], out QueueKind produced)) factories[typeId] = produced;
            }
        }

        units = units.Select(u => WithImplicitFactory(u, factories))
                     .Select(u => factories.TryGetValue(u.TypeId, out QueueKind produced) ? u with { Produces = Produced(produced) } : u)
                     .ToList();
        // Deterministic output order, independent of the INI's own layout.
        units.Sort(static (a, b) => string.CompareOrdinal(a.TypeId, b.TypeId));

        string provenance =
            "Imported from an operator-supplied rulesmd.ini via RulesmdImporter. Weapon class, unit role and build " +
            "time are heuristic estimates documented on RulesmdImporter, not a retail-accurate damage simulation.";
        if (unknownArmor.Count > 0)
        {
            provenance += " Unrecognised Armor values, read as None: " +
                string.Join("; ", unknownArmor.Select(static kv => $"{kv.Key} ({string.Join(", ", kv.Value)})")) + ".";
        }

        return new RulesDocument($"rulesmd-sha256:{sourceSha256}", provenance, units, DefaultEffectiveness);
    }

    /// <summary>
    /// Builds one <see cref="UnitRule"/> from its own INI section, or returns
    /// null when the type has no section (nothing to import), a negative or
    /// missing <c>TechLevel</c> (unbuildable: RA2's default is -1, so civilian and
    /// placeholder types without one are never buildable), or no <c>Owner=</c>
    /// token this importer recognises (see <see cref="ParseFactions"/>) — such a
    /// type cannot be placed on any faction's build tree and is skipped rather
    /// than emitted with an empty faction list. A type the bot's country cannot
    /// build (<see cref="IsCountryRestricted"/>) is kept with <c>TechLevel</c> -1.
    /// </summary>
    private static UnitRule? BuildUnit(
        string typeId,
        EntityKind kind,
        QueueKind defaultQueue,
        IReadOnlyDictionary<string, IniSection> sections,
        IReadOnlyDictionary<string, IReadOnlyList<string>> genericPrereqs,
        string? country,
        SortedDictionary<string, SortedSet<string>> unknownArmor)
    {
        if (!sections.TryGetValue(typeId, out IniSection? section)) return null;

        int techLevel = GetInt(section, "TechLevel", -1);
        if (techLevel < 0) return null;

        IReadOnlyList<Faction> factions = ParseFactions(section);
        if (factions.Count == 0) return null;
        if (IsCountryRestricted(section, country)) techLevel = -1;

        // Naval= marks a naval unit; on a structure (a shipyard) it says what the structure produces, not what it is.
        bool naval = kind != EntityKind.Building && GetBool(section, "Naval", false);
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
        ArmorClass armor = ParseArmor(typeId, GetString(section, "Armor", "none"), unknownArmor);
        double speed = GetDouble(section, "Speed", 0);
        int sight = GetInt(section, "Sight", 0);
        bool deployable = GetBool(section, "Deploys", false) || section.TryGet("DeploysInto", out _);

        IReadOnlyList<IReadOnlyList<string>> prerequisites = ParsePrerequisites(section, genericPrereqs);
        (WeaponClass weapon, double damage, double range, bool antiAirFlag) = ResolveWeapon(section, sections);

        UnitRole role = InferRole(resolvedKind, section, sections, weapon, power, prerequisites.Count > 0, sight, speed);
        if (resolvedKind == EntityKind.Building && role == UnitRole.Defense) queue = QueueKind.Defense;

        string name = GetString(section, "UIName", GetString(section, "Name", typeId));

        return new UnitRule(
            typeId, name, factions, resolvedKind, role, queue, cost, buildSeconds, power, prerequisites, techLevel,
            strength, armor, damage, weapon, range, speed, sight, antiAirFlag, deployable,
            Repairs: resolvedKind == EntityKind.Building && GetBool(section, "UnitRepair", false));
    }

    /// <summary>
    /// Whether the bot cannot build this type although its side can: it needs stolen tech
    /// (<c>RequiresStolen{Allied,Soviet,Third}Tech=yes</c>, which a skirmish bot never has), it names
    /// <c>RequiredHouses=</c> and the bot's country is unknown or not among them, it names the bot's country in
    /// <c>ForbiddenHouses=</c>, or the bot's country is known, belongs to one of the type's factions, and is
    /// missing from its <c>Owner=</c> list. Without this, a country unit (the German tank destroyer, the British
    /// sniper) would look buildable to every country of its side, and the game would reject every order for it.
    /// </summary>
    private static bool IsCountryRestricted(IniSection section, string? country)
    {
        if (GetBool(section, "RequiresStolenAlliedTech", false) ||
            GetBool(section, "RequiresStolenSovietTech", false) ||
            GetBool(section, "RequiresStolenThirdTech", false))
        {
            return true;
        }

        bool Lists(string key) =>
            country is not null && section.TryGet(key, out string raw) &&
            SplitList(raw).Contains(country, StringComparer.OrdinalIgnoreCase);

        if (section.TryGet("RequiredHouses", out string required) && SplitList(required).Count > 0 && !Lists("RequiredHouses")) return true;
        if (Lists("ForbiddenHouses")) return true;
        if (country is not null && CountryToFaction.TryGetValue(country, out Faction own) &&
            ParseFactions(section).Contains(own) && !Lists("Owner"))
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// Maps an <c>Armor=</c> value to <see cref="ArmorClass"/>. RA2 spells the special classes <c>special_1</c> and
    /// <c>special_2</c>; both become <see cref="ArmorClass.Special"/>. Any other unrecognised value falls back to
    /// <see cref="ArmorClass.None"/> and is recorded for the document's provenance, so a mod's custom armor is
    /// visible instead of silently read as infantry armor.
    /// </summary>
    private static ArmorClass ParseArmor(string typeId, string raw, SortedDictionary<string, SortedSet<string>> unknownArmor)
    {
        string value = raw.Trim();
        if (value.StartsWith("special", StringComparison.OrdinalIgnoreCase)) return ArmorClass.Special;
        if (!value.Any(char.IsDigit) && Enum.TryParse(value, true, out ArmorClass parsed) && Enum.IsDefined(parsed)) return parsed;
        if (!unknownArmor.TryGetValue(value, out SortedSet<string>? types)) unknownArmor[value] = types = new SortedSet<string>(StringComparer.Ordinal);
        types.Add(typeId);
        return ArmorClass.None;
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
    /// token is one AND-group; a generic token (<c>POWER</c>, <c>FACTORY</c>,
    /// <c>BARRACKS</c>, <c>RADAR</c>, <c>TECH</c>, <c>PROC</c>; see
    /// <see cref="ParseGenericPrerequisites"/>) expands to its OR-list of concrete
    /// building ids, while a plain building id becomes a singleton OR-group of itself.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<string>> ParsePrerequisites(
        IniSection section, IReadOnlyDictionary<string, IReadOnlyList<string>> genericPrereqs)
    {
        if (!section.TryGet("Prerequisite", out string raw) || raw.Length == 0) return [];
        List<IReadOnlyList<string>> groups = [];
        foreach (string token in SplitList(raw))
        {
            groups.Add(genericPrereqs.TryGetValue(token, out IReadOnlyList<string>? alternatives) ? alternatives : [NormaliseId(token)]);
        }
        return groups;
    }

    /// <summary>
    /// The queue a structure produces for, from its <c>Factory=</c> key: RA2's <c>BuildingType</c>,
    /// <c>InfantryType</c>, <c>UnitType</c> (naval when the structure itself is <c>Naval=yes</c>, a shipyard) and
    /// <c>AircraftType</c>, plus this importer's older short spellings (<c>Infantry</c>, <c>Vehicle</c>, ...).
    /// </summary>
    private static bool TryProducedQueue(IniSection building, out QueueKind queue)
    {
        queue = default;
        if (!building.TryGet("Factory", out string raw)) return false;
        string value = raw.Trim().ToUpperInvariant();
        bool naval = GetBool(building, "Naval", false);
        switch (value)
        {
            case "BUILDINGTYPE" or "BUILDING": queue = QueueKind.Building; return true;
            case "INFANTRYTYPE" or "INFANTRY": queue = QueueKind.Infantry; return true;
            case "UNITTYPE" or "UNIT" or "VEHICLE": queue = naval ? QueueKind.Naval : QueueKind.Vehicle; return true;
            case "NAVAL": queue = QueueKind.Naval; return true;
            case "AIRCRAFTTYPE" or "AIRCRAFT": queue = QueueKind.Aircraft; return true;
            default: return false;
        }
    }

    /// <summary>The queues a factory of <paramref name="kind"/> serves: a construction yard also builds defenses (their own sidebar tab).</summary>
    private static IReadOnlyList<QueueKind> Produced(QueueKind kind) =>
        kind == QueueKind.Building ? [QueueKind.Building, QueueKind.Defense] : [kind];

    /// <summary>
    /// Adds the producing factory RA2 requires implicitly: every structure needs a construction yard
    /// (<c>Factory=BuildingType</c>), every infantry type a barracks, and so on, although rulesmd.ini never writes it
    /// in <c>Prerequisite=</c>. The group lists every imported factory of that queue (any side's, since a captured
    /// one also produces), except the type itself, and is skipped when the type already requires one of them or the
    /// file has no factory for the queue. Without it, losing the construction yard would leave every structure
    /// buildable and PathTo from an empty base would omit the yard.
    /// </summary>
    private static UnitRule WithImplicitFactory(UnitRule unit, IReadOnlyDictionary<string, QueueKind> factories)
    {
        QueueKind needed = unit.Queue == QueueKind.Defense ? QueueKind.Building : unit.Queue;
        List<string> group = factories
            .Where(kv => kv.Value == needed && kv.Key != unit.TypeId)
            .Select(static kv => kv.Key)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToList();
        if (group.Count == 0) return unit;
        // Its own factory kind is not a prerequisite of a factory (a construction yard does not need a yard).
        if (factories.TryGetValue(unit.TypeId, out QueueKind produces) && produces == needed) return unit;
        if (unit.Prerequisites.Any(g => g.Count > 0 && g.All(group.Contains))) return unit;
        return unit with { Prerequisites = [group, .. unit.Prerequisites] };
    }

    /// <summary>
    /// Resolves a unit's weapons into (weapon class, estimated DPS, range, anti-air flag). Both <c>Primary=</c> and
    /// <c>Secondary=</c> are read. Whether a weapon hits air or ground lives on its <c>Projectile=</c> section
    /// (<c>AA=</c>, default no; <c>AG=</c>, default yes), as in rulesmd.ini. The class and damage come from the first
    /// weapon that hits ground, classified by <see cref="ClassifyByVerses"/>; a unit whose only weapons are
    /// anti-air is <see cref="WeaponClass.AntiAir"/> (never reclassified as artillery by range); the anti-air flag is
    /// set when any weapon can hit aircraft. DPS is estimated as <c>Damage * FramesPerSecond / ROF</c> (ROF is in
    /// frames, per RA2 convention); when ROF is missing or zero the raw damage value is used as a fallback.
    /// </summary>
    private static (WeaponClass WeaponClass, double Damage, double Range, bool AntiAir) ResolveWeapon(
        IniSection unitSection, IReadOnlyDictionary<string, IniSection> sections)
    {
        List<(IniSection Weapon, double Dps, double Range, bool Air, bool Ground)> weapons = [];
        foreach (string key in (string[])["Primary", "Secondary"])
        {
            if (!unitSection.TryGet(key, out string weaponName) || weaponName.Length == 0 || !sections.TryGetValue(weaponName, out IniSection? weapon)) continue;
            double damage = GetDouble(weapon, "Damage", 0);
            double rof = GetDouble(weapon, "ROF", 0);
            double dps = rof > 0 ? damage * GameTime.FramesPerSecond / rof : damage;
            IniSection? projectile = weapon.TryGet("Projectile", out string projectileName) && sections.TryGetValue(projectileName, out IniSection? p) ? p : null;
            bool air = (projectile is not null && GetBool(projectile, "AA", false)) || GetBool(weapon, "AA", false) || GetBool(unitSection, "AA", false);
            bool ground = projectile is null || GetBool(projectile, "AG", true);
            weapons.Add((weapon, dps, GetDouble(weapon, "Range", 0), air, ground));
        }

        if (weapons.Count == 0) return (WeaponClass.None, 0, 0, false);
        bool antiAir = weapons.Any(static w => w.Air);
        int groundIndex = weapons.FindIndex(static w => w.Ground);
        if (groundIndex < 0) return (WeaponClass.AntiAir, weapons[0].Dps, weapons[0].Range, true);
        (IniSection Weapon, double Dps, double Range, bool Air, bool Ground) g = weapons[groundIndex];

        if (g.Weapon.TryGet("Warhead", out string warheadName) &&
            sections.TryGetValue(warheadName, out IniSection? warhead) &&
            TryParseVerses(warhead, out double[] verses))
        {
            return (ClassifyByVerses(verses, g.Range), g.Dps, g.Range, antiAir);
        }

        return (g.Dps > 0 ? WeaponClass.General : WeaponClass.None, g.Dps, g.Range, antiAir);
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
    /// list, read in RA2's armor order: [none, flak, plate, light, medium, heavy,
    /// wood, steel, concrete, special_1, special_2]; a missing entry counts as
    /// 100%, the engine default. It averages the infantry-like entries (none,
    /// flak, plate), the vehicle entries (light, medium, heavy) and the structure
    /// entries (wood, steel, concrete), and assigns the class with the highest
    /// average; ties favour <see cref="WeaponClass.AntiArmor"/>, RA2's most
    /// common "generalist" class. This is a coarse bucketing, not a
    /// retail-accurate simulation. A weapon that is anti-armor by this test but
    /// has range 8 cells or more is reclassified as
    /// <see cref="WeaponClass.Artillery"/>, matching how RA2 treats long-range
    /// direct-fire vehicles (V3, artillery pieces) as a distinct siege role.
    /// </summary>
    private static WeaponClass ClassifyByVerses(double[] verses, double range)
    {
        double At(int i) => i < verses.Length ? verses[i] : 100;
        double infantry = (At(0) + At(1) + At(2)) / 3;
        double armor = (At(3) + At(4) + At(5)) / 3;
        double structure = (At(6) + At(7) + At(8)) / 3;

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
    /// <item>Non-building with a <c>DeploysInto=</c> target that is a construction yard
    /// (<c>Factory=BuildingType</c>), or with <c>Deploys=yes</c> and any <c>DeploysInto=</c>
    /// target -&gt; <see cref="UnitRole.Mcv"/>. Vanilla MCVs carry no <c>Deploys=</c> key.</item>
    /// <item>Non-building, <c>Engineer=yes</c> -&gt; <see cref="UnitRole.Engineer"/>.</item>
    /// <item>Building, <c>Refinery=yes</c> -&gt; <see cref="UnitRole.Economy"/>.</item>
    /// <item>Building, positive net <c>Power</c> and no weapon -&gt; <see cref="UnitRole.Power"/>.</item>
    /// <item>Building, <c>SuperWeapon=</c> present -&gt; <see cref="UnitRole.Superweapon"/>.</item>
    /// <item>Building, <c>Factory=</c> present -&gt; <see cref="UnitRole.Production"/>.</item>
    /// <item>Building with a resolved weapon and none of the above -&gt; <see cref="UnitRole.Defense"/>.</item>
    /// <item>Building with none of the above: <see cref="UnitRole.Tech"/> when it has
    /// prerequisites of its own (an upgrade/tech structure), else <see cref="UnitRole.Support"/>.</item>
    /// <item>Non-building, mapped from its resolved <see cref="WeaponClass"/>:
    /// AntiArmor/AntiInfantry/AntiAir/Artillery map to the role of the same name, and AntiStructure
    /// to <see cref="UnitRole.Artillery"/> (a siege unit) so the composition planner still counts it.</item>
    /// <item>Non-building with no weapon and sight at least double its speed
    /// (cheap, fast-seeing) -&gt; <see cref="UnitRole.Scout"/>; otherwise <see cref="UnitRole.Support"/>.</item>
    /// </list>
    /// </summary>
    private static UnitRole InferRole(
        EntityKind kind, IniSection section, IReadOnlyDictionary<string, IniSection> sections, WeaponClass weapon,
        int power, bool hasPrerequisites, int sight, double speed)
    {
        if (kind != EntityKind.Building)
        {
            if (GetBool(section, "Harvester", false)) return UnitRole.Harvester;
            if (section.TryGet("DeploysInto", out string target) && target.Length > 0 &&
                (GetBool(section, "Deploys", false) ||
                 (sections.TryGetValue(target, out IniSection? into) && TryProducedQueue(into, out QueueKind q) && q == QueueKind.Building)))
            {
                return UnitRole.Mcv;
            }
            if (GetBool(section, "Engineer", false)) return UnitRole.Engineer;
            return weapon switch
            {
                WeaponClass.AntiArmor => UnitRole.AntiArmor,
                WeaponClass.AntiInfantry => UnitRole.AntiInfantry,
                WeaponClass.AntiAir => UnitRole.AntiAir,
                WeaponClass.Artillery or WeaponClass.AntiStructure => UnitRole.Artillery,
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

    /// <summary>
    /// The generic prerequisite groups. Vanilla RA2/YR defines them under <c>[General]</c> as
    /// <c>PrerequisitePower=</c>, <c>PrerequisiteFactory=</c>, <c>PrerequisiteBarracks=</c>,
    /// <c>PrerequisiteRadar=</c>, <c>PrerequisiteTech=</c> and <c>PrerequisiteProc=</c> (plus YR's
    /// <c>PrerequisiteProcAlternate=</c>, which also satisfies <c>PROC</c>); the Ares extension's
    /// <c>[GenericPrerequisites]</c> section, when present, overrides a group of the same name. Without the
    /// <c>[General]</c> keys a vanilla file's <c>POWER</c> would be read as a building id nothing can own.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<string>> ParseGenericPrerequisites(IReadOnlyDictionary<string, IniSection> sections)
    {
        Dictionary<string, IReadOnlyList<string>> result = new(StringComparer.OrdinalIgnoreCase);
        if (sections.TryGetValue("General", out IniSection? general))
        {
            foreach (string group in (string[])["Power", "Factory", "Barracks", "Radar", "Tech", "Proc"])
            {
                List<string> members = [];
                foreach (string key in group == "Proc" ? ["PrerequisiteProc", "PrerequisiteProcAlternate"] : new[] { "Prerequisite" + group })
                {
                    if (general.TryGet(key, out string value)) members.AddRange(SplitList(value).Select(NormaliseId));
                }
                if (members.Count > 0) result[group.ToUpperInvariant()] = members.Distinct(StringComparer.Ordinal).ToList();
            }
        }
        if (sections.TryGetValue("GenericPrerequisites", out IniSection? section))
        {
            foreach ((string key, string value) in section.EntriesInOrder) result[key] = SplitList(value).Select(NormaliseId).ToList();
        }
        return result;
    }

    private static string NormaliseId(string raw) => raw.Trim().ToUpperInvariant();

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
