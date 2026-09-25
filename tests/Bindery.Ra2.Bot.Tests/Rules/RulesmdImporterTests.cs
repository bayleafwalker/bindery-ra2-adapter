// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Rules;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Rules;

public sealed class RulesmdImporterTests
{
    // A synthetic, deliberately tiny INI string in the RA2 rulesmd.ini shape.
    // Never real game files, per the package rules.
    private const string SyntheticIni = """
    ; comment line, must be stripped
    [GenericPrerequisites]
    POWER=GAPOWR,NAPOWR

    [BuildingTypes]
    0=GAYARD
    1=GAPOWR
    2=GAWEAP
    3=OLDTECH
    4=GAPILE
    5=GAREFN
    6=GAWEAT
    7=PBOX

    [InfantryTypes]
    0=E1
    1=ENGINEER

    [VehicleTypes]
    0=MTNK
    1=HARV
    2=AMCV

    [AircraftTypes]
    0=NOOWNER

    [GAYARD]
    Name=Allied Construction Yard
    Owner=Americans,British
    Strength=1000
    Armor=concrete
    TechLevel=0
    Cost=0

    [GAPOWR]
    Name=Allied Power Plant
    Owner=Americans
    Strength=400
    Armor=concrete
    TechLevel=0
    Cost=800   ; trailing comment
    Power=100
    Prerequisite=GAYARD

    [GAWEAP]
    Name=Allied War Factory
    Name=Allied War Factory (overridden, last wins)
    Owner=Americans
    Strength=800
    Armor=concrete
    TechLevel=0
    Cost=2000
    Power=-30
    Prerequisite=GAYARD,POWER

    ; a type with a negative tech level must be skipped entirely
    [OLDTECH]
    Owner=Americans
    TechLevel=-1
    Cost=100

    ; a type whose Owner maps to no known faction must be skipped
    [NOOWNER]
    Owner=Nod
    TechLevel=0
    Cost=500

    [E1]
    Name=Allied GI
    Owner=Americans
    Strength=125
    Armor=none
    TechLevel=0
    Cost=200
    Speed=3.5
    Sight=5
    Prerequisite=GAPILE
    Primary=Rifle

    [Rifle]
    Damage=12
    ROF=30
    Range=5
    Warhead=SmallArms

    [SmallArms]
    Verses=100%,80%,50%,25%,25%,50%,50%,50%

    [MTNK]
    Name=Allied Grizzly Tank
    Owner=Americans
    Strength=400
    Armor=heavy
    TechLevel=0
    Cost=700
    Speed=5
    Sight=6
    Prerequisite=GAWEAP
    Primary=105mmGun

    [105mmGun]
    Damage=30
    ROF=60
    Range=5
    Warhead=AP

    [AP]
    Verses=50%,60%,100%,125%,50%,80%,80%,80%

    [HARV]
    Name=Allied Chrono Miner
    Owner=Americans
    Strength=400
    TechLevel=0
    Cost=1400
    Speed=6
    Sight=5
    Prerequisite=GAWEAP
    Harvester=yes

    [ENGINEER]
    Name=Allied Engineer
    Owner=Americans
    Strength=50
    TechLevel=0
    Cost=500
    Speed=3
    Sight=4
    Prerequisite=GAYARD
    Engineer=yes

    [AMCV]
    Name=Allied MCV
    Owner=Americans
    Strength=600
    TechLevel=0
    Cost=3000
    Speed=4
    Sight=5
    Deploys=yes
    DeploysInto=GAYARD

    [GAPILE]
    Name=Allied Barracks
    Owner=Americans
    Strength=500
    TechLevel=0
    Cost=500
    Prerequisite=GAYARD
    Factory=Infantry

    [GAREFN]
    Name=Allied Ore Refinery
    Owner=Americans
    Strength=900
    TechLevel=0
    Cost=2000
    Prerequisite=GAYARD
    Refinery=yes

    [GAWEAT]
    Name=Allied Weather Control Device
    Owner=Americans
    Strength=1000
    TechLevel=0
    Cost=5000
    Prerequisite=GAYARD
    SuperWeapon=WeatherControl

    [PBOX]
    Name=Allied Pillbox
    Owner=Americans
    Strength=400
    Armor=concrete
    TechLevel=0
    Cost=400
    Prerequisite=GAYARD
    Primary=PillboxGun

    [PillboxGun]
    Damage=15
    ROF=20
    Range=5
    Warhead=SmallArms
    """;

    private static readonly RulesDocument Document = RulesmdImporter.Import(new StringReader(SyntheticIni), "deadbeef");

    [Fact]
    public void Import_ProducesHashQualifiedRulesetId()
    {
        Assert.Equal("rulesmd-sha256:deadbeef", Document.RulesetId);
    }

    [Fact]
    public void Import_SkipsTypesWithNegativeTechLevel()
    {
        // Forces the failure case: OLDTECH (TechLevel=-1) must not appear in the output at all.
        Assert.DoesNotContain(Document.Units, u => u.TypeId == "OLDTECH");
    }

    [Fact]
    public void Import_SkipsTypesWithUnrecognisedOwner()
    {
        // Forces the failure case: an Owner= token this importer doesn't map to a faction must drop the type.
        Assert.DoesNotContain(Document.Units, u => u.TypeId == "NOOWNER");
    }

