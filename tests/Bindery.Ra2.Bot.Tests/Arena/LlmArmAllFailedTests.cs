// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Arena;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary>
/// A live LLM arm whose first match got no successful Primary proposal (every request failed, every decision fell
/// back to the selector) is skipped with the first error, not reported as if the model had played.
/// </summary>
public sealed class LlmArmAllFailedTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), $"bindery-llm-allfailed-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private static DecisionRecord Record(string kind, string role, string? message = null) =>
        new(kind, new GameTime(0), 1, JsonSerializer.SerializeToElement(new { role, reason = "request_failed", message }));

    [Fact]
    public void Only_failed_Primary_proposals_give_a_skip_reason_with_the_first_error_truncated()
    {
        string longError = "HTTP 400: anthropic-workspace-id header required " + new string('x', 400);
        List<DecisionRecord> log =
        [
            Record(DecisionRecordKinds.ProposalFailed, "Primary", longError),
            Record(DecisionRecordKinds.ProposalFailed, "Primary", "second"),
            Record(DecisionRecordKinds.Proposal, "Shadow"),
        ];

        string? reason = Program.AllPrimaryProposalsFailed(log);

        Assert.NotNull(reason);
        Assert.StartsWith("skipped: 0 of 2 model proposals succeeded in the first match (HTTP 400: anthropic-workspace-id", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("second", reason, StringComparison.Ordinal);
        Assert.True(reason.Length < 320, reason);
    }

    [Fact]
    public void One_successful_Primary_proposal_lets_the_arm_proceed()
    {
        List<DecisionRecord> log =
        [
            Record(DecisionRecordKinds.ProposalFailed, "Primary", "boom"),
            Record(DecisionRecordKinds.Proposal, "Primary"),
        ];

        Assert.Null(Program.AllPrimaryProposalsFailed(log));
    }

    [Fact]
    public void Failed_Shadow_proposals_alone_or_an_empty_log_do_not_skip()
    {
        Assert.Null(Program.AllPrimaryProposalsFailed([Record(DecisionRecordKinds.ProposalFailed, "Shadow", "boom")]));
        Assert.Null(Program.AllPrimaryProposalsFailed([]));
    }

    [Fact]
    public void A_live_arm_whose_endpoint_fails_every_request_is_skipped_with_the_reason_and_no_results()
    {
        // Nothing listens on port 1: every model request fails, so every Primary proposal fails.
        Assert.Equal(0, Program.Main(["run", "--arms", "llm", "--maps", "training", "--opponents", "live-rush", "--seeds", "2",
            "--max-seconds", "120", "--llm-endpoint", "http://127.0.0.1:1/v1", "--no-decisions", "--out", dir]));

        using JsonDocument results = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "results.json")));
        Assert.Equal(0, results.RootElement.GetArrayLength());
        using JsonDocument probes = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "probes.json")));
        JsonElement skipped = Assert.Single(probes.RootElement.GetProperty("skipped").EnumerateArray());
        Assert.Contains("model proposals succeeded in the first match", skipped.GetProperty("reason").GetString(), StringComparison.Ordinal);
        string report = File.ReadAllText(Path.Combine(dir, "report.md"));
        Assert.Contains("## Skipped arms", report, StringComparison.Ordinal);
        Assert.Contains("model proposals succeeded in the first match", report, StringComparison.Ordinal);
    }
}
