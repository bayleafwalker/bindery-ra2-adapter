// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Rules;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Rules;

public sealed class RulesDatabaseTests
{
    private const string TwoBuildingJson = """
    {
      "rulesetId": "test-ruleset",
      "provenance": "unit test fixture",
      "units": [
        { "typeId": "YARD", "name": "Yard", "factions": ["Allied"], "kind": "Building", "role": "Production", "queue": "Building",
          "cost": 0, "buildSeconds": 2, "power": 0, "prerequisites": [], "techLevel": 0, "strength": 1000,
          "armor": "Concrete", "damage": 0, "weapon": "None", "range": 0, "speed": 0, "sight": 5, "antiAir": false, "deployable": false },
        { "typeId": "POWR", "name": "Power Plant", "factions": ["Allied"], "kind": "Building", "role": "Power", "queue": "Building",
          "cost": 800, "buildSeconds": 48, "power": 100, "prerequisites": [["YARD"]], "techLevel": 0, "strength": 400,
          "armor": "Concrete", "damage": 0, "weapon": "None", "range": 0, "speed": 0, "sight": 5, "antiAir": false, "deployable": false },
        { "typeId": "WEAP", "name": "War Factory", "factions": ["Allied"], "kind": "Building", "role": "Production", "queue": "Building",
          "cost": 2000, "buildSeconds": 120, "power": -30, "prerequisites": [["YARD"], ["POWR"]], "techLevel": 0, "strength": 800,
          "armor": "Concrete", "damage": 0, "weapon": "None", "range": 0, "speed": 0, "sight": 6, "antiAir": false, "deployable": false },
        { "typeId": "NWEAP", "name": "Soviet War Factory", "factions": ["Soviet"], "kind": "Building", "role": "Production", "queue": "Building",
          "cost": 2000, "buildSeconds": 120, "power": -30, "prerequisites": [], "techLevel": 0, "strength": 800,
          "armor": "Concrete", "damage": 0, "weapon": "None", "range": 0, "speed": 0, "sight": 6, "antiAir": false, "deployable": false },
        { "typeId": "GATTK", "name": "Ground Attacker", "factions": ["Allied"], "kind": "Vehicle", "role": "AntiArmor", "queue": "Vehicle",
          "cost": 700, "buildSeconds": 42, "power": 0, "prerequisites": [["WEAP"]], "techLevel": 0, "strength": 400,
          "armor": "Heavy", "damage": 30, "weapon": "AntiArmor", "range": 5, "speed": 5, "sight": 6, "antiAir": false, "deployable": false },
        { "typeId": "FLAK", "name": "Flak Track", "factions": ["Allied"], "kind": "Vehicle", "role": "AntiAir", "queue": "Vehicle",
          "cost": 800, "buildSeconds": 48, "power": 0, "prerequisites": [["WEAP"]], "techLevel": 0, "strength": 400,
          "armor": "Light", "damage": 20, "weapon": "AntiAir", "range": 6, "speed": 6, "sight": 5, "antiAir": true, "deployable": false },
        { "typeId": "UNARMED", "name": "Harvester", "factions": ["Allied"], "kind": "Vehicle", "role": "Harvester", "queue": "Vehicle",
          "cost": 1400, "buildSeconds": 84, "power": 0, "prerequisites": [["WEAP"]], "techLevel": 0, "strength": 400,
          "armor": "Medium", "damage": 0, "weapon": "None", "range": 0, "speed": 6, "sight": 5, "antiAir": false, "deployable": false },
        { "typeId": "PLANE", "name": "Plane", "factions": ["Soviet"], "kind": "Aircraft", "role": "AntiArmor", "queue": "Aircraft",
          "cost": 2000, "buildSeconds": 120, "power": 0, "prerequisites": [], "techLevel": 0, "strength": 1000,
          "armor": "Heavy", "damage": 100, "weapon": "AntiStructure", "range": 3, "speed": 2, "sight": 6, "antiAir": false, "deployable": false },
        { "typeId": "TECHLOCKED", "name": "Unbuildable", "factions": ["Allied"], "kind": "Building", "role": "Support", "queue": "Building",
          "cost": 1, "buildSeconds": 2, "power": 0, "prerequisites": [], "techLevel": -1, "strength": 1,
          "armor": "None", "damage": 0, "weapon": "None", "range": 0, "speed": 0, "sight": 0, "antiAir": false, "deployable": false }
      ],
      "effectiveness": {
        "AntiArmor": { "Heavy": 1.25, "Light": 1.0 },
        "AntiAir": { "None": 1.0 }
      }
    }
    """;

    private static RulesDatabase Load() => RulesDatabase.LoadJson(TwoBuildingJson);

    [Fact]
    public void LoadJson_ExposesRulesetIdAndAllUnits()
    {
        RulesDatabase db = Load();
        Assert.Equal("test-ruleset", db.RulesetId);
        Assert.Equal(9, db.All.Count);
    }

    [Fact]
    public void TryGet_UnknownTypeId_ReturnsFalse()
    {
        RulesDatabase db = Load();
        Assert.False(db.TryGet("NOPE", out _));
    }

    [Fact]
    public void Get_UnknownTypeId_Throws()
    {
        RulesDatabase db = Load();
        Assert.Throws<KeyNotFoundException>(() => db.Get("NOPE"));
    }

    [Fact]
    public void CanBuild_WithAllPrerequisitesOwned_IsTrue()
    {
        RulesDatabase db = Load();
        Assert.True(db.CanBuild(Faction.Allied, new HashSet<string> { "YARD", "POWR" }, "WEAP"));
    }

