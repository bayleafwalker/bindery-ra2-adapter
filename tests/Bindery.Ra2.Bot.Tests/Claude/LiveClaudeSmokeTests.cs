// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Claude;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Claude;

/// <summary>
/// A fact that runs only when <c>BINDERY_BOT_LIVE_LLM=1</c>. xunit 2.9 has no
/// dynamic skip, so the decision is made when the attribute is constructed and
/// reported as an ordinary skip with the reason.
/// </summary>
public sealed class LiveLlmFactAttribute : FactAttribute
{
    public const string Variable = "BINDERY_BOT_LIVE_LLM";

    public LiveLlmFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = $"Live LLM test: set {Variable}=1 (and ANTHROPIC_API_KEY) to run against the real API.";
        }
    }
}

public sealed class LiveClaudeSmokeTests
{
    [LiveLlmFact]
    public async Task Live_strategist_returns_a_catalogue_playbook()
    {
        ClaudeStrategist strategist = new(new AnthropicMessageClient(), new ClaudeStrategistOptions { RequestTimeoutSeconds = 120 });
        StrategistContext context = ClaudeFixtures.Context(active: ClaudeFixtures.ActiveIntent());

        StrategistProposal? proposal = await strategist.ProposeAsync(context);

        Assert.True(proposal is not null, $"{strategist.LastFailure?.Code}: {strategist.LastFailure?.Detail}");
        Assert.Contains(proposal.Intent.PlaybookId, context.Playbooks.For(Faction.Allied).Select(p => p.Id));
        Assert.True(proposal.Cost.InputTokens + proposal.Cost.CacheReadTokens > 0);
        Assert.NotNull(PriceTable.CostUsd(proposal.Cost));
    }
}
