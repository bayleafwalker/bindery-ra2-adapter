// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Claude;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Sim;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary>
/// A live LLM arm whose calls mostly fail is labelled "not a model result" (the selector fallback played the
/// gaps) and never counts as adoption evidence; a clean arm is left alone.
/// </summary>
public sealed class LlmFailureRateTests
{
    private static DecisionRecord Rec(string kind, object data) => new(kind, new GameTime(0), 1, JsonSerializer.SerializeToElement(data));

    private static DecisionRecord Answered(string source = "Llm") => Rec(DecisionRecordKinds.Proposal, new { role = "Primary", intent = new { source } });

    private static DecisionRecord FailedCall(string code) => Rec(DecisionRecordKinds.ProposalFailed, new { strategist = "s", code, detail = "d" });

    // The scheduler's Primary record for an arbitration outcome, not a failed call.
    private static DecisionRecord Arbitration(string reason) => Rec(DecisionRecordKinds.ProposalFailed, new { role = "Primary", strategistId = "s", reason, message = (string?)null });

    private static LlmCallTally Count(params DecisionRecord[] log)
    {
        ArenaAgentStats stats = new();
        DecisionLogMetrics.Apply(stats, log);
        return new LlmCallTally(stats.LlmAnswered, stats.LlmFailed);
    }

    private static PlayerMatchMetrics P(LlmCallTally? calls) => new(
        Faction.Allied, 10, 0, 0, [], 1, 0, 0, new Dictionary<string, int>(), 0, 0, 0, 0, 0.1, 1000, 500, 500, 0, 0, null, 0, 5000, 10, 3, 2000, null, [])
    { LlmCalls = calls };

    private static MatchRecord M(string arm, int seed, int? winner, LlmCallTally? calls, string split = "heldout") =>
        new(arm, "live-rush", "open-steppe", split, seed, winner, "elimination", 300,
            new Dictionary<string, PlayerMatchMetrics> { ["arm"] = P(calls), ["opponent"] = P(null) });

    [Fact]
    public void Counts_answers_and_transport_or_model_failures_but_not_arbitration_outcomes()
    {
        LlmCallTally tally = Count(
            Answered(), Answered(), Answered(),
            FailedCall("claude.timeout"), Arbitration("no_opinion"),
            FailedCall("claude.server_error"), Arbitration("no_opinion"),
            FailedCall("claude.parse_failed"),
            Arbitration("superseded"), Arbitration("no_opinion"),
            Rec(DecisionRecordKinds.Proposal, new { role = "Shadow" }),
            FailedCall("claude.cancelled"),
            FailedCall("claude.refine_no_active_intent"), FailedCall("claude.refine_not_strategic_plan"), FailedCall("claude.refine_playbook_switch"));

        Assert.Equal(3, tally.Answered);
        Assert.Equal(3, tally.Failed);
        Assert.Equal(0.5, tally.FailureRate);
    }

    [Fact]
    public void Only_model_intents_are_answers_not_selector_distilled_or_fallback_proposals()
    {
        LlmCallTally tally = Count(Answered("Selector"), Answered("Bandit"), Answered("Distilled"), Answered("Fallback"), Answered("Llm"),
            Rec(DecisionRecordKinds.ShadowProposal, new { role = "Shadow", intent = new { source = "Llm" } }),
            FailedCall("claude.timeout"));

        Assert.Equal(new LlmCallTally(2, 1), tally);
    }

    private static MatchRecord PlayReal(ArmSpec arm, ArenaRunContext context)
    {
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();
        BotAgentFactory factory = new(rules, PlaybookLibrary.LoadDefault(), context);
        return MatchRunner.Run(arm, "rush", SimMaps.TwinValley, "training", 1, 60, rules, factory);
    }

    [Theory]
    [InlineData("selector")]
    [InlineData("bandit")]
    public void Arms_without_a_model_strategist_have_no_tally_through_the_real_arena_path(string arm)
    {
        MatchRecord match = PlayReal(new ArmSpec(arm, false, true), new ArenaRunContext(llmFake: true, llmLatencySeconds: null));

        Assert.Null(match.Players["arm"].LlmCalls);
    }

