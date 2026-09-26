// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Beta = Anthropic.Models.Beta.Messages;
using Msg = Anthropic.Models.Messages;

namespace Bindery.Ra2.Bot.Claude;

/// <summary>
/// <see cref="IMessageClient"/> over the official Anthropic C# SDK. Requests
/// without server fallbacks use the GA <c>client.Messages</c> path; requests
/// with <see cref="ModelRequest.ServerFallbacks"/> use <c>client.Beta.Messages</c>
/// with the <c>server-side-fallback-2026-07-01</c> beta and <c>fallbacks: "default"</c>,
/// so a classifier refusal is re-served by Anthropic's recommended fallback model
/// inside the same call instead of costing the bot a whole strategic cadence.
/// SDK exceptions are translated to <see cref="ModelClientException"/>.
/// </summary>
public sealed class AnthropicMessageClient : IMessageClient
{
    private readonly IAnthropicClient client;

    /// <summary>Uses an <see cref="AnthropicClient"/> resolving credentials from the environment (<c>ANTHROPIC_API_KEY</c>).</summary>
    public AnthropicMessageClient()
        : this(new AnthropicClient())
    {
    }

    public AnthropicMessageClient(IAnthropicClient client)
    {
        this.client = client;
    }

    public async Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        Dictionary<string, JsonElement> schema = ParseSchema(request.JsonSchema);
        try
        {
            return request.ServerFallbacks
                ? await CompleteBetaAsync(request, schema, cancellationToken).ConfigureAwait(false)
                : await CompleteGaAsync(request, schema, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            // The SDK can surface a cancelled request as an IO error; the caller's
            // token is the truth about why the call ended.
            throw new OperationCanceledException("Model request cancelled.", ex, cancellationToken);
        }
        catch (AnthropicRateLimitException ex)
        {
            throw new ModelClientException(ModelFailureKind.RateLimited, ex.Message, 429, ex);
        }
        catch (Anthropic5xxException ex)
        {
            throw new ModelClientException(ModelFailureKind.ServerError, ex.Message, null, ex);
        }
        catch (AnthropicIOException ex)
        {
            throw new ModelClientException(ModelFailureKind.Connection, ex.Message, null, ex);
        }
        catch (AnthropicUnauthorizedException ex)
        {
            throw new ModelClientException(ModelFailureKind.Unauthorized, ex.Message, 401, ex);
        }
        catch (AnthropicForbiddenException ex)
        {
            throw new ModelClientException(ModelFailureKind.Unauthorized, ex.Message, 403, ex);
        }
        catch (Anthropic4xxException ex)
        {
            throw new ModelClientException(ModelFailureKind.InvalidRequest, ex.Message, null, ex);
        }
        catch (AnthropicException ex)
        {
            throw new ModelClientException(ModelFailureKind.Unknown, ex.Message, null, ex);
        }
    }

    private async Task<ModelReply> CompleteGaAsync(ModelRequest request, Dictionary<string, JsonElement> schema, CancellationToken cancellationToken)
    {
        Msg.MessageCreateParams parameters = BuildGaParams(request, schema);
        Msg.Message message = await client.Messages.Create(parameters, cancellationToken).ConfigureAwait(false);

        StringBuilder text = new();
        foreach (Msg.TextBlock block in message.Content.Select(b => b.Value).OfType<Msg.TextBlock>())
        {
            text.Append(block.Text);
        }

        string? detail = message.StopDetails is { } d ? $"{d.Category?.Raw()}: {d.Explanation}" : null;
        ModelUsage usage = new(
            message.Usage.InputTokens,
            message.Usage.OutputTokens,
            message.Usage.CacheReadInputTokens ?? 0,
            message.Usage.CacheCreationInputTokens ?? 0);
        return new ModelReply(text.ToString(), message.StopReason?.Raw() ?? string.Empty, detail, usage, message.Model.Raw());
    }

