// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Playbooks;

public sealed class PlaybookLibraryTests
{
    private static readonly RulesDatabase Fixture = RulesDatabase.LoadEmbeddedFixture();

    public static IEnumerable<object[]> DefaultPlaybookIds() =>
        PlaybookLibrary.LoadDefault().All.Select(p => new object[] { p.Id });

    [Fact]
    public void LoadDefault_ReturnsTheTwelveNamedPlaybooks()
    {
        IReadOnlyList<Playbook> all = PlaybookLibrary.LoadDefault().All;
        string[] expected =
        [
            "allied-boom", "allied-grizzly-timing", "allied-ifv-mix", "allied-prism-turtle", "allied-harass",
            "soviet-rhino-rush", "soviet-flak-mix", "soviet-v3-siege", "soviet-apoc-tech", "soviet-turtle",
            "generic-defend", "generic-expand",
        ];
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), all.Select(p => p.Id));
    }

    [Fact]
    public void TryGet_UnknownId_ReturnsFalse()
    {
        Assert.False(PlaybookLibrary.LoadDefault().TryGet("does-not-exist", out _));
    }

    [Fact]
    public void For_FiltersByFaction()
    {
        PlaybookLibrary library = PlaybookLibrary.LoadDefault();
        Assert.All(library.For(Faction.Allied), p => Assert.Contains(Faction.Allied, p.Factions));
        Assert.DoesNotContain(library.For(Faction.Allied), p => p.Id == "soviet-rhino-rush");
    }

    [Theory]
    [MemberData(nameof(DefaultPlaybookIds))]
    public void BudgetShares_SumToOneWithinTolerance(string playbookId)
    {
        PlaybookLibrary.LoadDefault().TryGet(playbookId, out Playbook playbook);
        Assert.InRange(playbook.Budget.Sum, 0.99, 1.01);
    }

    [Theory]
    [MemberData(nameof(DefaultPlaybookIds))]
    public void Parameters_DefaultIsWithinDeclaredRange(string playbookId)
    {
        PlaybookLibrary.LoadDefault().TryGet(playbookId, out Playbook playbook);
        Assert.InRange(playbook.Parameters.Count, 1, 3);
        foreach (PlaybookParameter parameter in playbook.Parameters)
        {
            Assert.True(parameter.Min <= parameter.Default, $"{playbookId}.{parameter.Name}: min > default");
            Assert.True(parameter.Default <= parameter.Max, $"{playbookId}.{parameter.Name}: default > max");
        }
    }

    [Theory]
    [MemberData(nameof(DefaultPlaybookIds))]
    public void MinCommitSeconds_IsWithinThirtyToNinety(string playbookId)
    {
        PlaybookLibrary.LoadDefault().TryGet(playbookId, out Playbook playbook);
        Assert.InRange(playbook.MinCommitSeconds, 30, 90);
    }

    [Theory]
    [MemberData(nameof(DefaultPlaybookIds))]
    public void TechGoals_AreReachableForEveryDeclaredFaction(string playbookId)
    {
        PlaybookLibrary.LoadDefault().TryGet(playbookId, out Playbook playbook);
        foreach (string techGoal in playbook.TechGoals)
        {
            foreach (Faction faction in playbook.Factions)
            {
                IReadOnlyList<string>? path = Fixture.PathTo(faction, new HashSet<string>(), techGoal);
                Assert.True(path is not null, $"{playbookId}: tech goal '{techGoal}' is unreachable for {faction} in the fixture.");
            }
        }
    }

    [Fact]
    public void TechGoals_UnreachableGoal_WouldFailTheReachabilityCheck()
    {
        // Forces the failure case the test above guards against: an invented type id must not be "reachable".
        Assert.Null(Fixture.PathTo(Faction.Allied, new HashSet<string>(), "NOT-A-REAL-TYPE"));
    }

    [Fact]
    public void LoadJson_RoundTripsAPlaybook()
    {
        const string json = """
        {
          "playbooks": [
            {
              "id": "custom-test",
              "description": "operator authored",
              "factions": ["Allied"],
              "posture": "Defend",
              "budget": { "economy": 0.25, "army": 0.25, "tech": 0.25, "defense": 0.25 },
              "composition": [],
              "techGoals": [],
              "attackConditions": [],
              "abortTriggers": [],
              "parameters": [],
              "minCommitSeconds": 45
            }
          ]
        }
        """;
        PlaybookLibrary library = PlaybookLibrary.LoadJson(json);
        Assert.True(library.TryGet("custom-test", out Playbook playbook));
        Assert.Equal(StrategicPosture.Defend, playbook.Posture);
        Assert.Equal(45, playbook.MinCommitSeconds);
    }

    [Fact]
    public void Constructor_CombinesDefaultAndCustomPlaybooks()
    {
        Playbook custom = new(
            "custom-combined", "test", [Faction.Allied], StrategicPosture.Defend,
            new BudgetShares(0.25, 0.25, 0.25, 0.25), [], [], [], [], [], 45);
        PlaybookLibrary library = new(PlaybookLibrary.LoadDefault().All.Append(custom).ToList());
        Assert.Equal(13, library.All.Count);
        Assert.True(library.TryGet("custom-combined", out _));
        Assert.True(library.TryGet("allied-boom", out _));
    }

    [Fact]
    public void LoadJson_PlaybookMissingCollections_ThrowsInvalidDataNamingIt()
    {
        const string json = """{ "playbooks": [ { "id": "x", "description": "d", "posture": "Defend" } ] }""";
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => PlaybookLibrary.LoadJson(json));
        Assert.Contains("'x'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadJson_MissingPlaybooksArray_ThrowsInvalidData()
    {
        Assert.Throws<InvalidDataException>(() => PlaybookLibrary.LoadJson("{}"));
    }

    [Fact]
    public void LoadJson_ParameterDefaultOutsideRange_ThrowsInvalidData()
    {
        const string json = """
        { "playbooks": [ { "id": "p", "description": "d", "factions": ["Allied"], "posture": "Defend",
          "budget": { "economy": 0.25, "army": 0.25, "tech": 0.25, "defense": 0.25 },
          "composition": [], "techGoals": [], "attackConditions": [], "abortTriggers": [],
          "parameters": [ { "name": "k", "min": 1, "max": 2, "default": 5, "description": "d" } ], "minCommitSeconds": 45 } ] }
        """;
        Assert.Throws<InvalidDataException>(() => PlaybookLibrary.LoadJson(json));
    }

    [Fact]
    public void Constructor_DuplicateIds_Throws()
    {
        Playbook first = new(
            "dup", "first", [Faction.Allied], StrategicPosture.Defend,
            new BudgetShares(0.25, 0.25, 0.25, 0.25), [], [], [], [], [], 45);
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            new PlaybookLibrary([first, first with { Description = "second" }]));
        Assert.Contains("dup", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthoredPlaybooks_DeclareEveryParameterThePlannerReads()
    {
        // The planner reads these names from the intent; the validator drops any name the active playbook does not
        // declare, so a consumed name no playbook declares can never reach the planner.
        HashSet<string> declared = PlaybookLibrary.LoadAuthored().All
            .SelectMany(static p => p.Parameters).Select(static p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.All(Bindery.Ra2.Bot.Tuning.TuningKnobs.ConsumedPlaybookParameters, name => Assert.Contains(name, declared));
    }
}