    [Fact]
    public void Import_DuplicateKey_LastWriteWins()
    {
        UnitRule weap = Document.Units.Single(u => u.TypeId == "GAWEAP");
        Assert.Equal("Allied War Factory (overridden, last wins)", weap.Name);
    }

    [Fact]
    public void Import_StripsTrailingComments()
    {
        UnitRule power = Document.Units.Single(u => u.TypeId == "GAPOWR");
        Assert.Equal(800, power.Cost);
    }

    [Fact]
    public void Import_MapsOwnerCountriesToFaction()
    {
        UnitRule yard = Document.Units.Single(u => u.TypeId == "GAYARD");
        Assert.Equal([Faction.Allied], yard.Factions);
    }

    [Fact]
    public void Import_ExpandsGenericPrerequisiteToAlternativeGroup()
    {
        UnitRule weap = Document.Units.Single(u => u.TypeId == "GAWEAP");
        Assert.Equal(2, weap.Prerequisites.Count);
        Assert.Equal(["GAYARD"], weap.Prerequisites[0]);
        Assert.Equal(["GAPOWR", "NAPOWR"], weap.Prerequisites[1]);
    }

    [Fact]
    public void Import_DerivesBuildSecondsFromCost()
    {
        // Documented formula: max(2, cost / (1000 / 60)).
        UnitRule power = Document.Units.Single(u => u.TypeId == "GAPOWR");
        Assert.Equal(48, power.BuildSeconds, 3);

        UnitRule yard = Document.Units.Single(u => u.TypeId == "GAYARD");
        Assert.Equal(2, yard.BuildSeconds, 3); // clamped to the floor for a free (cost 0) type
    }

    [Fact]
    public void Import_EstimatesDpsFromDamageAndRof()
    {
        UnitRule gi = Document.Units.Single(u => u.TypeId == "E1");
        // Damage 12, ROF 30 frames, 15 fps => 12 * 15 / 30 = 6 DPS.
        Assert.Equal(6.0, gi.Damage, 3);
    }

    [Fact]
    public void Import_ClassifiesWeaponByVerses_AntiInfantry()
    {
        UnitRule gi = Document.Units.Single(u => u.TypeId == "E1");
        Assert.Equal(WeaponClass.AntiInfantry, gi.Weapon);
    }

    [Fact]
    public void Import_ClassifiesWeaponByVerses_AntiArmor()
    {
        UnitRule tank = Document.Units.Single(u => u.TypeId == "MTNK");
        Assert.Equal(WeaponClass.AntiArmor, tank.Weapon);
    }

    [Fact]
    public void Import_HarvesterKey_InfersHarvesterRole()
    {
        UnitRule harv = Document.Units.Single(u => u.TypeId == "HARV");
        Assert.Equal(UnitRole.Harvester, harv.Role);
    }

    [Fact]
    public void Import_EngineerKey_InfersEngineerRole()
    {
        UnitRule engineer = Document.Units.Single(u => u.TypeId == "ENGINEER");
        Assert.Equal(UnitRole.Engineer, engineer.Role);
    }

    [Fact]
    public void Import_DeploysIntoKey_InfersMcvRoleAndDeployable()
    {
        UnitRule mcv = Document.Units.Single(u => u.TypeId == "AMCV");
        Assert.Equal(UnitRole.Mcv, mcv.Role);
        Assert.True(mcv.Deployable);
    }

    [Fact]
    public void Import_RefineryKey_InfersEconomyRole()
    {
        UnitRule refinery = Document.Units.Single(u => u.TypeId == "GAREFN");
        Assert.Equal(UnitRole.Economy, refinery.Role);
    }

    [Fact]
    public void Import_FactoryKey_InfersProductionRole()
    {
        UnitRule barracks = Document.Units.Single(u => u.TypeId == "GAPILE");
        Assert.Equal(UnitRole.Production, barracks.Role);
    }

    [Fact]
    public void Import_SuperWeaponKey_InfersSuperweaponRole()
    {
        UnitRule weatherDevice = Document.Units.Single(u => u.TypeId == "GAWEAT");
        Assert.Equal(UnitRole.Superweapon, weatherDevice.Role);
    }

    [Fact]
    public void Import_PositivePowerNoWeapon_InfersPowerRole()
    {
        UnitRule power = Document.Units.Single(u => u.TypeId == "GAPOWR");
        Assert.Equal(UnitRole.Power, power.Role);
    }

    [Fact]
    public void Import_ArmedBuilding_InfersDefenseRoleAndQueue()
    {
        UnitRule pillbox = Document.Units.Single(u => u.TypeId == "PBOX");
        Assert.Equal(UnitRole.Defense, pillbox.Role);
        Assert.Equal(QueueKind.Defense, pillbox.Queue);
    }

    [Fact]
    public void Import_OutputIsDeterministicallySortedByTypeId()
    {
        List<string> ids = Document.Units.Select(u => u.TypeId).ToList();
        List<string> sorted = ids.OrderBy(id => id, StringComparer.Ordinal).ToList();
        Assert.Equal(sorted, ids);
    }

    [Fact]
    public void Import_ProducedDocument_LoadsIntoRulesDatabase()
    {
        System.Text.Json.JsonSerializerOptions options = BotJson.Options;
        string json = System.Text.Json.JsonSerializer.Serialize(Document, options);
        RulesDatabase database = RulesDatabase.LoadJson(json);
        Assert.True(database.CanBuild(Faction.Allied, new HashSet<string> { "GAYARD" }, "GAPOWR"));
    }
}
