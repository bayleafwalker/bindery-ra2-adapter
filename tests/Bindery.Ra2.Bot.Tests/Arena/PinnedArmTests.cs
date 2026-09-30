// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Playbooks;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary><c>--arms pinned:&lt;playbookId&gt;</c>: the deterministic runner of a compiled playbook.</summary>
[Collection("ConsoleRedirect")]
public sealed class PinnedArmTests : IDisposable
{
    private readonly string root = InduceFixtures.NewRoot();

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    /// <summary>Plays one short match of the arm on the first training map and returns the decision records of its log.</summary>
    private List<JsonElement> Play(string arm, string seed, params string[] extra)
    {
        string dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Assert.Equal(0, Program.Main(["run", "--arms", arm, "--maps", "training", "--opponents", "ai-rush", "--seed-list", seed, "--max-seconds", "120", "--out", dir, .. extra]));
        List<JsonElement> records = [];
        foreach (string log in Directory.GetFiles(Path.Combine(dir, "decisions"), "*.ndjson"))
        {
            records.AddRange(File.ReadAllLines(log).Where(static l => l.Length > 0).Select(static l => JsonDocument.Parse(l).RootElement.Clone()));
        }
        return records;
    }

    private static List<JsonElement> Proposals(List<JsonElement> records, string role) =>
        [.. records.Where(r => r.GetProperty("kind").GetString() == "strategy.proposal" && r.GetProperty("data").GetProperty("role").GetString() == role)];

    [Fact]
    public void The_arm_name_parses_and_the_usage_and_factory_know_it()
    {
        Assert.True(BotAgentFactory.IsArm("pinned:soviet-rhino-rush"));
        Assert.False(BotAgentFactory.IsArm("pinned:"));
        Assert.Equal("soviet-rhino-rush", BotAgentFactory.PinnedPlaybookId("pinned:soviet-rhino-rush"));
        Assert.Null(BotAgentFactory.PinnedPlaybookId("selector"));
        ArmSpec spec = Assert.Single(CliOptions.Parse(["run", "--arms", "pinned:soviet-rhino-rush"]).ArmSpecs());
        Assert.Equal("pinned:soviet-rhino-rush", spec.Name);
        Assert.False(spec.UsesLlm);
        Assert.Contains("pinned:<playbookId>", CliOptions.Usage);
        Assert.DoesNotContain(':', MatchManifest.Stem(spec, "ai-rush", "twin-valley", 2));
    }

    [Fact]
    public void A_pinned_arm_proposes_only_its_playbook_at_the_normal_cadence()
    {
        List<JsonElement> records = Play("pinned:soviet-rhino-rush", "2");

        List<JsonElement> primary = Proposals(records, "Primary");
        Assert.True(primary.Count >= 2, $"expected renewals, got {primary.Count}");
        Assert.All(primary, static r =>
        {
            JsonElement data = r.GetProperty("data");
            Assert.Equal("pinned-soviet-rhino-rush", data.GetProperty("strategistId").GetString());
            JsonElement intent = data.GetProperty("intent");
            Assert.Equal("soviet-rhino-rush", intent.GetProperty("playbookId").GetString());
            Assert.Equal("Scripted", intent.GetProperty("source").GetString());
            Assert.Equal(1000, intent.GetProperty("playbookParameters").GetProperty("attackArmyValue").GetDouble());
            Assert.Null(data.GetProperty("rawResponse").GetString());
        });
    }

    [Fact]
    public void A_pinned_arm_is_deterministic()
    {
        string a = string.Join('\n', Play("pinned:soviet-rhino-rush", "2").Select(static r => r.GetRawText()));
        string b = string.Join('\n', Play("pinned:soviet-rhino-rush", "2").Select(static r => r.GetRawText()));
        Assert.Equal(a, b);
    }

    [Fact]
    public void An_unknown_playbook_id_is_refused_before_any_match()
    {
        string dir = Path.Combine(root, "refused");
        Assert.Equal(1, Program.Main(["run", "--arms", "pinned:no-such-playbook", "--maps", "training", "--opponents", "ai-rush", "--seeds", "1", "--out", dir]));
        Assert.False(Directory.Exists(Path.Combine(dir, "decisions")));

        (IRulesDatabase rules, IPlaybookLibrary library, _) = Program.LoadRules(null);
        BotAgentFactory factory = new(rules, library, new ArenaRunContext(llmFake: false, llmLatencySeconds: null));
        Assert.Throws<ArgumentException>(() => factory.Create(new ArmSpec("pinned:no-such-playbook", false, false), MatchRunner.ArmPlayer, Faction.Soviet, Bindery.Ra2.Bot.Sim.SimMaps.TwinValley.Map, 2));
    }

    [Fact]
    public void A_faction_the_playbook_does_not_list_warns_on_stderr()
    {
        (IRulesDatabase rules, IPlaybookLibrary library, _) = Program.LoadRules(null);
        BotAgentFactory factory = new(rules, library, new ArenaRunContext(llmFake: false, llmLatencySeconds: null));
        ArmSpec arm = new("pinned:soviet-rhino-rush", false, false);
        TextWriter original = Console.Error;
        StringWriter captured = new();
        Console.SetError(captured);
        try
        {
            factory.Create(arm, MatchRunner.ArmPlayer, Faction.Soviet, Bindery.Ra2.Bot.Sim.SimMaps.TwinValley.Map, 2);
            Assert.Equal(string.Empty, captured.ToString());
            factory.Create(arm, MatchRunner.ArmPlayer, Faction.Allied, Bindery.Ra2.Bot.Sim.SimMaps.TwinValley.Map, 1);
        }
        finally
        {
            Console.SetError(original);
        }
        Assert.Contains("plays Allied but playbook 'soviet-rhino-rush' lists only Soviet", captured.ToString());
    }

    [Fact]
    public void An_induced_playbook_can_be_pinned_through_the_playbooks_flag()
    {
        InductionResult induced = PlaybookInducer.Induce([InduceFixtures.Standard(root)], InduceFixtures.Arm, "training", 4, PlaybookLibrary.LoadDefault());
        string id = Assert.Single(induced.Playbooks).Id;
        string file = Path.Combine(root, "induced.json");
        File.WriteAllText(file, induced.Json);

        // Without the file the id is unknown; with it the pinned arm plays the induced playbook, phases included.
        Assert.Equal(1, Program.Main(["run", "--arms", $"pinned:{id}", "--maps", "training", "--opponents", "ai-rush", "--seeds", "1", "--out", Path.Combine(root, "nofile")]));
        List<JsonElement> primary = Proposals(Play($"pinned:{id}", "2", "--playbooks", file), "Primary");
        Assert.NotEmpty(primary);
        Assert.All(primary, r => Assert.Equal(id, r.GetProperty("data").GetProperty("intent").GetProperty("playbookId").GetString()));
        Assert.Equal(1150, primary[0].GetProperty("data").GetProperty("intent").GetProperty("playbookParameters").GetProperty("attackArmyValue").GetDouble());
    }

    [Fact]
    public void A_side_the_playbook_does_not_list_is_played_by_the_selector_fallback()
    {
        // soviet-rhino-rush is Soviet only; odd seeds put the arm on the Allied side.
        List<JsonElement> records = Play("pinned:soviet-rhino-rush", "1");
        Assert.Empty(Proposals(records, "Primary"));
        Assert.NotEmpty(Proposals(records, "Fallback"));
    }
}
