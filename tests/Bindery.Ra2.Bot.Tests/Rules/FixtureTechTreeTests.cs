// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Rules;

/// <summary>
/// The committed fixtures must encode RA2's implicit factories (a construction yard for every structure, a barracks
/// for every infantry type), and every playbook that raids or pushes must field a unit that can hurt a harvester.
/// </summary>
public sealed class FixtureTechTreeTests
{
    public static IEnumerable<object[]> Fixtures() =>
        [[RulesDatabase.FixtureFile], [RulesDatabase.VariantFixtureFile]];

    private static RulesDatabase Load(string file) => RulesDatabase.LoadJson(RulesDatabase.EmbeddedFixtureJson(file));

    private static readonly string[] ConstructionYards = ["GAYARD", "NACNST"];
    private static readonly string[] Barracks = ["GAPILE", "NAHAND"];

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EveryStructure_RequiresAConstructionYard(string file)
    {
        foreach (UnitRule unit in Load(file).All.Where(static u => u.Kind == EntityKind.Building && !ConstructionYards.Contains(u.TypeId)))
        {
            Assert.True(unit.Prerequisites.Any(static g => g.All(ConstructionYards.Contains)), $"{file}: {unit.TypeId} lacks a construction yard");
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EveryInfantryType_RequiresABarracks(string file)
    {
        foreach (UnitRule unit in Load(file).All.Where(static u => u.Kind == EntityKind.Infantry))
        {
            Assert.True(unit.Prerequisites.Any(static g => g.All(Barracks.Contains)), $"{file}: {unit.TypeId} lacks a barracks");
        }
    }

    [Fact]
    public void WithoutAConstructionYard_NoStructureIsBuildable()
    {
        RulesDatabase db = RulesDatabase.LoadEmbeddedFixture();
        HashSet<string> owned = ["GAPOWR", "GAREFN", "GAWEAP", "GAAIRC", "GATECH", "GAPILE"];
        Assert.All(new[] { "GATECH", "GAWEAT", "GAPRIS", "GAPATR", "PBOX", "GADEPT", "GAAIRC" },
            id => Assert.False(db.CanBuild(Faction.Allied, owned, id), id));
    }

    [Fact]
    public void HarassAndPressurePlaybooks_FieldAUnitThatCanHurtAHarvester()
    {
        RulesDatabase db = RulesDatabase.LoadEmbeddedFixture();
        foreach (Playbook playbook in PlaybookLibrary.LoadAuthored().All.Where(static p => p.Posture is StrategicPosture.Harass or StrategicPosture.Pressure))
        {
            foreach (Faction faction in playbook.Factions)
            {
                string harvester = faction == Faction.Allied ? "HARV" : "CMIN";
                HashSet<UnitRole> roles = playbook.Composition.Where(static c => c.MaxShare > 0).Select(static c => c.Role).ToHashSet();
                bool canHurt = db.All.Any(u => u.Factions.Contains(faction) && roles.Contains(u.Role) && u.Kind != EntityKind.Building
                    && db.Effectiveness(u.TypeId, harvester) > 0);
                Assert.True(canHurt, $"{playbook.Id} ({faction}) fields nothing that damages {harvester}");
            }
        }
    }

    [Fact]
    public void Rocketeers_HitGroundAndAir()
    {
        RulesDatabase db = RulesDatabase.LoadEmbeddedFixture();
        Assert.True(db.Effectiveness("E3", "HARV") > 0);
        Assert.True(db.Effectiveness("E3", "KIROV") > 0);
        Assert.True(db.Effectiveness("HTK", "E1") > 0);
        Assert.True(db.Effectiveness("HTK", "HARR") > 0);
    }
}
