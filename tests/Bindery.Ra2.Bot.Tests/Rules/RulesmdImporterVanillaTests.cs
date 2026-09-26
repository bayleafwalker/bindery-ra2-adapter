// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Rules;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Rules;

/// <summary>
/// The importer against the layout a vanilla RA2/YR <c>rulesmd.ini</c> actually uses: generic prerequisites under
/// <c>[General]</c>, eleven-entry <c>Verses=</c> lists, anti-air flags on the projectile, MCVs without
/// <c>Deploys=</c>, factories implied rather than listed, country-only units, and lowercase ids. A synthetic INI,
/// never real game files.
/// </summary>
public sealed class RulesmdImporterVanillaTests
{
    private const string VanillaIni = """
    [General]
    PrerequisitePower=GAPOWR,NAPOWR
    PrerequisiteFactory=GAWEAP,NAWEAP
    PrerequisiteBarracks=GAPILE,NAHAND
    PrerequisiteProc=GAREFN,NAREFN

    [BuildingTypes]
    0=GACNST
    1=GAPOWR
    2=GAPILE
    3=GAWEAP
    4=GAREFN
    5=GAYARD
    6=gadept
    7=NACNST
    8=NAPOWR
    9=NAWEAP

    [InfantryTypes]
    0=E1
    1=SNIPE
    2=CLEG
    3=E1

    [VehicleTypes]
    0=MTNK
    1=HTK
    2=FV
    3=AMCV
    4=TNKD
    5=DEST
    6=ROBO
    7=ODDARMOR
    8=CIVCAR

    [GACNST]
    Owner=British,French,Germans,Americans,Alliance
    TechLevel=0
    Cost=3000
    Factory=BuildingType

    [NACNST]
    Owner=Russians,Confederation,Africans,Arabs
    TechLevel=0
    Cost=3000
    Factory=BuildingType

    [GAPOWR]
    Owner=British,French,Germans,Americans,Alliance
    TechLevel=1
    Cost=800
    Power=200

    [NAPOWR]
    Owner=Russians,Confederation,Africans,Arabs
    TechLevel=1
    Cost=600
    Power=150

    [GAPILE]
    Owner=British,French,Germans,Americans,Alliance
    TechLevel=1
    Cost=500
    Prerequisite=POWER
    Factory=InfantryType

    [GAREFN]
    Owner=British,French,Germans,Americans,Alliance
    TechLevel=1
    Cost=2000
    Prerequisite=POWER
    Refinery=yes

    [GAWEAP]
    Owner=British,French,Germans,Americans,Alliance
    TechLevel=2
    Cost=2000
    Prerequisite=PROC,POWER
    Factory=UnitType

    [NAWEAP]
    Owner=Russians,Confederation,Africans,Arabs
    TechLevel=2
    Cost=2000
    Prerequisite=POWER
    Factory=UnitType

    [GAYARD]
    Owner=British,French,Germans,Americans,Alliance
    TechLevel=3
    Cost=1000
    Prerequisite=POWER
    Factory=UnitType
    Naval=yes
    WaterBound=yes

    [GADEPT]
    Owner=British,French,Germans,Americans,Alliance
    TechLevel=3
    Cost=800
    Prerequisite=factory,gacnst
    UnitRepair=yes

    [E1]
    Owner=British,French,Germans,Americans,Alliance
    TechLevel=1
    Cost=200
    Prerequisite=BARRACKS
    Armor=flak

    [SNIPE]
    Owner=British
    RequiredHouses=British
    TechLevel=5
    Cost=600
    Prerequisite=BARRACKS

    [CLEG]
    Owner=British,French,Germans,Americans,Alliance
    RequiresStolenSovietTech=yes
    TechLevel=10
    Cost=1000
    Prerequisite=BARRACKS

    [MTNK]
    Owner=British,French,Germans,Americans,Alliance
    TechLevel=2
    Cost=700
    Armor=heavy
    Prerequisite=FACTORY
    Primary=105mm

    [105mm]
    Damage=65
    ROF=65
    Range=5.75
    Projectile=Cannon
    Warhead=AP

    [Cannon]
    AA=no
    AG=yes

    [AP]
    Verses=25%,25%,25%,90%,75%,75%,70%,40%,40%,100%,100%

    [HTK]
    Owner=Russians,Confederation,Africans,Arabs
    TechLevel=2
    Cost=500
    Armor=light
    Prerequisite=FACTORY
    Primary=FlakTrackAAGun

    [FlakTrackAAGun]
    Damage=50
    ROF=40
    Range=8
    Projectile=FlakProj
    Warhead=FlakWH

    [FlakProj]
    AA=yes
    AG=no

    [FlakWH]
    Verses=100%,100%,100%,100%,100%,100%,100%,100%,100%,100%,100%

    [FV]
    Owner=British,French,Germans,Americans,Alliance
    TechLevel=2
    Cost=600
    Armor=light
    Prerequisite=FACTORY
    Primary=105mm
    Secondary=FlakTrackAAGun

    [AMCV]
    Owner=British,French,Germans,Americans,Alliance
    TechLevel=2
    Cost=3000
    Prerequisite=FACTORY
    DeploysInto=GACNST

    [TNKD]
    Owner=Germans
    RequiredHouses=Germans
    TechLevel=5
    Cost=900
    Prerequisite=FACTORY

    [DEST]
    Owner=British,French,Germans,Americans,Alliance
    TechLevel=4
    Cost=1000
    Naval=yes
    Prerequisite=GAYARD

    [ROBO]
    Owner=British,French,Germans,Americans,Alliance
    TechLevel=6
    Cost=900
    Armor=special_2
    Prerequisite=FACTORY

    [ODDARMOR]
    Owner=British,French,Germans,Americans,Alliance
    TechLevel=6
    Cost=900
    Armor=bogus
    Prerequisite=FACTORY

    [CIVCAR]
    Owner=British,Russians
    Cost=0
    """;

