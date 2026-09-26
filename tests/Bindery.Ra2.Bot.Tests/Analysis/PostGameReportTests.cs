// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Analysis;
using Bindery.Ra2.Bot.Runtime;
using Bindery.Ra2.Bot.Tests.Runtime;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Analysis;

/// <summary>
/// A post-game report from a real runtime's decision log: a primary that pivots from boom to turtle at 50 s and
/// proposes an unknown playbook from 90 s, and a shadow that always says boom.
/// </summary>
public sealed class PostGameReportTests
{
    private static IReadOnlyList<DecisionRecord> Log()
    {
        ScriptedStrategist primary = new("primary", IntentSource.Llm, static c =>
        {
            double t = c.Features.Time.Seconds;
            StrategistProposal p = t < 50
                ? Proposals.For(c, $"p-{c.Features.SnapshotVersion}", "allied-boom", StrategicPosture.Boom, 0.6)
                : t < 90
                    ? Proposals.For(c, $"p-{c.Features.SnapshotVersion}", "allied-turtle", StrategicPosture.Turtle, 0.95)
                    : Proposals.For(c, $"p-{c.Features.SnapshotVersion}", "no-such-playbook", StrategicPosture.Turtle, 0.95);
            return p with { Intent = p.Intent with { Rationale = $"because t={t:0}" } };
        });
        ScriptedStrategist shadow = new("shadow", IntentSource.Llm, static c => Proposals.For(c, $"s-{c.Features.SnapshotVersion}", "allied-boom", StrategicPosture.Boom, 0.6));
        DecisionLog log = new();
        using BotRuntime runtime = Runtimes.Create(primary, shadow: shadow, log: log,
            options: new BotOptions(StrategicCadenceSeconds: 20, RunDeterministicStrategistsInline: true));
        for (long f = 0; f < 120 * GameTime.FramesPerSecond; f++) runtime.Tick(Frames.At(f));
        return log.Records;
    }

    [Fact]
    public void Timeline_carries_each_intent_with_its_rationale_trigger_and_end()
    {
        PostGameReport report = PostGameReport.Build(Log());

        TimelineEntry first = report.Timeline[0];
        Assert.Equal("allied-boom", first.PlaybookId);
        Assert.Equal("initial", first.Trigger);
        Assert.Equal("because t=0", first.Rationale);
        Assert.NotNull(first.EndedAtSeconds);
        Assert.Contains(report.Timeline, static e => e.PlaybookId == "allied-turtle" && e.Rationale is not null && e.Rationale.StartsWith("because", StringComparison.Ordinal));
        Assert.True(first.Renewals > 0, "later boom proposals renewed the first intent");
    }

    [Fact]
    public void Pivots_name_what_changed_and_what_triggered_it()
    {
        PostGameReport report = PostGameReport.Build(Log());

        Pivot pivot = Assert.Single(report.Pivots);
        Assert.Equal(("allied-boom", "Boom", "allied-turtle", "Turtle"), (pivot.FromPlaybook, pivot.FromPosture, pivot.ToPlaybook, pivot.ToPosture));
        Assert.Equal("cadence", pivot.Trigger);
        Assert.InRange(pivot.AtSeconds, 50, 90);
        Assert.NotNull(pivot.PreviousEndReason);
    }

    [Fact]
    public void Rejected_proposals_carry_the_validator_codes()
    {
        PostGameReport report = PostGameReport.Build(Log());

        RejectedProposal rejected = Assert.Single(report.Rejected, static r => r.Outcome == "rejected");
        Assert.Equal("primary", rejected.StrategistId);
        Assert.NotEmpty(rejected.Codes);
        Assert.All(rejected.Codes, static c => Assert.Contains(".", c, StringComparison.Ordinal));
    }

    [Fact]
    public void Shadow_agreement_compares_each_shadow_answer_with_the_primary_answer_to_the_same_request()
    {
        PostGameReport report = PostGameReport.Build(Log());

        // Requests at 0, 20, 40 (primary boom = shadow boom), 60, 80 (turtle), 100 (unknown playbook).
        Assert.Equal(6, report.Shadow.Compared);
        Assert.Equal(3, report.Shadow.Agreed);
        Assert.Equal(0.5, report.Shadow.AgreementRate);
        Assert.Equal(6, report.Shadow.ShadowPlaybooks["allied-boom"]);
    }

    [Fact]
    public void Time_by_playbook_adds_up_and_the_markdown_is_deterministic()
    {
        IReadOnlyList<DecisionRecord> log = Log();
        PostGameReport report = PostGameReport.Build(log);

        Assert.Equal(report.PlaybookSeconds.Values.Sum(), report.PostureSeconds.Values.Sum(), 6);
        Assert.InRange(report.PlaybookSeconds.Values.Sum(), 100, report.DurationSeconds + 1e-9);
        string markdown = report.ToMarkdown();
        Assert.Equal(markdown, PostGameReport.Build(log).ToMarkdown());
        foreach (string heading in new[] { "## Intent timeline", "## Pivots", "## Proposals without effect", "## Key events", "## Shadow strategist" })
        {
            Assert.Contains(heading, markdown, StringComparison.Ordinal);
        }
        Assert.Contains("Same playbook: 3/6 (0.500)", markdown, StringComparison.Ordinal);
    }
}