    [Fact]
    public void A_fake_client_arm_has_no_tally_because_it_is_not_a_model()
    {
        MatchRecord match = PlayReal(new ArmSpec("llm", false, true), new ArenaRunContext(llmFake: true, llmLatencySeconds: null));

        Assert.Null(match.Players["arm"].LlmCalls);
    }

    [Theory]
    [InlineData("llm")]
    [InlineData("llm-shadow")]
    [InlineData("distilled")]
    public void A_live_arm_whose_every_call_fails_is_tallied_and_labelled_end_to_end(string arm)
    {
        // No credential: every request fails as unauthorized, the same path a run of timeouts takes.
        ArenaRunContext context = new(llmFake: false, llmLatencySeconds: 3);
        context.MarkLlmSkipped("test: no credential");

        MatchRecord match = PlayReal(new ArmSpec(arm, false, false), context);

        LlmCallTally tally = Assert.IsType<LlmCallTally>(match.Players["arm"].LlmCalls);
        Assert.True(tally.Failed > 0, arm);
        Assert.Equal(0, tally.Answered);
        Assert.Equal(1.0, tally.FailureRate);
        string report = ReportBuilder.Build([match], [], [], CliOptions.Parse(["run", "--arms", arm]), "test");
        Assert.Contains($"| {arm} (not a model result) | training |", report, StringComparison.Ordinal);
        Assert.Contains($"{arm} on training maps: not a model result", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Superseded_and_no_opinion_alone_are_not_failures()
    {
        LlmCallTally tally = Count(Answered(), Arbitration("superseded"), Arbitration("no_opinion"));

        Assert.Equal(new LlmCallTally(1, 0), tally);
        Assert.Equal(0, tally.FailureRate);
    }

    [Fact]
    public void The_tally_is_in_results_json_as_llmCalls()
    {
        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(M("llm", 1, 0, new LlmCallTally(3, 141)), BotJson.Options));
        JsonElement calls = doc.RootElement.GetProperty("players").GetProperty("arm").GetProperty("llmCalls");

        Assert.Equal(3, calls.GetProperty("answered").GetInt32());
        Assert.Equal(141, calls.GetProperty("failed").GetInt32());
        Assert.Equal(141 / 144.0, calls.GetProperty("failureRate").GetDouble(), 6);
    }

    [Fact]
    public void An_arm_over_the_threshold_is_marked_in_the_report_and_gets_a_warning()
    {
        List<MatchRecord> matches = [.. Enumerable.Range(1, 4).Select(s => M("llm", s, 0, new LlmCallTally(s == 1 ? 3 : 0, s == 1 ? 40 : 34)))];
        CliOptions options = CliOptions.Parse(["run", "--arms", "llm"]);

        string report = ReportBuilder.Build(matches, [], [], options, "test");
        IReadOnlyDictionary<(string Arm, string Split), LlmCallTally> unreliable = LlmCallTally.Unreliable(matches, options.MaxLlmFailureRate);

        Assert.Contains("| llm (not a model result) | heldout |", report, StringComparison.Ordinal);
        Assert.Contains("3 answered, 142 failed (0.979)", report, StringComparison.Ordinal);
        Assert.Contains("**llm on heldout maps: not a model result.**", report, StringComparison.Ordinal);
        Assert.Equal(4, matches.Count);
        LlmCallTally tally = Assert.Single(unreliable).Value;
        Assert.Contains("142 of 145 model calls failed", tally.Warning("llm", "heldout", 0.2), StringComparison.Ordinal);
    }

    [Fact]
    public void The_threshold_is_a_flag_and_the_boundary_is_not_over()
    {
        Assert.Equal(0.2, CliOptions.Parse(["run"]).MaxLlmFailureRate);
        Assert.Equal(0.5, CliOptions.Parse(["run", "--max-llm-failure-rate", "0.5"]).MaxLlmFailureRate);
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["run", "--max-llm-failure-rate", "2"]));
        Assert.False(new LlmCallTally(8, 2).Exceeds(0.2));
        Assert.True(new LlmCallTally(7, 3).Exceeds(0.2));

