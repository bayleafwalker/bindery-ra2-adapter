// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Claude;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Claude;

public sealed class ClaudeStrategistTests
{
    [Fact]
    public async Task Valid_reply_becomes_an_llm_proposal_with_cost()
    {
        FakeMessageClient client = FakeMessageClient.Replying(ClaudeFixtures.DraftJson(), usage: new ModelUsage(1500, 400, 900, 100), modelId: "claude-opus-5");
        ClaudeStrategist strategist = new(client);

        StrategistProposal? proposal = await strategist.ProposeAsync(ClaudeFixtures.Context(ClaudeFixtures.Features(version: 9)));

        Assert.NotNull(proposal);
        Assert.Equal(IntentSource.Llm, strategist.Source);
        Assert.Equal(IntentSource.Llm, proposal.Intent.Source);
        Assert.Equal("claude-strategic/9", proposal.Intent.IntentId);
        Assert.Equal(9, proposal.Intent.BasedOnSnapshotVersion);
        Assert.Equal(1500, proposal.Cost.InputTokens);
        Assert.Equal(400, proposal.Cost.OutputTokens);
        Assert.Equal(900, proposal.Cost.CacheReadTokens);
        Assert.Equal("claude-opus-5", proposal.Cost.Model);
        Assert.True(proposal.Cost.LatencySeconds >= 0);
        Assert.Equal(ClaudeFixtures.DraftJson(), proposal.RawResponse);
        Assert.Null(strategist.LastFailure);
    }

    [Fact]
    public void Request_for_the_default_model_uses_effort_adaptive_thinking_schema_and_fallbacks()
    {
        ClaudeStrategist strategist = new(FakeMessageClient.Replying("{}"));
        ModelRequest request = strategist.BuildRequest(ClaudeFixtures.Context());

        Assert.Equal("claude-opus-5", request.Model);
        Assert.Equal("low", request.Effort);
        Assert.True(request.AdaptiveThinking);
        Assert.True(request.ServerFallbacks);
        Assert.Equal(16000, request.MaxTokens);
        Assert.Equal(IntentDraftSchema.Json, request.JsonSchema);
    }

    [Fact]
    public void Haiku_requests_omit_effort_thinking_and_fallbacks()
    {
        ClaudeStrategist strategist = new(FakeMessageClient.Replying("{}"), ClaudeStrategistOptions.ForRefine());
        ModelRequest request = strategist.BuildRequest(ClaudeFixtures.Context(active: ClaudeFixtures.ActiveIntent()));

        Assert.Equal("claude-haiku-4-5", request.Model);
        Assert.Null(request.Effort);
        Assert.False(request.AdaptiveThinking);
        Assert.False(request.ServerFallbacks);
    }

    [Fact]
    public void Server_fallbacks_can_be_disabled()
    {
        ClaudeStrategist strategist = new(FakeMessageClient.Replying("{}"), new ClaudeStrategistOptions { EnableServerFallbacks = false });
        Assert.False(strategist.BuildRequest(ClaudeFixtures.Context()).ServerFallbacks);
    }

    [Fact]
    public async Task System_prompt_is_byte_stable_across_calls()
    {
        FakeMessageClient client = FakeMessageClient.Replying(ClaudeFixtures.DraftJson());
        ClaudeStrategist strategist = new(client);

        await strategist.ProposeAsync(ClaudeFixtures.Context(ClaudeFixtures.Features(version: 1, seconds: 60)));
        await strategist.ProposeAsync(ClaudeFixtures.Context(ClaudeFixtures.Features(version: 2, seconds: 400, credits: 10), ClaudeFixtures.ActiveIntent(), "rusher"));

        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(client.Requests[0].SystemPrompt, client.Requests[1].SystemPrompt);
        Assert.NotEqual(client.Requests[0].UserContent[1].Text, client.Requests[1].UserContent[1].Text);
    }

    [Fact]
    public async Task Refusal_returns_null_even_when_the_text_would_parse()
    {
        FakeMessageClient client = FakeMessageClient.Replying(ClaudeFixtures.DraftJson(), stopReason: "refusal", usage: new ModelUsage(1000, 0, 0, 0));
        ClaudeStrategist strategist = new(client);
        List<ClaudeProposalFailure> raised = new();
        strategist.ProposalFailed += (_, f) => raised.Add(f);

        StrategistProposal? proposal = await strategist.ProposeAsync(ClaudeFixtures.Context());

        Assert.Null(proposal);
        Assert.Equal(ClaudeFailureCodes.Refusal, strategist.LastFailure?.Code);
        Assert.Equal(1000, strategist.LastFailure?.Cost.InputTokens);
        Assert.Single(raised);
    }

    [Fact]
    public async Task Max_tokens_stop_returns_null_even_when_the_text_would_parse()
    {
        ClaudeStrategist strategist = new(FakeMessageClient.Replying(ClaudeFixtures.DraftJson(), stopReason: "max_tokens"));

        Assert.Null(await strategist.ProposeAsync(ClaudeFixtures.Context()));
        Assert.Equal(ClaudeFailureCodes.MaxTokens, strategist.LastFailure?.Code);
    }

