// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bindery.Ra2.Bot.Claude;

/// <summary>
/// <see cref="IMessageClient"/> over an OpenAI-compatible <c>/chat/completions</c> endpoint, so the strategist can run
/// on a local model (llama-swap at <c>http://127.0.0.1:8020/v1</c>) or any hosted OpenAI-compatible service for bulk
/// validation without an Anthropic credential.
/// </summary>
/// <remarks>
/// The configured <see cref="Model"/> replaces the Claude model id in the request; the reply reports the model the
/// server says it used, so decision logs and cost accounting never attribute a local model's answer to Claude.
/// Effort, adaptive thinking and server fallbacks are Anthropic features and are not sent. The JSON schema goes out as
/// a strict <c>response_format</c>; <c>finish_reason</c> maps onto the Anthropic stop reasons the strategist checks
/// (<c>stop</c> → <c>end_turn</c>, <c>length</c> → <c>max_tokens</c>, <c>content_filter</c> → <c>refusal</c>).
/// Prompt blocks are concatenated: cache breakpoints have no equivalent here.
/// </remarks>
public sealed class OpenAiCompatibleMessageClient : IMessageClient, IDisposable
{
    private readonly HttpClient http;
    private readonly bool ownsHttp;

    public OpenAiCompatibleMessageClient(Uri baseUrl, string model, string? apiKey = null, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        Model = model;
        http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        ownsHttp = true;
        http.BaseAddress = new Uri(baseUrl.ToString().TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromMinutes(5);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("bindery-ra2-bot-strategist/1.0");
        if (!string.IsNullOrEmpty(apiKey)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    /// <summary>The model id sent to the endpoint for every request.</summary>
    public string Model { get; }

    public async Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        JsonObject body = new()
        {
            ["model"] = Model,
            ["max_tokens"] = request.MaxTokens,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = string.Concat(request.UserContent.Select(static b => b.Text)) },
            },
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "intent",
                    ["strict"] = true,
                    ["schema"] = JsonNode.Parse(request.JsonSchema),
                },
            },
        };

        HttpResponseMessage response;
        try
        {
            using StringContent content = new(body.ToJsonString(), Encoding.UTF8, "application/json");
            response = await http.PostAsync("chat/completions", content, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ModelClientException(ModelFailureKind.Connection, ex.Message, null, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelClientException(ModelFailureKind.Connection, "request timed out", null, ex);
        }

        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode;
                ModelFailureKind kind = response.StatusCode switch
                {
                    HttpStatusCode.TooManyRequests => ModelFailureKind.RateLimited,
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ModelFailureKind.Unauthorized,
                    _ when status >= 500 => ModelFailureKind.ServerError,
                    _ when status >= 400 => ModelFailureKind.InvalidRequest,
                    _ => ModelFailureKind.Unknown,
                };
                throw new ModelClientException(kind, $"HTTP {status}: {Truncate(text)}", status);
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(text);
                JsonElement root = doc.RootElement;
                JsonElement choice = root.GetProperty("choices")[0];
                string reply = choice.GetProperty("message").TryGetProperty("content", out JsonElement c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "";
                string finish = choice.TryGetProperty("finish_reason", out JsonElement f) && f.ValueKind == JsonValueKind.String ? f.GetString()! : "";
                string stop = finish switch
                {
                    "stop" => "end_turn",
                    "length" => "max_tokens",
                    "content_filter" => "refusal",
                    _ => finish,
                };
                long input = 0, output = 0, cached = 0;
                if (root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    input = usage.TryGetProperty("prompt_tokens", out JsonElement p) ? p.GetInt64() : 0;
                    output = usage.TryGetProperty("completion_tokens", out JsonElement o) ? o.GetInt64() : 0;
                    if (usage.TryGetProperty("prompt_tokens_details", out JsonElement d) && d.ValueKind == JsonValueKind.Object
                        && d.TryGetProperty("cached_tokens", out JsonElement ct) && ct.ValueKind == JsonValueKind.Number)
                        cached = ct.GetInt64();
                }
                string served = root.TryGetProperty("model", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : Model;
                ModelUsage modelUsage = new(input - cached, output, cached, 0, [new ModelAttemptUsage(served, input - cached, output, cached, 0)]);
                return new ModelReply(reply, stop, finish == "stop" ? null : finish, modelUsage, served);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
            {
                throw new ModelClientException(ModelFailureKind.Unknown, $"unreadable response: {Truncate(text)}", (int)response.StatusCode, ex);
            }
        }
    }

    public void Dispose()
    {
        if (ownsHttp) http.Dispose();
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