    private static RulesDocument Import(string? country = null) =>
        RulesmdImporter.Import(new StringReader(VanillaIni), "cafe", country);

    private static readonly RulesDocument Document = Import();

    private static RulesDatabase Database(RulesDocument document) =>
        RulesDatabase.LoadJson(JsonSerializer.Serialize(document, BotJson.Options));

    private static UnitRule Unit(string id, RulesDocument? document = null) =>
        (document ?? Document).Units.Single(u => u.TypeId == id);

    [Fact]
    public void GeneralSectionPrerequisites_ExpandToTheirBuildings()
    {
        Assert.Contains(Unit("GAPILE").Prerequisites, g => g.SequenceEqual(["GAPOWR", "NAPOWR"]));
        Assert.Contains(Unit("MTNK").Prerequisites, g => g.SequenceEqual(["GAWEAP", "NAWEAP"]));
        RulesDatabase db = Database(Document);
        Assert.True(db.CanBuild(Faction.Allied, new HashSet<string> { "GACNST", "GAPOWR" }, "GAPILE"));
        Assert.NotNull(db.PathTo(Faction.Allied, new HashSet<string> { "GACNST" }, "MTNK"));
    }

    [Fact]
    public void AresGenericPrerequisites_TakePrecedenceOverGeneral()
    {
        string ini = "[GenericPrerequisites]\nPOWER=XPOWR\n" + VanillaIni;
        RulesDocument document = RulesmdImporter.Import(new StringReader(ini), "cafe");
        Assert.Contains(Unit("GAPILE", document).Prerequisites, g => g.SequenceEqual(["XPOWR"]));
    }

    [Fact]
    public void Verses_UseTheRa2ArmorOrder_SoATankIsAntiArmor()
    {
        UnitRule tank = Unit("MTNK");
        Assert.Equal(WeaponClass.AntiArmor, tank.Weapon);
        Assert.Equal(UnitRole.AntiArmor, tank.Role);
    }

    [Fact]
    public void ProjectileAntiAirFlag_MakesAnAntiAirUnit_NotArtillery()
    {
        UnitRule flak = Unit("HTK");
        Assert.Equal(WeaponClass.AntiAir, flak.Weapon);
        Assert.Equal(UnitRole.AntiAir, flak.Role);
        Assert.True(flak.AntiAir);
    }

    [Fact]
    public void SecondaryAntiAirWeapon_SetsTheDualPurposeFlag()
    {
        UnitRule ifv = Unit("FV");
        Assert.Equal(WeaponClass.AntiArmor, ifv.Weapon);
        Assert.True(ifv.AntiAir);
    }

    [Fact]
    public void VehicleDeployingIntoAConstructionYard_IsAnMcv()
    {
        UnitRule mcv = Unit("AMCV");
        Assert.Equal(UnitRole.Mcv, mcv.Role);
        Assert.True(mcv.Deployable);
    }

    [Fact]
    public void LowercaseIdsAndTokens_AreNormalised()
    {
        UnitRule depot = Unit("GADEPT");
        Assert.Contains(depot.Prerequisites, g => g.SequenceEqual(["GACNST"]));
        RulesDatabase db = Database(Document);
        Assert.True(db.CanBuild(Faction.Allied, new HashSet<string> { "GACNST", "GAWEAP" }, "GADEPT"));
    }

