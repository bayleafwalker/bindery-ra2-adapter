// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using Bindery.Ra2.Adapter.Channel;
using Bindery.Ra2.Adapter.Planner.Claude;
using Xunit;

namespace Bindery.Ra2.Adapter.Tests;

public sealed class ClaudePlannerTests
{
    private static readonly PlanningRequest request = new(
        StalledEconomyTrigger.Name,
        new Playbook(2, "economy=build_refineries, posture=scout, tech=hold", new Dictionary<string, string> { ["economy"] = "build_refineries", ["posture"] = "scout", ["tech"] = "hold" }),
        new PlayerViewSummary("Americans", TimeSpan.FromSeconds(240), 800, -200, new Dictionary<string, int> { ["MTNK"] = 3 }, ["GAREFN", "GAPOWR"], 4, []));

    private const string Answer = "{\"directives\":{\"economy\":\"expand\",\"posture\":\"defend\",\"tech\":\"hold\"},\"summary\":\"Take a second ore field while holding the base.\",\"rationale\":\"Credits fell 200 over the window with only one refinery.\"}";

    [Fact]
    public void ParseBuildsTheNextRevisionFromTheFixedVocabulary()
    {
        Playbook next = PlannerPrompt.Parse(Answer, request.Current);

        Assert.Equal(3, next.Revision);
        Assert.Equal("expand", next.Directive("economy"));
        Assert.Equal("defend", next.Directive("posture"));
        Assert.StartsWith("Take a second ore field", next.Text, StringComparison.Ordinal);
        Assert.Contains("economy=expand, posture=defend, tech=hold", next.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"summary\":\"no directives\"}")]
    [InlineData("{\"directives\":{\"economy\":\"expand\",\"posture\":\"nuke_everything\",\"tech\":\"hold\"}}")]
    [InlineData("{\"directives\":{\"economy\":\"expand\",\"posture\":\"defend\"}}")]
    public void ParseRefusesAnythingTheRoutineControllerCannotRead(string text)
    {
        Assert.ThrowsAny<Exception>(() => PlannerPrompt.Parse(text, request.Current));
    }

    [Fact]
    public void ThePromptCarriesOnlyThePlayersOwnView()
    {
        using JsonDocument user = JsonDocument.Parse(PlannerPrompt.User(request));
        JsonElement player = user.RootElement.GetProperty("player");
        Assert.Equal("Americans", player.GetProperty("house").GetString());
        Assert.Equal(-200, player.GetProperty("credits_change_over_window").GetInt64());
        Assert.Equal(4, player.GetProperty("enemy_sightings").GetInt32());
        Assert.Equal(["house", "elapsed_seconds", "credits", "credits_change_over_window", "own_units", "own_building_types", "enemy_sightings", "defeated_houses"],
            player.EnumerateObject().Select(static p => p.Name));

        JsonElement posture = JsonSerializer.SerializeToElement(PlannerPrompt.Schema())
            .GetProperty("properties").GetProperty("directives").GetProperty("properties").GetProperty("posture");
        Assert.Equal(["scout", "defend", "pressure", "attack"], posture.GetProperty("enum").EnumerateArray().Select(static e => e.GetString()));
        Assert.DoesNotContain("{", PlannerPrompt.System, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRequestUsesStructuredOutputAndServerSideFallbacks()
    {
        CannedHandler handler = new(Message("end_turn", Answer));
        ClaudePlaybookPlanner planner = new(Client(handler));

        Playbook next = await planner.PlanAsync(request, CancellationToken.None);

        Assert.Equal("expand", next.Directive("economy"));
        using JsonDocument body = JsonDocument.Parse(handler.Body!);
        JsonElement root = body.RootElement;
        Assert.Equal("claude-opus-5", root.GetProperty("model").GetString());
        Assert.Equal("default", root.GetProperty("fallbacks").GetString());
        Assert.Equal("json_schema", root.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
        Assert.Equal("medium", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.False(root.TryGetProperty("thinking", out _));
        Assert.Contains("server-side-fallback-2026-07-01", handler.Beta, StringComparison.Ordinal);
        Assert.Equal("claude/claude-opus-5", planner.PlannerId);
    }

    [Fact]
    public async Task ARefusalKeepsTheCurrentPlaybook()
    {
        ClaudePlaybookPlanner planner = new(Client(new CannedHandler(Message("refusal", ""))));
        PlaybookController controller = new("Americans", planner);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => planner.PlanAsync(request, CancellationToken.None));
        Assert.Contains("declined", error.Message, StringComparison.Ordinal);

        ControllerStep step = await controller.ObserveAsync(ChannelTests.Observation(Ra2TelemetryEventTypes.MatchStarted, "{}"), CancellationToken.None);
        await controller.SettleAsync();
        ControllerStep next = await controller.ObserveAsync(ChannelTests.Observation(Ra2TelemetryEventTypes.CreditsSampled, "{\"house\":\"Americans\",\"credits\":1}"), CancellationToken.None);
        Assert.Null(step.Revision ?? next.Revision);
        Assert.Contains((step.Notes ?? []).Concat(next.Notes ?? []), static n => n.Contains("planner failed on opening", StringComparison.Ordinal));
        Assert.Equal(0, controller.Current.Revision);
    }

    private static AnthropicClient Client(HttpMessageHandler handler) => new()
    {
        ApiKey = "test-key",
        BaseUrl = "https://api.anthropic.test",
        HttpClient = new HttpClient(handler),
        MaxRetries = 0,
    };

    private static string Message(string stopReason, string text) => JsonSerializer.Serialize(new
    {
        id = "msg_test",
        type = "message",
        role = "assistant",
        model = "claude-opus-5",
        content = new[] { new { type = "text", text } },
        stop_reason = stopReason,
        stop_sequence = (string?)null,
        stop_details = stopReason == "refusal" ? new { type = "refusal", category = "cyber", explanation = "declined" } : null,
        usage = new { input_tokens = 10, output_tokens = 20 },
    });

    private sealed class CannedHandler(string response) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        public string Beta { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Beta = request.Headers.TryGetValues("anthropic-beta", out IEnumerable<string>? values) ? string.Join(",", values) : string.Empty;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
