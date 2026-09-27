// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json;
using Bindery.Ra2.Bot.Claude;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Claude;

public sealed class OpenAiCompatibleMessageClientTests
{
    private sealed class Recorder(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public JsonDocument? Sent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            Sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static ModelRequest Request(int maxTokens = 500) => new(
        "claude-opus-5", "system text", [new PromptBlock("part one ", true), new PromptBlock("part two", false)],
        """{"type":"object","additionalProperties":false,"required":["a"],"properties":{"a":{"type":"string"}}}""",
        "low", maxTokens, true, true);

    [Fact]
    public async Task Sends_the_configured_model_and_a_strict_schema_and_maps_the_reply()
    {
        Recorder handler = new(HttpStatusCode.OK, """
            {"model":"worker-fast","choices":[{"message":{"content":"{\"a\":\"x\"}"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":120,"completion_tokens":30,"prompt_tokens_details":{"cached_tokens":20}}}
            """);
        using OpenAiCompatibleMessageClient client = new(new Uri("http://127.0.0.1:8020/v1"), "worker-fast", handler: handler);

        ModelReply reply = await client.CompleteAsync(Request(), CancellationToken.None);

        Assert.Equal("/v1/chat/completions", handler.Path);
        JsonElement sent = handler.Sent!.RootElement;
        Assert.Equal("worker-fast", sent.GetProperty("model").GetString());
        Assert.Equal("system text", sent.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("part one part two", sent.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.True(sent.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.False(sent.TryGetProperty("effort", out _));
        Assert.Equal("{\"a\":\"x\"}", reply.Text);
        Assert.Equal("end_turn", reply.StopReason);
        Assert.Equal("worker-fast", reply.ModelId);
        Assert.Equal(new ModelUsage(100, 30, 20, 0).InputTokens, reply.Usage.InputTokens);
        Assert.Equal(20, reply.Usage.CacheReadTokens);
    }

    [Theory]
    [InlineData("length", "max_tokens")]
    [InlineData("content_filter", "refusal")]
    public async Task Finish_reasons_map_onto_the_stop_reasons_the_strategist_checks(string finish, string stop)
    {
        Recorder handler = new(HttpStatusCode.OK, $$"""{"choices":[{"message":{"content":""},"finish_reason":"{{finish}}"}]}""");
        using OpenAiCompatibleMessageClient client = new(new Uri("http://h/v1/"), "m", handler: handler);

        Assert.Equal(stop, (await client.CompleteAsync(Request(), CancellationToken.None)).StopReason);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, ModelFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.Unauthorized, ModelFailureKind.Unauthorized)]
    [InlineData(HttpStatusCode.BadGateway, ModelFailureKind.ServerError)]
    [InlineData(HttpStatusCode.BadRequest, ModelFailureKind.InvalidRequest)]
    public async Task Http_failures_become_typed_model_client_exceptions(HttpStatusCode status, ModelFailureKind kind)
    {
        Recorder handler = new(status, """{"error":"nope"}""");
        using OpenAiCompatibleMessageClient client = new(new Uri("http://h/v1"), "m", handler: handler);

        ModelClientException ex = await Assert.ThrowsAsync<ModelClientException>(() => client.CompleteAsync(Request(), CancellationToken.None));
        Assert.Equal(kind, ex.Kind);
        Assert.Equal((int)status, ex.StatusCode);
    }
}
