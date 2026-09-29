// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bindery.Ra2.Bot.Claude;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Claude;

public sealed class ExtendedMetricsPromptTests
{
    // SHA-256 of the artefacts as they were before the extended metrics existed (captured on main at 8f8b515).
    private const string GoldenSchema = "30B6890F3752FCFC0674A7EB6F31514363F0FADD53CA869053BA7DFB98F21D8F";
    private const string GoldenSystemStrategic = "2DC438AEEAA2A7C0A56CBB6BA416B9E8D5B1E2A443DBEDE258129D43F2750A67";
    private const string GoldenSystemRefine = "4C34996EE130498081291BA4B35E055E870B77A945FB938DA22900F000F6B614";
    private const string GoldenConditionMetrics = "46493B35E3C3CF7D79CC80B35BAD0D372EF4BB7E09AF25DAD14B26ECF9F62F4F";

    private static readonly string[] NewNames = [.. ConditionMetrics.Extended.Select(m => m.ToString())];

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string ConditionMetricsJson(string situation)
    {
        using JsonDocument doc = JsonDocument.Parse(situation);
        return doc.RootElement.GetProperty("conditionMetrics").GetRawText();
    }

    [Fact]
    public void Default_prompt_and_schema_are_byte_identical_to_before()
    {
        IntentPrompt prompt = new IntentPromptBuilder().Build(ClaudeFixtures.Context(), StrategistMode.Strategic);
        ModelRequest request = new ClaudeStrategist(new NullClient()).BuildRequest(ClaudeFixtures.Context());

        Assert.Equal(GoldenSchema, Hash(IntentDraftSchema.Json));
        Assert.Equal(GoldenSchema, Hash(request.JsonSchema));
        Assert.Equal(GoldenSystemStrategic, Hash(IntentPromptBuilder.SystemPrompt(StrategistMode.Strategic)));
        Assert.Equal(GoldenSystemRefine, Hash(IntentPromptBuilder.SystemPrompt(StrategistMode.Refine)));
        Assert.Equal(GoldenConditionMetrics, Hash(ConditionMetricsJson(prompt.Situation)));
        Assert.Equal(new IntentPromptBuilder().Build(ClaudeFixtures.Context(), StrategistMode.Strategic, null, new ClaudeStrategistOptions().Vocabulary).SystemPrompt, request.SystemPrompt);
        foreach (string name in NewNames)
        {
            Assert.DoesNotContain(name, IntentDraftSchema.Json, StringComparison.Ordinal);
            Assert.DoesNotContain(name, prompt.SystemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain(name, prompt.Situation, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Extended_option_adds_the_metrics_to_prompt_situation_and_schema()
    {
        ClaudeStrategist strategist = new(new NullClient(), new ClaudeStrategistOptions { ExtendedConditionMetrics = true });
        ModelRequest request = strategist.BuildRequest(ClaudeFixtures.Context());
        IntentPrompt prompt = new IntentPromptBuilder().Build(ClaudeFixtures.Context(), StrategistMode.Strategic, extendedMetrics: true);

        Assert.Equal(IntentDraftSchema.JsonExtended, request.JsonSchema);
        using JsonDocument schema = JsonDocument.Parse(request.JsonSchema);
        JsonElement condition = schema.RootElement.GetProperty("properties").GetProperty("abortTriggers").GetProperty("items").GetProperty("properties");
        Assert.Equal(Enum.GetNames<ConditionMetric>(), condition.GetProperty("metric").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray());

        using JsonDocument situation = JsonDocument.Parse(prompt.Situation);
        JsonElement metrics = situation.RootElement.GetProperty("conditionMetrics");
        foreach (string name in NewNames)
        {
            Assert.Contains(name, request.SystemPrompt, StringComparison.Ordinal);
            Assert.True(metrics.TryGetProperty(name, out _), name);
        }
    }

    private sealed class NullClient : IMessageClient
    {
        public Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
