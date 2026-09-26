// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Claude;

/// <summary>
/// One text block of the user turn. <see cref="CacheBreakpoint"/> marks the end
/// of a prefix that is byte-identical across calls in a match (catalogue, rule
/// facts), so the API can serve it from the prompt cache; per-snapshot content
/// goes after the last breakpoint.
/// </summary>
public sealed record PromptBlock(string Text, bool CacheBreakpoint);

/// <summary>
/// A provider-neutral description of one structured-output completion. The
/// strategist builds this; an <see cref="IMessageClient"/> turns it into an SDK
/// call. Keeping it SDK-free lets tests run the whole strategist on canned replies.
/// </summary>
/// <param name="Model">Model id, e.g. <c>claude-opus-5</c>.</param>
/// <param name="SystemPrompt">Byte-stable system prompt; always sent first and cached.</param>
/// <param name="UserContent">User turn blocks, stable prefix first.</param>
/// <param name="JsonSchema">JSON schema (an object) the reply must satisfy, serialised as JSON text.</param>
/// <param name="Effort">Effort level (<c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c>, <c>max</c>), or null to omit it (models without effort support).</param>
/// <param name="MaxTokens">Output token ceiling, including thinking.</param>
/// <param name="AdaptiveThinking">Send <c>thinking: {type: "adaptive"}</c>; false for models that do not support it.</param>
/// <param name="ServerFallbacks">Opt into server-side refusal fallbacks (<c>fallbacks: "default"</c>).</param>
public sealed record ModelRequest(
    string Model,
    string SystemPrompt,
    IReadOnlyList<PromptBlock> UserContent,
    string JsonSchema,
    string? Effort,
    int MaxTokens,
    bool AdaptiveThinking,
    bool ServerFallbacks);

/// <summary>Token accounting for one reply, as reported by the API.</summary>
public sealed record ModelUsage(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheCreationTokens)
{
    public static readonly ModelUsage None = new(0, 0, 0, 0);
}

/// <summary>
/// A completed API response, before any interpretation. <see cref="StopReason"/>
/// must be checked before <see cref="Text"/> is trusted: a refusal or a
/// max-tokens cut can leave partial or empty text.
/// </summary>
/// <param name="Text">Concatenated text blocks (thinking blocks excluded).</param>
/// <param name="StopReason">Wire stop reason (<c>end_turn</c>, <c>refusal</c>, <c>max_tokens</c>, ...).</param>
/// <param name="StopDetail">Refusal category/explanation when the API gives one; informational only.</param>
/// <param name="Usage">Tokens billed for the whole request: with server-side fallbacks, every attempt (usage.iterations), not only the one that produced this reply.</param>
/// <param name="ModelId">Model that actually served the reply (differs from the request when a fallback served it).</param>
public sealed record ModelReply(string Text, string StopReason, string? StopDetail, ModelUsage Usage, string? ModelId);

/// <summary>Why a model call failed before producing a reply.</summary>
public enum ModelFailureKind
{
    /// <summary>HTTP 429: back off; the next cadence tick retries naturally.</summary>
    RateLimited,

    /// <summary>HTTP 5xx or overloaded: transient server-side failure.</summary>
    ServerError,

    /// <summary>Network or connection failure before a status code.</summary>
    Connection,

    /// <summary>A 4xx other than 429: a request-shape or credential problem that retries will not fix.</summary>
    InvalidRequest,

    /// <summary>Authentication or permission failure (401/403).</summary>
    Unauthorized,

    /// <summary>Anything else the client could not classify.</summary>
    Unknown,
}

/// <summary>
/// The one exception type an <see cref="IMessageClient"/> throws for API
/// failures, so the strategist classifies failures without depending on SDK
/// exception types (and test fakes can raise every class).
/// </summary>
public sealed class ModelClientException : Exception
{
    public ModelClientException(ModelFailureKind kind, string message, int? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public ModelFailureKind Kind { get; }

    public int? StatusCode { get; }
}

/// <summary>
/// The seam between the strategist and the Anthropic SDK. Implementations throw
/// <see cref="ModelClientException"/> for API failures and
/// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/>
/// fires; they never interpret the reply.
/// </summary>
public interface IMessageClient
{
    Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Per-model feature support that changes the request shape. Haiku 4.5 predates
/// the effort parameter and adaptive thinking; sending either is a 400, so the
/// strategist omits them for it. Unknown ids are assumed current-generation.
/// </summary>
public static class ModelCapabilities
{
    public static bool SupportsEffort(string model) => !IsLegacySmallModel(model);

    public static bool SupportsAdaptiveThinking(string model) => !IsLegacySmallModel(model);

    /// <summary>
    /// Server-side <c>fallbacks: "default"</c> exists for the 5-generation models,
    /// whose safety classifiers can decline a request. Older models have no
    /// default fallback configuration, so the flag is not sent for them.
    /// </summary>
    public static bool SupportsServerFallbacks(string model) =>
        model.StartsWith("claude-opus-5", StringComparison.Ordinal)
        || model.StartsWith("claude-sonnet-5", StringComparison.Ordinal)
        || model.StartsWith("claude-fable-5", StringComparison.Ordinal)
        || model.StartsWith("claude-mythos-5", StringComparison.Ordinal);

    private static bool IsLegacySmallModel(string model) =>
        model.StartsWith("claude-haiku-4", StringComparison.Ordinal)
        || model.StartsWith("claude-3", StringComparison.Ordinal);
}