    [Fact]
    public async Task Unexpected_stop_reason_returns_null()
    {
        ClaudeStrategist strategist = new(FakeMessageClient.Replying(ClaudeFixtures.DraftJson(), stopReason: "pause_turn"));

        Assert.Null(await strategist.ProposeAsync(ClaudeFixtures.Context()));
        Assert.Equal(ClaudeFailureCodes.UnexpectedStop, strategist.LastFailure?.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"playbookId\":\"allied-boom\"")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task Malformed_json_returns_null(string text)
    {
        ClaudeStrategist strategist = new(FakeMessageClient.Replying(text));

        Assert.Null(await strategist.ProposeAsync(ClaudeFixtures.Context()));
        Assert.Equal(ClaudeFailureCodes.ParseFailed, strategist.LastFailure?.Code);
        Assert.Equal(text, strategist.LastFailure?.RawResponse);
    }

    [Fact]
    public async Task Unknown_enum_returns_null_with_mapping_failure()
    {
        ClaudeStrategist strategist = new(FakeMessageClient.Replying(ClaudeFixtures.DraftJson(ClaudeFixtures.Draft(posture: "Blitz"))));

        Assert.Null(await strategist.ProposeAsync(ClaudeFixtures.Context()));
        Assert.Equal(ClaudeFailureCodes.MappingFailed, strategist.LastFailure?.Code);
    }

    [Fact]
    public async Task Refine_mode_rejects_a_playbook_switch()
    {
        FakeMessageClient client = FakeMessageClient.Replying(ClaudeFixtures.DraftJson(ClaudeFixtures.Draft(playbookId: "allied-harass", posture: "Harass")));
        ClaudeStrategist strategist = new(client, ClaudeStrategistOptions.ForRefine());

        StrategistProposal? proposal = await strategist.ProposeAsync(ClaudeFixtures.Context(active: ClaudeFixtures.ActiveIntent("allied-boom") with { Source = IntentSource.Llm }));

        Assert.Null(proposal);
        Assert.Equal(ClaudeFailureCodes.RefinePlaybookSwitch, strategist.LastFailure?.Code);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Refine_mode_changes_parameters_only()
    {
        // The model also tries to move posture, budget and objectives: refine keeps the active intent's.
        IntentDraft draft = ClaudeFixtures.Draft(playbookId: "allied-boom", posture: "AllIn", aggression: 0.95) with
        {
            Budget = new DraftBudget { Economy = 0, Army = 1, Tech = 0, Defense = 0 },
        };
        ClaudeStrategist strategist = new(FakeMessageClient.Replying(ClaudeFixtures.DraftJson(draft), modelId: "claude-haiku-4-5-20251001"), ClaudeStrategistOptions.ForRefine());
        StrategicIntent active = ClaudeFixtures.ActiveIntent("allied-boom") with { Source = IntentSource.Llm };

        StrategistProposal? proposal = await strategist.ProposeAsync(ClaudeFixtures.Context(ClaudeFixtures.Features(version: 11), active));

        Assert.NotNull(proposal);
        StrategicIntent refined = proposal.Intent;
        Assert.Equal("allied-boom", refined.PlaybookId);
        Assert.Equal(active.Posture, refined.Posture);
        Assert.Equal(active.Budget, refined.Budget);
        Assert.Equal(active.Objectives, refined.Objectives);
        Assert.Equal(active.AbortTriggers, refined.AbortTriggers);
        Assert.Equal(0.95, refined.PlaybookParameters["aggression"]);
        Assert.Equal(IntentSource.Llm, refined.Source);
        Assert.Equal(11, refined.BasedOnSnapshotVersion);
        Assert.Equal("claude-refine/11", refined.IntentId);
    }

    [Fact]
    public async Task Refine_mode_without_an_active_intent_does_not_call_the_model()
    {
        FakeMessageClient client = FakeMessageClient.Replying(ClaudeFixtures.DraftJson());
        ClaudeStrategist strategist = new(client, ClaudeStrategistOptions.ForRefine());

        Assert.Null(await strategist.ProposeAsync(ClaudeFixtures.Context()));
        Assert.Equal(ClaudeFailureCodes.RefineNoActiveIntent, strategist.LastFailure?.Code);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Timeout_returns_null_without_throwing()
    {
        FakeMessageClient client = new(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        });
        ClaudeStrategist strategist = new(client, new ClaudeStrategistOptions { RequestTimeoutSeconds = 0.2 });

        StrategistProposal? proposal = await strategist.ProposeAsync(ClaudeFixtures.Context());

        Assert.Null(proposal);
        Assert.Equal(ClaudeFailureCodes.Timeout, strategist.LastFailure?.Code);
        Assert.True(strategist.LastFailure!.Cost.LatencySeconds >= 0.15);
    }

    [Fact]
    public async Task Timeout_holds_even_when_the_client_ignores_cancellation()
    {
        TaskCompletionSource<ModelReply> never = new();
        FakeMessageClient client = new((_, _) => never.Task);
        ClaudeStrategist strategist = new(client, new ClaudeStrategistOptions { RequestTimeoutSeconds = 0.2 });

        Task<StrategistProposal?> call = strategist.ProposeAsync(ClaudeFixtures.Context());
        Task finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(call, finished);
        Assert.Null(await call);
        Assert.Equal(ClaudeFailureCodes.Timeout, strategist.LastFailure?.Code);
    }

    [Fact]
    public async Task Caller_cancellation_returns_null_without_throwing()
    {
        FakeMessageClient client = new(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        });
        ClaudeStrategist strategist = new(client);
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(100));

        Assert.Null(await strategist.ProposeAsync(ClaudeFixtures.Context(), cts.Token));
        Assert.Equal(ClaudeFailureCodes.Cancelled, strategist.LastFailure?.Code);
    }