        List<MatchRecord> matches = [M("llm", 1, 0, new LlmCallTally(3, 4))];
        Assert.DoesNotContain("not a model result", ReportBuilder.Build(matches, [], [], CliOptions.Parse(["run", "--arms", "llm", "--max-llm-failure-rate", "0.6"]), "test"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_clean_arm_stays_unmarked()
    {
        List<MatchRecord> matches = [.. Enumerable.Range(1, 4).Select(s => M("llm", s, 0, new LlmCallTally(30, 2))),
                                     .. Enumerable.Range(1, 4).Select(s => M("selector", s, 1, null))];

        string report = ReportBuilder.Build(matches, [], [], CliOptions.Parse(["run", "--arms", "selector,llm"]), "test");

        Assert.Empty(LlmCallTally.Unreliable(matches, 0.2));
        Assert.DoesNotContain("not a model result", report, StringComparison.Ordinal);
        Assert.Contains("120 answered, 8 failed (0.063)", report, StringComparison.Ordinal);
    }

    [Fact]
    public void The_threshold_applies_per_split_for_labels_and_adoption()
    {
        LlmCallTally bad = new(3, 141);
        List<MatchRecord> matches =
        [
            .. Enumerable.Range(1, 40).Select(s => M("llm-t1", s, s <= 30 ? 1 : 0, new LlmCallTally(30, 1))),
            .. Enumerable.Range(1, 40).Select(s => M("llm-t2", s, 0, new LlmCallTally(30, 1))),
            .. Enumerable.Range(1, 40).Select(s => M("llm-t2", s, 0, bad, "training")),
        ];

        (string Arm, string Split) only = Assert.Single(LlmCallTally.Unreliable(matches, 0.2)).Key;
        VocabularyAdoption adoption = Program.TierAdoption(matches, live: true, "2026-09-29");
        string report = ReportBuilder.Build(matches, [], [], CliOptions.Parse(["run", "--arms", "llm-t1,llm-t2"]), "test");

        Assert.Equal(("llm-t2", "training"), only);
        Assert.Equal(VocabularyTier.ObjectivesAndRegions, adoption.AdoptedTier);
        Assert.Contains("| llm-t2 (not a model result) | training |", report, StringComparison.Ordinal);
        Assert.Contains("| llm-t2 | heldout |", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Adoption_evidence_from_an_arm_over_the_threshold_is_refused_like_fake_evidence()
    {
        LlmCallTally bad = new(3, 141);
        List<MatchRecord> matches =
        [
            .. Enumerable.Range(1, 40).Select(s => M("llm-t1", s, s <= 30 ? 1 : 0, new LlmCallTally(30, 1))),
            .. Enumerable.Range(1, 40).Select(s => M("llm-t2", s, 0, bad)),
        ];

        VocabularyAdoption refused = Program.TierAdoption(matches, live: true, "2026-09-29");
        VocabularyAdoption clean = Program.TierAdoption([.. matches.Select(static m => m.Arm == "llm-t2" ? M(m.Arm, m.Seed, m.Winner, new LlmCallTally(30, 1)) : m)], live: true, "2026-09-29");

        Assert.Equal(VocabularyTier.Parameters, refused.AdoptedTier);
        Assert.False(Assert.Single(refused.Evidence).Live);
        Assert.Contains(refused.Reasons, static r => r.Contains("llm-t2 is not a model result", StringComparison.Ordinal));
        Assert.Equal(VocabularyTier.ObjectivesAndRegions, clean.AdoptedTier);
        // Raising the limit admits the same evidence.
        Assert.Equal(VocabularyTier.ObjectivesAndRegions, Program.TierAdoption(matches, live: true, "2026-09-29", maxLlmFailureRate: 0.99).AdoptedTier);
    }
}