    [Fact]
    public void CanBuild_MissingPrerequisite_IsFalse()
    {
        // Forces the failure case: War Factory needs both YARD and POWR; owning only YARD must not be enough.
        RulesDatabase db = Load();
        Assert.False(db.CanBuild(Faction.Allied, new HashSet<string> { "YARD" }, "WEAP"));
    }

    [Fact]
    public void CanBuild_WrongFaction_IsFalse()
    {
        // Forces the failure case: a Soviet-only building must not be buildable by Allied, even with prerequisites owned.
        RulesDatabase db = Load();
        Assert.False(db.CanBuild(Faction.Allied, new HashSet<string>(), "NWEAP"));
    }

    [Fact]
    public void CanBuild_NegativeTechLevel_IsFalse()
    {
        // Forces the failure case: TechLevel < 0 means unbuildable, regardless of faction or prerequisites.
        RulesDatabase db = Load();
        Assert.False(db.CanBuild(Faction.Allied, new HashSet<string>(), "TECHLOCKED"));
    }

    [Fact]
    public void PathTo_AlreadyBuildable_ReturnsEmptyList()
    {
        RulesDatabase db = Load();
        IReadOnlyList<string>? path = db.PathTo(Faction.Allied, new HashSet<string> { "YARD" }, "POWR");
        Assert.NotNull(path);
        Assert.Empty(path);
    }

    [Fact]
    public void PathTo_ReturnsOrderedMissingBuildings()
    {
        // PathTo returns what is needed BEFORE typeId becomes buildable (see the
        // contract's own doc comment); typeId itself is never in the list.
        RulesDatabase db = Load();
        IReadOnlyList<string>? path = db.PathTo(Faction.Allied, new HashSet<string>(), "WEAP");
        Assert.NotNull(path);
        Assert.Equal(new[] { "YARD", "POWR" }, path);
    }

    [Fact]
    public void PathTo_ResolvesTransitivePrerequisitesOfAUnit()
    {
        RulesDatabase db = Load();
        IReadOnlyList<string>? path = db.PathTo(Faction.Allied, new HashSet<string>(), "GATTK");
        Assert.NotNull(path);
        Assert.Equal(new[] { "YARD", "POWR", "WEAP" }, path);
    }

    [Fact]
    public void PathTo_UnreachableForFaction_ReturnsNull()
    {
        // Forces the failure case: Soviet cannot reach an Allied-only type.
        RulesDatabase db = Load();
        Assert.Null(db.PathTo(Faction.Soviet, new HashSet<string>(), "WEAP"));
    }

    [Fact]
    public void PathTo_UnknownTypeId_ReturnsNull()
    {
        RulesDatabase db = Load();
        Assert.Null(db.PathTo(Faction.Allied, new HashSet<string>(), "NOPE"));
    }

    [Fact]
    public void Effectiveness_UnarmedAttacker_IsZero()
    {
        // Forces the failure case: units with WeaponClass.None must never contribute damage.
        RulesDatabase db = Load();
        Assert.Equal(0, db.Effectiveness("UNARMED", "GATTK"));
    }

    [Fact]
    public void Effectiveness_AntiAirAttacker_VersusGroundTarget_IsZero()
    {
        // Forces the failure case: AntiAir weapons only hit Aircraft.
        RulesDatabase db = Load();
        Assert.Equal(0, db.Effectiveness("FLAK", "GATTK"));
    }

    [Fact]
    public void Effectiveness_GroundWeapon_VersusAircraftTarget_IsZeroWithoutAntiAirFlag()
    {
        // Forces the failure case: a ground-only weapon cannot touch an aircraft target.
        RulesDatabase db = Load();
        Assert.Equal(0, db.Effectiveness("GATTK", "PLANE"));
    }

    [Fact]
    public void Effectiveness_AntiAirAttacker_VersusAircraftTarget_UsesMatrix()
    {
        RulesDatabase db = Load();
        Assert.Equal(1.0, db.Effectiveness("FLAK", "PLANE"));
    }

    [Fact]
    public void Effectiveness_KnownWeaponArmorPair_UsesMatrixValue()
    {
        RulesDatabase db = Load();
        Assert.Equal(1.25, db.Effectiveness("GATTK", "GATTK"));
    }

    [Fact]
    public void Effectiveness_MissingMatrixEntry_DefaultsToNeutral()
    {
        RulesDatabase db = Load();
        Assert.Equal(1.0, db.Effectiveness("GATTK", "UNARMED"));
    }

    [Fact]
    public void LoadEmbeddedFixture_ExposesBinderySimApprox()
    {
        RulesDatabase db = RulesDatabase.LoadEmbeddedFixture();
        Assert.Equal("bindery-sim-approx", db.RulesetId);
        Assert.Contains("approximate", db.Provenance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not retail-accurate", db.Provenance, StringComparison.OrdinalIgnoreCase);
        Assert.True(db.All.Count > 30);
        Assert.True(db.TryGet("GAYARD", out _));
        Assert.True(db.TryGet("NACNST", out _));
    }

    [Fact]
    public void LoadEmbeddedFixture_AlliedWarFactoryPath_IsReachable()
    {
        RulesDatabase db = RulesDatabase.LoadEmbeddedFixture();
        IReadOnlyList<string>? path = db.PathTo(Faction.Allied, new HashSet<string>(), "GAWEAP");
        Assert.NotNull(path);
        Assert.Contains("GAYARD", path);
        Assert.Contains("GAPOWR", path);
        Assert.Contains("GAREFN", path);
        Assert.DoesNotContain("GAWEAP", path); // the target itself is never included
        // Build order must place prerequisites before the thing they unlock.
        List<string> ordered = path.ToList();
        Assert.True(ordered.IndexOf("GAYARD") < ordered.IndexOf("GAREFN"));
    }
}
