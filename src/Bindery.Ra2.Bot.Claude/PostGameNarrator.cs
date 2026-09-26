// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text.Json;
using Bindery.Ra2.Bot.Analysis;

namespace Bindery.Ra2.Bot.Claude;

/// <summary>The outcome of one narration request.</summary>
/// <param name="Narrative">The model's prose, or null when the request failed.</param>
/// <param name="Failure">Why there is no narrative (a <see cref="ClaudeFailureCodes"/> code and detail), or null.</param>
public sealed record NarrationResult(string? Narrative, string? Failure, ProposalCost Cost);

/// <summary>
/// The optional LLM half of post-game analysis: turns a deterministic <see cref="PostGameReport"/> into a short
/// narrative. The report is the source of truth; the model only retells it, and is told to say when the report
/// does not answer something. Structured output (<c>{"narrative": "..."}</c>) keeps the reply parseable, and the
/// request goes through the same <see cref="IMessageClient"/> seam as the strategist, so tests and the arena's
/// <c>--llm-fake</c> run it without a credential.
/// </summary>
public sealed class PostGameNarrator
{
    /// <summary>Byte-stable system prompt.</summary>
    public const string SystemPrompt =
        """
        You are a post-game analyst for a Red Alert 2 / Yuri's Revenge skirmish bot. The user message is the bot's deterministic post-game report as JSON: the timeline of strategic intents (playbook, posture, who proposed it, its rationale, what triggered it, how it ended), the pivots, the proposals that did not take effect and why, key events, and how often a shadow strategist agreed with the active one.

        Write one short narrative (150 to 300 words) of how the match went strategically: what the bot set out to do, when and why it changed course, what it tried that did not take effect, and how the match ended. Use only facts in the report and quote its numbers; when the report does not say something (for example why the enemy did what it did), say that it does not say. Plain prose, no lists, no headings. Reply as JSON matching the schema.
        """;

    /// <summary>The reply schema: one string field.</summary>
    public const string Schema = """{"type":"object","properties":{"narrative":{"type":"string"}},"required":["narrative"],"additionalProperties":false}""";

    private readonly IMessageClient client;
    private readonly string model;
    private readonly string effort;
    private readonly int maxTokens;

    public PostGameNarrator(IMessageClient client, string model = ClaudeStrategistOptions.DefaultStrategicModel, string effort = "low", int maxTokens = 8000)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        this.model = model;
        this.effort = effort;
        this.maxTokens = maxTokens;
    }

    /// <summary>The exact request sent for a report; <paramref name="header"/> is optional match context (arm, opponent, map).</summary>
    public ModelRequest BuildRequest(PostGameReport report, string? header = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        List<PromptBlock> blocks = [];
        if (!string.IsNullOrWhiteSpace(header)) blocks.Add(new PromptBlock(header, false));
        blocks.Add(new PromptBlock(report.ToJson(), false));
        return new ModelRequest(
            model,
            SystemPrompt,
            blocks,
            Schema,
            ModelCapabilities.SupportsEffort(model) ? effort : null,
            maxTokens,
            ModelCapabilities.SupportsAdaptiveThinking(model),
            ModelCapabilities.SupportsServerFallbacks(model));
    }

    /// <summary>Asks for a narrative; never throws for API failures (they come back as <see cref="NarrationResult.Failure"/>).</summary>
    public async Task<NarrationResult> NarrateAsync(PostGameReport report, string? header = null, CancellationToken cancellationToken = default)
    {
        ModelRequest request = BuildRequest(report, header);
        Stopwatch watch = Stopwatch.StartNew();
        ModelReply reply;
        try
        {
            reply = await client.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (ModelClientException ex)
        {
            return new NarrationResult(null, $"{ex.Kind}: {ex.Message}", new ProposalCost(watch.Elapsed.TotalSeconds, 0, 0, 0, model));
        }
        ProposalCost cost = new(watch.Elapsed.TotalSeconds, reply.Usage.InputTokens, reply.Usage.OutputTokens, reply.Usage.CacheReadTokens, reply.ModelId ?? model);
        if (reply.StopReason != "end_turn") return new NarrationResult(null, $"stop_reason {reply.StopReason}: {reply.StopDetail}", cost);
        try
        {
            using JsonDocument document = JsonDocument.Parse(reply.Text);
            if (document.RootElement.TryGetProperty("narrative", out JsonElement narrative) && narrative.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(narrative.GetString()))
            {
                return new NarrationResult(narrative.GetString(), null, cost);
            }
            return new NarrationResult(null, $"{ClaudeFailureCodes.MappingFailed}: no narrative field", cost);
        }
        catch (JsonException ex)
        {
            return new NarrationResult(null, $"{ClaudeFailureCodes.ParseFailed}: {ex.Message}", cost);
        }
    }
}
