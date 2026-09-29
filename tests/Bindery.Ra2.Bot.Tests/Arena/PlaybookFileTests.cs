// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Arbitration;
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Claude;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Strategy;
using Bindery.Ra2.Bot.Tests.Arbitration;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary><c>arena run --playbooks</c> and <c>arena playbooks export</c>: extra playbook sets loaded next to the built-in ones.</summary>
[Collection("ConsoleRedirect")]
public sealed class PlaybookFileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bindery-playbooks-{Guid.NewGuid():N}");

    public PlaybookFileTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    /// <summary>A small extra playbook: the default allied-ifv-mix under a new id.</summary>
    private string WriteExtra(string name, string id, string description = "Induced test playbook.")
    {
        Playbook basis = PlaybookLibrary.LoadDefault().All.Single(static p => p.Id == "allied-ifv-mix");
        string path = Path.Combine(root, name);
        File.WriteAllText(path, JsonSerializer.Serialize(new PlaybookDocument([basis with { Id = id, Description = description }]), BotJson.Options));
        return path;
    }

    [Fact]
    public void Export_then_load_gives_equal_playbooks()
    {
        string path = Path.Combine(root, "export.json");
        Assert.Equal(0, Program.Main(["playbooks", "export", "--out", path]));

        PlaybookLibrary loaded = PlaybookLibrary.LoadJson(File.ReadAllText(path));
        PlaybookLibrary expected = PlaybookLibrary.LoadDefault();
        Assert.Equal(12, loaded.All.Count);
        Assert.Equal(expected.All.Select(static p => p.Id), loaded.All.Select(static p => p.Id));
        foreach (Playbook playbook in expected.All)
        {
            Assert.True(loaded.TryGet(playbook.Id, out Playbook got));
            // Records compare their lists by reference, so compare the canonical JSON.
            Assert.Equal(JsonSerializer.Serialize(playbook, BotJson.Options), JsonSerializer.Serialize(got, BotJson.Options));
        }
    }

    [Fact]
    public void A_duplicate_id_is_refused_naming_the_id_and_file()
    {
        string dup = WriteExtra("dup.json", "allied-boom");
        ArgumentException ex = Assert.Throws<ArgumentException>(() => Program.LoadRules(null, [dup]));
        Assert.Contains("allied-boom", ex.Message);
        Assert.Contains("dup.json", ex.Message);

        string a = WriteExtra("a.json", "extra-x");
        string b = WriteExtra("b.json", "extra-x");
        ex = Assert.Throws<ArgumentException>(() => Program.LoadRules(null, [a, b]));
        Assert.Contains("extra-x", ex.Message);
        Assert.Contains("b.json", ex.Message);

        Assert.Equal(1, Program.Main(["run", "--arms", "selector", "--playbooks", dup, "--out", Path.Combine(root, "out")]));
    }

    [Fact]
    public void The_flag_parses_and_appears_in_the_usage()
    {
        Assert.Empty(CliOptions.Parse(["run"]).PlaybookFiles);
        Assert.Equal(["a.json", "b.json"], CliOptions.Parse(["run", "--playbooks", "a.json", "--playbooks", "b.json"]).PlaybookFiles);
        Assert.Contains("--playbooks", CliOptions.Usage);
        Assert.Contains("playbooks export", CliOptions.Usage);
    }

    [Fact]
    public async Task An_extra_playbook_reaches_the_llm_prompt_and_the_validator()
    {
        string extra = WriteExtra("extra.json", "allied-induced-1");
        (IRulesDatabase rules, IPlaybookLibrary library, _) = Program.LoadRules(null, [extra]);
        Assert.Equal(13, library.All.Count);

        // The LLM arm's prompt: play a short match with the fake client and read the catalogue it was sent.
        List<string> catalogues = [];
        ArenaRunContext context = new(llmFake: true, llmLatencySeconds: null)
        {
            LlmRequestObserver = request => catalogues.Add(request.UserContent[0].Text),
        };
        BotAgentFactory factory = new(rules, library, context);
        MatchRunner.Run(new ArmSpec("llm", false, true), "ai-rush", Bindery.Ra2.Bot.Sim.SimMaps.TwinValley, "training", 1, 60, rules, factory);
        Assert.NotEmpty(catalogues);
        Assert.All(catalogues, static c => Assert.Contains("allied-induced-1", c));

        // The validator resolves it through the same library.
        StrategicFeatures features = Fx.Features(100);
        StrategistProposal? proposal = await new PinnedPlaybookStrategist(new Dictionary<Faction, string> { [Faction.Allied] = "allied-induced-1" }, "pinned")
            .ProposeAsync(new StrategistContext(features, rules, library, null, [], null));
        StrategicIntent intent = proposal!.Intent;
        Assert.Equal("allied-induced-1", intent.PlaybookId);
        ValidationResult result = new IntentValidator().Validate(intent, new ValidationContext(features, Fx.Belief(100), rules, library, null, default));
        Assert.True(result.Accepted, string.Join("; ", result.Issues.Select(static i => $"{i.Code}: {i.Message}")));

        // Without the file the same id is unknown.
        Assert.False(PlaybookLibrary.LoadDefault().TryGet("allied-induced-1", out _));
    }

    [Fact]
    public void The_fingerprint_changes_when_the_file_changes_and_is_empty_without_one()
    {
        string extra = WriteExtra("fp.json", "allied-induced-2");
        string[] args = ["run", "--arms", "selector"];
        Assert.Equal("", ArenaScheduling.Fingerprint(CliOptions.Parse(args))["playbooks"]);

        string before = ArenaScheduling.Fingerprint(CliOptions.Parse([.. args, "--playbooks", extra]))["playbooks"];
        Assert.NotEqual("", before);
        Assert.Equal(before, ArenaScheduling.Fingerprint(CliOptions.Parse([.. args, "--playbooks", extra]))["playbooks"]);

        WriteExtra("fp.json", "allied-induced-2", "Edited.");
        Assert.NotEqual(before, ArenaScheduling.Fingerprint(CliOptions.Parse([.. args, "--playbooks", extra]))["playbooks"]);
    }

    [Fact]
    public void The_fingerprint_does_not_depend_on_flag_order()
    {
        string a = WriteExtra("o1.json", "allied-induced-3");
        string b = WriteExtra("o2.json", "allied-induced-4");
        Assert.Equal(ArenaScheduling.PlaybooksHash([a, b]), ArenaScheduling.PlaybooksHash([b, a]));
        Assert.NotEqual(ArenaScheduling.PlaybooksHash([a, b]), ArenaScheduling.PlaybooksHash([a]));
    }

    [Fact]
    public void Replay_refuses_playbook_files_that_changed_since_the_match()
    {
        string extra = WriteExtra("replay.json", "allied-induced-5");
        string dir = Path.Combine(root, "replay-run");
        Assert.Equal(0, Program.Main(["run", "--arms", "selector", "--maps", "training", "--opponents", "ai-rush", "--seeds", "1",
            "--max-seconds", "30", "--playbooks", extra, "--out", dir]));
        string log = Directory.GetFiles(Path.Combine(dir, "decisions"), "*.ndjson").Order().First();

        Assert.True(Program.ReplayMatch(log).Equal);

        WriteExtra("replay.json", "allied-induced-5", "Edited after the match.");
        ArgumentException ex = Assert.Throws<ArgumentException>(() => Program.ReplayMatch(log));
        Assert.Contains("playbook files changed since the match", ex.Message);
    }
}
