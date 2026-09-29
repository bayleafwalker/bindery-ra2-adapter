// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json;
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Claude;
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

    // The shapes the real Claude path writes: the scheduler's Primary record says no_opinion with no message; the
    // strategist's own record has no role and carries the error as code and detail.
    private static DecisionRecord SchedulerFailure(string role) =>
        new(DecisionRecordKinds.ProposalFailed, new GameTime(0), 1, JsonSerializer.SerializeToElement(new { role, strategistId = "s", reason = "no_opinion", message = (string?)null }));

    private static DecisionRecord StrategistFailure(string detail) =>
        new(DecisionRecordKinds.ProposalFailed, new GameTime(0), 1, JsonSerializer.SerializeToElement(new { strategist = "s", code = "invalid_request", detail }));

    private static DecisionRecord Proposal(string role) =>
        new(DecisionRecordKinds.Proposal, new GameTime(0), 1, JsonSerializer.SerializeToElement(new { role }));

    [Fact]
    public void Only_failed_Primary_proposals_give_a_skip_reason_with_the_first_error_truncated()
    {
        string longError = "HTTP 400: anthropic-workspace-id header required " + new string('x', 400);
        List<DecisionRecord> log =
        [
            StrategistFailure(longError),
            SchedulerFailure("Primary"),
            StrategistFailure("second"),
            SchedulerFailure("Primary"),
            Proposal("Shadow"),
        ];

        string? reason = Program.AllPrimaryProposalsFailed(log, "llm-t3");

        Assert.NotNull(reason);
        Assert.StartsWith("skipped: llm-t3: 0 of 2 model proposals succeeded in the first match (HTTP 400: anthropic-workspace-id", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("second", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("no_opinion", reason, StringComparison.Ordinal);
        Assert.True(reason.Length < 340, reason);
    }

    [Fact]
    public void One_successful_Primary_proposal_lets_the_arm_proceed()
    {
        List<DecisionRecord> log = [StrategistFailure("boom"), SchedulerFailure("Primary"), Proposal("Primary")];

        Assert.Null(Program.AllPrimaryProposalsFailed(log));
    }

    [Fact]
    public void Failed_Shadow_proposals_alone_or_an_empty_log_do_not_skip()
    {
        Assert.Null(Program.AllPrimaryProposalsFailed([StrategistFailure("boom"), SchedulerFailure("Shadow")]));
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
        string? skipReason = skipped.GetProperty("reason").GetString();
        Assert.Contains("model proposals succeeded in the first match", skipReason, StringComparison.Ordinal);
        Assert.DoesNotContain("no_opinion", skipReason, StringComparison.Ordinal);
        // The real connection error, not just the prefix.
        Assert.Matches("(?i)refused|connect|unreachable", skipReason);
        string report = File.ReadAllText(Path.Combine(dir, "report.md"));
        Assert.Contains("## Skipped arms", report, StringComparison.Ordinal);
        Assert.Contains("model proposals succeeded in the first match", report, StringComparison.Ordinal);
    }

    /// <summary>A local OpenAI-compatible endpoint that answers like the fake client, or fails with HTTP 400 while told to.</summary>
    private sealed class Endpoint : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly FakeMessageClient fake = new();
        public volatile bool Failing = true;
        public string Url { get; }

        public Endpoint()
        {
            int port;
            using (System.Net.Sockets.TcpListener probe = new(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            Url = $"http://127.0.0.1:{port}/v1/";
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            _ = Task.Run(Serve);
        }

        private async Task Serve()
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { return; }
                _ = Task.Run(async () => { try { await Answer(ctx); } catch (Exception ex) { ctx.Response.StatusCode = 500; byte[] b = Encoding.UTF8.GetBytes(ex.ToString()); ctx.Response.OutputStream.Write(b); ctx.Response.Close(); } });
            }
        }

        private async Task Answer(HttpListenerContext ctx)
        {
            string text;
            int status = 200;
            if (Failing)
            {
                status = 400;
                text = "{\"error\":\"missing anthropic-workspace-id header\"}";
            }
            else
            {
                using StreamReader reader = new(ctx.Request.InputStream);
                using JsonDocument body = JsonDocument.Parse(await reader.ReadToEndAsync());
                JsonElement messages = body.RootElement.GetProperty("messages");
                ModelRequest request = new(
                    "m", messages[0].GetProperty("content").GetString()!, SplitBlocks(messages[1].GetProperty("content").GetString()!),
                    body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema").GetRawText(),
                    null, body.RootElement.GetProperty("max_tokens").GetInt32(), false, false);
                ModelReply reply = await fake.CompleteAsync(request, CancellationToken.None);
                text = JsonSerializer.Serialize(new
                {
                    model = "fake",
                    choices = new[] { new { message = new { content = reply.Text }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 10, completion_tokens = 5 },
                });
            }
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }

        // The client joins the strategist's two JSON blocks (match context, situation); the fake reads them apart.
        private static List<PromptBlock> SplitBlocks(string joined)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(joined);
            Utf8JsonReader reader = new(bytes, isFinalBlock: false, default);
            do { reader.Read(); } while (reader.CurrentDepth > 0 || reader.TokenType is not (JsonTokenType.EndObject or JsonTokenType.EndArray));
            int end = (int)reader.BytesConsumed;
            return [new PromptBlock(Encoding.UTF8.GetString(bytes, 0, end), true), new PromptBlock(Encoding.UTF8.GetString(bytes, end, bytes.Length - end), false)];
        }

        public void Dispose() => listener.Close();
    }

    private sealed class FlipOnSkip(Endpoint endpoint, TextWriter inner) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;
        public override void Write(char value) => inner.Write(value);
        public override void WriteLine(string? value)
        {
            // The first arm's skip line is printed before the next arm starts: from then on the endpoint answers.
            if (value is not null && value.Contains("skipped:", StringComparison.Ordinal)) endpoint.Failing = false;
            inner.WriteLine(value);
        }
    }

    [Fact]
    public void An_all_failed_arm_is_skipped_without_skipping_the_next_llm_arm()
    {
        using Endpoint endpoint = new();
        TextWriter original = Console.Out;
        Console.SetOut(new FlipOnSkip(endpoint, original));
        try
        {
            Assert.Equal(0, Program.Main(["run", "--arms", "llm,llm-t1", "--maps", "training", "--opponents", "live-rush", "--seeds", "1",
                "--max-seconds", "60", "--llm-endpoint", endpoint.Url, "--no-decisions", "--out", dir]));
        }
        finally
        {
            Console.SetOut(original);
        }

        using JsonDocument probes = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "probes.json")));
        JsonElement skipped = Assert.Single(probes.RootElement.GetProperty("skipped").EnumerateArray());
        Assert.Equal("llm", skipped.GetProperty("arm").GetString());
        Assert.Contains("HTTP 400", skipped.GetProperty("reason").GetString(), StringComparison.Ordinal);
        using JsonDocument results = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "results.json")));
        List<string?> arms = [.. results.RootElement.EnumerateArray().Select(static m => m.GetProperty("arm").GetString()).Distinct()];
        Assert.Equal(["llm-t1"], arms);
    }
}
