// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Analysis;
using Bindery.Ra2.Bot.Claude;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Claude;

public sealed class PostGameNarratorTests
{
    private static PostGameReport Report() => PostGameReport.Build([]);

    [Fact]
    public async Task Narrative_comes_back_from_structured_output()
    {
        FakeMessageClient client = FakeMessageClient.Replying("""{"narrative":"The bot boomed, then turtled."}""");
        NarrationResult result = await new PostGameNarrator(client).NarrateAsync(Report(), "arm llm vs live-rush on twin-valley, seed 1");

        Assert.Equal("The bot boomed, then turtled.", result.Narrative);
        Assert.Null(result.Failure);
        Assert.Equal(1200, result.Cost.InputTokens);
        ModelRequest request = Assert.Single(client.Requests);
        Assert.Equal(PostGameNarrator.SystemPrompt, request.SystemPrompt);
        Assert.Equal(PostGameNarrator.Schema, request.JsonSchema);
        Assert.Equal("arm llm vs live-rush on twin-valley, seed 1", request.UserContent[0].Text);
        Assert.Equal(Report().ToJson(), request.UserContent[^1].Text);
    }

    [Theory]
    [InlineData("""{"narrative":"x"}""", "refusal")]
    [InlineData("not json", "end_turn")]
    [InlineData("""{"story":"x"}""", "end_turn")]
    public async Task Refusals_and_malformed_replies_are_failures_not_narratives(string text, string stop)
    {
        NarrationResult result = await new PostGameNarrator(FakeMessageClient.Replying(text, stop)).NarrateAsync(Report());

        Assert.Null(result.Narrative);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task Api_failures_are_reported_not_thrown()
    {
        NarrationResult result = await new PostGameNarrator(FakeMessageClient.Throwing(new ModelClientException(ModelFailureKind.Unauthorized, "no key"))).NarrateAsync(Report());

        Assert.Null(result.Narrative);
        Assert.Contains("Unauthorized", result.Failure, StringComparison.Ordinal);
    }
}
