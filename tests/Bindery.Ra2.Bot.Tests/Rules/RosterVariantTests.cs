// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Arbitration;
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Sim;
using Bindery.Ra2.Bot.Sim.Opponents;
using Bindery.Ra2.Bot.Strategy;
using Bindery.Ra2.Bot.Tests.Arbitration;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Rules;

/// <summary>
/// The proposal's "adaptation across mods and changing unit rosters": under the committed roster variant
/// (<c>bindery-sim-variant-roster</c>: a unit renamed, two removed, four re-costed, one added) the authored playbooks
/// fail validation cleanly as written, and adapt through rule facts (same role, nearest cost, reachable) when loaded
/// against the roster; the arena plays whole matches on it.
/// </summary>
public sealed class RosterVariantTests : IDisposable
{
    private static readonly RulesDatabase Fixture = RulesDatabase.LoadEmbeddedFixture();
    private static readonly RulesDatabase Variant = RulesDatabase.LoadEmbeddedVariantFixture();
    private readonly string dir = Path.Combine(Path.GetTempPath(), $"bindery-roster-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void The_variant_renames_removes_recosts_and_adds_units()
    {
        Assert.Equal("bindery-sim-variant-roster", Variant.RulesetId);
        Assert.False(Variant.TryGet("MTNK", out _));
        Assert.True(Variant.TryGet("GTNK", out UnitRule guardian));
        Assert.Equal(Fixture.Get("MTNK").Role, guardian.Role);
        Assert.False(Variant.TryGet("HTK", out _));
        Assert.False(Variant.TryGet("V3", out _));
        Assert.Equal(200, Variant.Get("E2").Cost);
        Assert.NotEqual(Fixture.Get("HTNK").Cost, Variant.Get("HTNK").Cost);
        Assert.True(Variant.TryGet("TNKD", out _));
    }

    [Fact]
    public void Authored_playbooks_fail_validation_cleanly_on_the_variant()
    {
        StrategicFeatures features = Fx.Features(60);
        Playbook grizzly = PlaybookLibrary.LoadDefault().All.Single(static p => p.Id == "allied-grizzly-timing");
        StrategicIntent intent = IntentComposer.Compose(grizzly, features, "i", IntentSource.Selector, 0.6, "test");
        ValidationContext context = Fx.Context(features) with { Rules = Variant, Playbooks = PlaybookLibrary.LoadDefault() };

        ValidationResult result = new IntentValidator().Validate(intent, context);

        Assert.False(result.Accepted);
        Assert.Contains(result.Issues, static i => i.Code == ValidationCodes.TypeUnknown && i.Message.Contains("MTNK", StringComparison.Ordinal));
    }

    [Fact]
    public void Playbooks_adapt_through_rule_facts_or_are_dropped_with_a_reason()
    {
        RosterAdaptation adapted = PlaybookRosterAdapter.Adapt(PlaybookLibrary.LoadDefault().All, Variant, Fixture);

        Assert.True(adapted.Library.TryGet("allied-grizzly-timing", out Playbook grizzly));
        Assert.Equal(["GAWEAP", "GTNK"], grizzly.TechGoals);
        Assert.True(adapted.Library.TryGet("soviet-flak-mix", out Playbook flak));
        Assert.Equal(["NAWEAP", "E4"], flak.TechGoals); // the only Soviet anti-air unit left is infantry
        Assert.False(adapted.Library.TryGet("soviet-v3-siege", out _)); // no artillery left in the roster
        Assert.Contains(adapted.Changes, static c => c.PlaybookId == "soviet-v3-siege" && c.Replacement is null && c.Reason.Contains("V3", StringComparison.Ordinal));
        Assert.Contains(adapted.Changes, static c => c.PlaybookId == "allied-grizzly-timing" && c.TechGoal == "MTNK" && c.Replacement == "GTNK");
        foreach (Playbook p in adapted.Library.All)
        {
            foreach (string goal in p.TechGoals)
            {
                Assert.True(Variant.TryGet(goal, out _), $"{p.Id}: {goal}");
                Assert.All(p.Factions, f => Assert.NotNull(Variant.PathTo(f, new HashSet<string>(), goal)));
            }
        }
    }

    [Fact]
    public void Adapting_to_the_authoring_roster_changes_nothing()
    {
        PlaybookLibrary authored = PlaybookLibrary.LoadDefault();

        RosterAdaptation same = PlaybookRosterAdapter.Adapt(authored.All, Fixture, Fixture);

        Assert.Empty(same.Changes);
        Assert.Equal(authored.All, same.Library.All);
    }

    [Fact]
    public void Adapted_intents_pass_validation_on_the_variant()
    {
        StrategicFeatures features = Fx.Features(60);
        RosterAdaptation adapted = PlaybookRosterAdapter.Adapt(PlaybookLibrary.LoadDefault().All, Variant, Fixture);
        StrategicIntent intent = IntentComposer.Compose(adapted.Library.All.Single(static p => p.Id == "allied-grizzly-timing"), features, "i", IntentSource.Selector, 0.6, "test");
        ValidationContext context = Fx.Context(features) with { Rules = Variant, Playbooks = adapted.Library };

        ValidationResult result = new IntentValidator().Validate(intent, context);

        Assert.DoesNotContain(result.Issues, static i => i.Code is ValidationCodes.TypeUnknown or ValidationCodes.TechUnreachable);
    }

    [Fact]
    public void Scripted_opponents_substitute_missing_units_by_role()
    {
        Assert.Equal("GTNK", RosterSubstitution.Resolve("MTNK", [Faction.Allied], Variant, Fixture));
        Assert.Equal("MTNK", RosterSubstitution.Resolve("MTNK", [Faction.Allied], Fixture, Fixture));
        Assert.Null(RosterSubstitution.Resolve("V3", [Faction.Soviet], Variant, Fixture));
        SimMap map = SimMaps.Training[0];
        foreach (string style in OpponentProfiles.Styles.Concat(OpponentProfiles.HeldOutStyles))
        {
            foreach (Faction faction in new[] { Faction.Allied, Faction.Soviet })
            {
                _ = new ScriptedSkirmishAi(Variant, new PlayerId(1), faction, map.Map, style, OpponentDifficulty.Hard, 1);
            }
        }
    }

    [Fact]
    public void The_arena_runs_on_a_rules_file_reports_the_adaptation_and_replays()
    {
        Directory.CreateDirectory(dir);
        string rules = Path.Combine(dir, "variant.json");
        File.WriteAllText(rules, RulesDatabase.EmbeddedFixtureJson(RulesDatabase.VariantFixtureFile));
        string output = Path.Combine(dir, "out");

        Assert.Equal(0, Program.Main(["run", "--arms", "selector,llm", "--maps", "training", "--opponents", "ai-rush,live-rush", "--seeds", "2",
            "--benchmark", "contested", "--max-seconds", "300", "--llm-fake", "--rules", rules, "--out", output]));

        using JsonDocument results = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "results.json")));
        Assert.Equal(2 * 2 * 3 * 2, results.RootElement.GetArrayLength());
        string report = File.ReadAllText(Path.Combine(output, "report.md"));
        Assert.Contains("`bindery-sim-variant-roster`", report, StringComparison.Ordinal);
        Assert.Contains("## Roster adaptation", report, StringComparison.Ordinal);
        Assert.Contains("allied-grizzly-timing: MTNK → GTNK", report, StringComparison.Ordinal);
        Assert.Contains("soviet-v3-siege: dropped", report, StringComparison.Ordinal);
        string log = Directory.GetFiles(Path.Combine(output, "decisions"), "llm_*.ndjson").OrderBy(static f => f, StringComparer.Ordinal).First();
        Assert.True(Program.ReplayMatch(log).Equal);
    }
}