    /// <summary>GA request: system prompt cached, stable user prefix cached, JSON schema output, optional effort and adaptive thinking.</summary>
    internal static Msg.MessageCreateParams BuildGaParams(ModelRequest request, Dictionary<string, JsonElement> schema)
    {
        List<Msg.ContentBlockParam> content = new();
        foreach (PromptBlock block in request.UserContent)
        {
            content.Add(block.CacheBreakpoint
                ? new Msg.TextBlockParam { Text = block.Text, CacheControl = new Msg.CacheControlEphemeral() }
                : new Msg.TextBlockParam { Text = block.Text });
        }

        Msg.OutputConfig output = request.Effort is { } effort
            ? new Msg.OutputConfig { Format = new Msg.JsonOutputFormat { Schema = schema }, Effort = GaEffort(effort) }
            : new Msg.OutputConfig { Format = new Msg.JsonOutputFormat { Schema = schema } };

        Msg.MessageCreateParams parameters = new()
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            System = new List<Msg.TextBlockParam>
            {
                new() { Text = request.SystemPrompt, CacheControl = new Msg.CacheControlEphemeral() },
            },
            Messages = [new() { Role = Msg.Role.User, Content = content }],
            OutputConfig = output,
        };
        return request.AdaptiveThinking ? parameters with { Thinking = new Msg.ThinkingConfigAdaptive() } : parameters;
    }

    private async Task<ModelReply> CompleteBetaAsync(ModelRequest request, Dictionary<string, JsonElement> schema, CancellationToken cancellationToken)
    {
        Beta.MessageCreateParams parameters = BuildBetaParams(request, schema);
        Beta.BetaMessage message = await client.Beta.Messages.Create(parameters, cancellationToken).ConfigureAwait(false);

        StringBuilder text = new();
        foreach (Beta.BetaTextBlock block in message.Content.Select(b => b.Value).OfType<Beta.BetaTextBlock>())
        {
            text.Append(block.Text);
        }

        string? detail = message.StopDetails is { } d ? $"{d.Category?.Raw()}: {d.Explanation}" : null;
        return new ModelReply(text.ToString(), message.StopReason?.Raw() ?? string.Empty, detail, UsageOf(message.Usage), message.Model.Raw());
    }

    /// <summary>
    /// Tokens billed for the whole request. With server-side fallbacks the top-level usage covers only the attempt
    /// that produced the returned message; a declined attempt (billed at normal rates when it declines mid-output)
    /// appears only in <c>usage.iterations</c>, the per-attempt source of truth, so the iterations are summed when
    /// present.
    /// </summary>
    private static string? ModelOf(string? model) => string.IsNullOrEmpty(model) ? null : model;

    internal static ModelUsage UsageOf(Beta.BetaUsage usage)
    {
        if (usage.Iterations is not { Count: > 0 } iterations)
        {
            return new ModelUsage(usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens ?? 0, usage.CacheCreationInputTokens ?? 0);
        }
        long input = 0, output = 0, read = 0, write = 0;
        List<ModelAttemptUsage> attempts = [];
        foreach (Beta.BetaUsageIteration iteration in iterations)
        {
            (string? model, long i, long o, long r, long w) = iteration.Match<(string?, long, long, long, long)>(
                static m => (m.Model is { } message ? ModelOf(message) : null, m.InputTokens, m.OutputTokens, m.CacheReadInputTokens, m.CacheCreationInputTokens),
                static c => (null, c.InputTokens, c.OutputTokens, c.CacheReadInputTokens, c.CacheCreationInputTokens),
                static a => (a.Model is { } advisor ? ModelOf(advisor) : null, a.InputTokens, a.OutputTokens, a.CacheReadInputTokens, a.CacheCreationInputTokens),
                static f => (f.Model is { } fallback ? ModelOf(fallback) : null, f.InputTokens, f.OutputTokens, f.CacheReadInputTokens, f.CacheCreationInputTokens));
            input += i;
            output += o;
            read += r;
            write += w;
            attempts.Add(new ModelAttemptUsage(model, i, o, r, w));
        }
        return new ModelUsage(input, output, read, write, attempts);
    }

    /// <summary>Beta request: as <see cref="BuildGaParams"/> plus <c>fallbacks: "default"</c> under the <c>server-side-fallback-2026-07-01</c> beta.</summary>
    internal static Beta.MessageCreateParams BuildBetaParams(ModelRequest request, Dictionary<string, JsonElement> schema)
    {
        List<Beta.BetaContentBlockParam> content = new();
        foreach (PromptBlock block in request.UserContent)
        {
            content.Add(block.CacheBreakpoint
                ? new Beta.BetaTextBlockParam { Text = block.Text, CacheControl = new Beta.BetaCacheControlEphemeral() }
                : new Beta.BetaTextBlockParam { Text = block.Text });
        }

        Beta.BetaOutputConfig output = request.Effort is { } effort
            ? new Beta.BetaOutputConfig { Format = new Beta.BetaJsonOutputFormat { Schema = schema }, Effort = BetaEffort(effort) }
            : new Beta.BetaOutputConfig { Format = new Beta.BetaJsonOutputFormat { Schema = schema } };

        Beta.MessageCreateParams parameters = new()
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Beta.Default(),
            System = new List<Beta.BetaTextBlockParam>
            {
                new() { Text = request.SystemPrompt, CacheControl = new Beta.BetaCacheControlEphemeral() },
            },
            Messages = [new() { Role = Beta.Role.User, Content = content }],
            OutputConfig = output,
        };
        return request.AdaptiveThinking ? parameters with { Thinking = new Beta.BetaThinkingConfigAdaptive() } : parameters;
    }

    internal static Dictionary<string, JsonElement> ParseSchema(string schemaJson)
    {
        using JsonDocument document = JsonDocument.Parse(schemaJson);
        Dictionary<string, JsonElement> schema = new(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            schema[property.Name] = property.Value.Clone();
        }
        return schema;
    }

    private static Msg.Effort GaEffort(string effort) => effort switch
    {
        "low" => Msg.Effort.Low,
        "medium" => Msg.Effort.Medium,
        "high" => Msg.Effort.High,
        "xhigh" => Msg.Effort.Xhigh,
        "max" => Msg.Effort.Max,
        _ => throw new ModelClientException(ModelFailureKind.InvalidRequest, $"Unknown effort '{effort}'."),
    };

    private static Beta.Effort BetaEffort(string effort) => effort switch
    {
        "low" => Beta.Effort.Low,
        "medium" => Beta.Effort.Medium,
        "high" => Beta.Effort.High,
        "xhigh" => Beta.Effort.Xhigh,
        "max" => Beta.Effort.Max,
        _ => throw new ModelClientException(ModelFailureKind.InvalidRequest, $"Unknown effort '{effort}'."),
    };
}