    [Theory]
    [InlineData(ModelFailureKind.RateLimited, ClaudeFailureCodes.RateLimited)]
    [InlineData(ModelFailureKind.ServerError, ClaudeFailureCodes.ServerError)]
    [InlineData(ModelFailureKind.Connection, ClaudeFailureCodes.Connection)]
    [InlineData(ModelFailureKind.InvalidRequest, ClaudeFailureCodes.InvalidRequest)]
    [InlineData(ModelFailureKind.Unauthorized, ClaudeFailureCodes.Unauthorized)]
    [InlineData(ModelFailureKind.Unknown, ClaudeFailureCodes.ClientError)]
    public async Task Api_failures_return_null_with_a_classified_reason(ModelFailureKind kind, string code)
    {
        ClaudeStrategist strategist = new(FakeMessageClient.Throwing(new ModelClientException(kind, "boom")));

        Assert.Null(await strategist.ProposeAsync(ClaudeFixtures.Context()));
        Assert.Equal(code, strategist.LastFailure?.Code);
    }

    [Fact]
    public async Task Unexpected_client_exceptions_and_synchronous_throws_return_null()
    {
        ClaudeStrategist faulted = new(FakeMessageClient.Throwing(new InvalidOperationException("bad")));
        ClaudeStrategist sync = new(new FakeMessageClient((_, _) => throw new InvalidOperationException("sync")));

        Assert.Null(await faulted.ProposeAsync(ClaudeFixtures.Context()));
        Assert.Null(await sync.ProposeAsync(ClaudeFixtures.Context()));
        Assert.Equal(ClaudeFailureCodes.ClientError, faulted.LastFailure?.Code);
        Assert.Equal(ClaudeFailureCodes.ClientError, sync.LastFailure?.Code);
    }

    [Fact]
    public async Task Throwing_failure_subscriber_does_not_escape()
    {
        ClaudeStrategist strategist = new(FakeMessageClient.Replying("nope"));
        strategist.ProposalFailed += (_, _) => throw new InvalidOperationException("subscriber bug");

        Assert.Null(await strategist.ProposeAsync(ClaudeFixtures.Context()));
    }

    [Fact]
    public async Task Success_clears_the_previous_failure()
    {
        int call = 0;
        FakeMessageClient client = new((_, _) => Task.FromResult(call++ == 0
            ? new ModelReply("garbage", "end_turn", null, ModelUsage.None, "claude-opus-5")
            : new ModelReply(ClaudeFixtures.DraftJson(), "end_turn", null, ModelUsage.None, "claude-opus-5")));
        ClaudeStrategist strategist = new(client);

        await strategist.ProposeAsync(ClaudeFixtures.Context());
        Assert.NotNull(strategist.LastFailure);
        Assert.NotNull(await strategist.ProposeAsync(ClaudeFixtures.Context()));
        Assert.Null(strategist.LastFailure);
    }

    [Fact]
    public async Task Failure_converts_to_a_proposal_failed_decision_record()
    {
        ClaudeStrategist strategist = new(FakeMessageClient.Replying("garbage"));
        await strategist.ProposeAsync(ClaudeFixtures.Context(ClaudeFixtures.Features(version: 33, seconds: 120)));

        DecisionRecord record = strategist.LastFailure!.ToDecisionRecord(strategist.Id);

        Assert.Equal(DecisionRecordKinds.ProposalFailed, record.Kind);
        Assert.Equal(33, record.SnapshotVersion);
        Assert.Equal(GameTime.FromSeconds(120), record.Time);
        Assert.Equal(ClaudeFailureCodes.ParseFailed, record.Data.GetProperty("code").GetString());
        Assert.Equal("claude-strategic", record.Data.GetProperty("strategist").GetString());
        Assert.Equal(JsonValueKind.Object, record.Data.GetProperty("cost").ValueKind);
    }

    [Fact]
    public void Invalid_options_are_rejected_at_construction()
    {
        FakeMessageClient client = FakeMessageClient.Replying("{}");
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClaudeStrategist(client, new ClaudeStrategistOptions { Effort = "extreme" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClaudeStrategist(client, new ClaudeStrategistOptions { MaxTokens = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClaudeStrategist(client, new ClaudeStrategistOptions { RequestTimeoutSeconds = 0 }));
    }
}
