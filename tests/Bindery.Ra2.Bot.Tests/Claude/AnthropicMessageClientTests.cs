// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Claude;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Claude;

/// <summary>Wire shape of the SDK requests, checked offline through the params' raw body.</summary>
public sealed class AnthropicMessageClientTests
{
    private static ModelRequest Request(bool fallbacks, string? effort = "low", bool thinking = true) =>
        new("claude-opus-5", "system", [new PromptBlock("{\"a\":1}", true), new PromptBlock("{\"b\":2}", false)], IntentDraftSchema.Json, effort, 16000, thinking, fallbacks);

    private static Dictionary<string, JsonElement> Body(IReadOnlyDictionary<string, JsonElement> raw) =>
        raw.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    [Fact]
    public void Ga_request_carries_schema_effort_thinking_and_cache_breakpoints()
    {
        ModelRequest request = Request(fallbacks: false);
        Dictionary<string, JsonElement> body = Body(AnthropicMessageClient.BuildGaParams(request, AnthropicMessageClient.ParseSchema(request.JsonSchema)).RawBodyData);

        Assert.Equal("claude-opus-5", body["model"].GetString());
        Assert.Equal("low", body["output_config"].GetProperty("effort").GetString());
        Assert.Equal("json_schema", body["output_config"].GetProperty("format").GetProperty("type").GetString());
        Assert.False(body["output_config"].GetProperty("format").GetProperty("schema").GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("adaptive", body["thinking"].GetProperty("type").GetString());
        Assert.Equal("ephemeral", body["system"][0].GetProperty("cache_control").GetProperty("type").GetString());
        JsonElement content = body["messages"][0].GetProperty("content");
        Assert.True(content[0].TryGetProperty("cache_control", out _));
        Assert.False(content[1].TryGetProperty("cache_control", out _));
        Assert.False(body.ContainsKey("fallbacks"));
    }

    [Fact]
    public void Ga_request_omits_effort_and_thinking_when_not_requested()
    {
        ModelRequest request = Request(fallbacks: false, effort: null, thinking: false);
        Dictionary<string, JsonElement> body = Body(AnthropicMessageClient.BuildGaParams(request, AnthropicMessageClient.ParseSchema(request.JsonSchema)).RawBodyData);

        Assert.False(body["output_config"].TryGetProperty("effort", out _));
        Assert.False(body.ContainsKey("thinking"));
    }

    [Fact]
    public void Beta_request_opts_into_default_server_fallbacks()
    {
        ModelRequest request = Request(fallbacks: true);
        Anthropic.Models.Beta.Messages.MessageCreateParams parameters = AnthropicMessageClient.BuildBetaParams(request, AnthropicMessageClient.ParseSchema(request.JsonSchema));
        Dictionary<string, JsonElement> body = Body(parameters.RawBodyData);

        Assert.Equal("default", body["fallbacks"].GetString());
        Assert.Contains("server-side-fallback-2026-07-01", parameters.Betas!.Select(b => b.Raw()));
        Assert.Equal("low", body["output_config"].GetProperty("effort").GetString());
        Assert.Equal("adaptive", body["thinking"].GetProperty("type").GetString());
    }
}
