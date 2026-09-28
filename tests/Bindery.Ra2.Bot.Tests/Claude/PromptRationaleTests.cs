// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Claude;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Strategy;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Claude;

/// <summary>
/// The 2026-09-28 full-vocabulary shadow pass failed explainability on length: 365 of 427 rationales ran over
/// 400 characters under "a short rationale". The prompt now states the bound and what a rationale must name
/// (docs/experiments/2026-09-29-llm-shadow-short-rationale-preregistration.md). The model is told, not truncated:
/// clipping in code would make the metric measure the code.
/// </summary>
public sealed class PromptRationaleTests
{
    private static string SystemPrompt() =>
        new IntentPromptBuilder().Build(
            new StrategistContext(ClaudeFixtures.Features(), RulesDatabase.LoadEmbeddedFixture(), PlaybookLibrary.LoadDefault(), null, [], null, null),
            StrategistMode.Strategic).SystemPrompt;

    [Fact]
    public void The_rationale_is_bounded_and_names_the_deciding_evidence()
    {
        string prompt = SystemPrompt();

        Assert.Contains("at most 300 characters", prompt, StringComparison.Ordinal);
        Assert.Contains("rationale: one or two sentences", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("a short rationale", prompt, StringComparison.Ordinal);
    }
}