    [Fact]
    public void A_service_depot_is_marked_as_repairing_whatever_its_role()
    {
        UnitRule depot = Unit("GADEPT");
        Assert.True(depot.Repairs);
        Assert.Equal(UnitRole.Tech, depot.Role);
        Assert.DoesNotContain(Document.Units, u => u.Repairs && u.TypeId != "GADEPT");
    }

    [Fact]
    public void Factories_declare_the_queues_they_produce_for()
    {
        Assert.Equal([QueueKind.Building, QueueKind.Defense], Unit("GACNST").Produces!);
        Assert.Equal([QueueKind.Infantry], Unit("GAPILE").Produces!);
        Assert.Equal([QueueKind.Vehicle], Unit("GAWEAP").Produces!);
        Assert.Equal([QueueKind.Naval], Unit("GAYARD").Produces!);
        Assert.Null(Unit("GAPOWR").Produces);
    }

    [Fact]
    public void MissingTechLevel_IsUnbuildable()
    {
        Assert.DoesNotContain(Document.Units, u => u.TypeId == "CIVCAR");
    }

    [Fact]
    public void NavalBuilding_StaysABuildingAndProducesNavalUnits()
    {
        UnitRule yard = Unit("GAYARD");
        Assert.Equal(EntityKind.Building, yard.Kind);
        Assert.Equal(QueueKind.Building, yard.Queue);
        Assert.Equal(UnitRole.Production, yard.Role);

        UnitRule destroyer = Unit("DEST");
        Assert.Equal(EntityKind.Naval, destroyer.Kind);
        Assert.NotNull(Database(Document).PathTo(Faction.Allied, new HashSet<string> { "GACNST" }, "DEST"));
    }

    [Fact]
    public void DuplicateListEntries_AreImportedOnce()
    {
        Assert.Single(Document.Units, u => u.TypeId == "E1");
    }

    [Fact]
    public void CountryOnlyAndStolenTechUnits_AreUnbuildableWithoutAMatchingCountry()
    {
        RulesDatabase db = Database(Document);
        HashSet<string> owned = ["GACNST", "GAPOWR", "GAPILE", "GAREFN", "GAWEAP"];
        Assert.False(db.CanBuild(Faction.Allied, owned, "TNKD"));
        Assert.False(db.CanBuild(Faction.Allied, owned, "SNIPE"));
        Assert.False(db.CanBuild(Faction.Allied, owned, "CLEG"));
        Assert.Null(db.PathTo(Faction.Allied, new HashSet<string>(), "CLEG"));
        // Still known, so an enemy's tank destroyer resolves in belief and effectiveness lookups.
        Assert.True(db.TryGet("TNKD", out _));

        RulesDatabase german = Database(Import("Germans"));
        Assert.True(german.CanBuild(Faction.Allied, owned, "TNKD"));
        Assert.False(german.CanBuild(Faction.Allied, owned, "SNIPE"));
        Assert.False(german.CanBuild(Faction.Allied, owned, "CLEG"));
    }

    [Fact]
    public void EveryBuilding_RequiresAConstructionYard()
    {
        RulesDatabase db = Database(Document);
        Assert.False(db.CanBuild(Faction.Allied, new HashSet<string>(), "GAPOWR"));
        Assert.False(db.CanBuild(Faction.Allied, new HashSet<string> { "GAPOWR", "GAREFN" }, "GAWEAP"));
        Assert.True(db.CanBuild(Faction.Allied, new HashSet<string> { "GACNST", "GAPOWR", "GAREFN" }, "GAWEAP"));
        Assert.Equal(["GACNST", "GAPOWR"], db.PathTo(Faction.Allied, new HashSet<string>(), "GAPILE"));
        // A construction yard is not its own prerequisite.
        Assert.True(db.CanBuild(Faction.Allied, new HashSet<string>(), "GACNST"));
    }

    [Fact]
    public void Infantry_RequiresABarracks()
    {
        Assert.Contains(Unit("E1").Prerequisites, g => g.Contains("GAPILE"));
    }

    [Fact]
    public void SpecialArmor_MapsToSpecial_AndUnknownArmorIsReported()
    {
        Assert.Equal(ArmorClass.Special, Unit("ROBO").Armor);
        Assert.Equal(ArmorClass.Flak, Unit("E1").Armor);
        Assert.Contains("bogus", Document.Provenance, StringComparison.Ordinal);
        Assert.Contains("ODDARMOR", Document.Provenance, StringComparison.Ordinal);
    }
}
