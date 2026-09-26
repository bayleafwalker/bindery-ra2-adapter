// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Rules;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Rules;

/// <summary>
/// Build-order resolution after a failed alternative, and the loader's rejection of documents that would only fail
/// later (missing collections, null prerequisites, duplicate type ids).
/// </summary>
public sealed class RulesDatabaseLoadAndPathTests
{
    private static UnitRule Building(string id, int cost, Faction faction, params string[][] prerequisites) =>
        new(id, id, [faction], EntityKind.Building, UnitRole.Tech, QueueKind.Building, cost, 10, 0,
            prerequisites.Select(static g => (IReadOnlyList<string>)g).ToList(), 0, 100, ArmorClass.Concrete, 0,
            WeaponClass.None, 0, 0, 5, false, false);

    private static RulesDatabase Load(params UnitRule[] units)
    {
        RulesDocument document = new("t", "test", units, new Dictionary<string, IReadOnlyDictionary<string, double>>());
        return RulesDatabase.LoadJson(JsonSerializer.Serialize(document, BotJson.Options));
    }

    [Fact]
    public void PathTo_FailedAlternative_LeavesNoBuildingsInThePlan()
    {
        // GOAL needs CHEAP or DEAR. CHEAP (cheaper, tried first) needs POW then SOVONLY, which Allied cannot build,
        // so CHEAP fails after POW was already resolved. POW must not survive into the plan.
        RulesDatabase db = Load(
            Building("YARD", 0, Faction.Allied),
            Building("POW", 50, Faction.Allied, ["YARD"]),
            Building("SOVONLY", 50, Faction.Soviet),
            Building("CHEAP", 100, Faction.Allied, ["POW"], ["SOVONLY"]),
            Building("DEAR", 500, Faction.Allied, ["YARD"]),
            Building("GOAL", 100, Faction.Allied, ["CHEAP", "DEAR"]));

        IReadOnlyList<string>? path = db.PathTo(Faction.Allied, new HashSet<string> { "YARD" }, "GOAL");

        Assert.Equal(["DEAR"], path);
    }

    [Fact]
    public void LoadJson_MissingUnits_ThrowsInvalidData()
    {
        Assert.Throws<InvalidDataException>(() => RulesDatabase.LoadJson("""{ "rulesetId": "x" }"""));
    }

    [Fact]
    public void LoadJson_NullPrerequisites_ThrowsInvalidDataNamingTheType()
    {
        const string json = """
        { "rulesetId": "x", "provenance": "p", "effectiveness": {}, "units": [
          { "typeId": "BAD", "name": "Bad", "factions": ["Allied"], "kind": "Building", "role": "Tech", "queue": "Building",
            "cost": 1, "buildSeconds": 2, "power": 0, "prerequisites": null, "techLevel": 0, "strength": 1,
            "armor": "None", "damage": 0, "weapon": "None", "range": 0, "speed": 0, "sight": 0, "antiAir": false, "deployable": false } ] }
        """;
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => RulesDatabase.LoadJson(json));
        Assert.Contains("BAD", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadJson_DuplicateTypeId_ThrowsInvalidData()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            Load(Building("YARD", 0, Faction.Allied), Building("YARD", 10, Faction.Allied)));
        Assert.Contains("YARD", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedFixtures_PassValidation()
    {
        Assert.NotEmpty(RulesDatabase.LoadEmbeddedFixture().All);
        Assert.NotEmpty(RulesDatabase.LoadEmbeddedVariantFixture().All);
    }
}
