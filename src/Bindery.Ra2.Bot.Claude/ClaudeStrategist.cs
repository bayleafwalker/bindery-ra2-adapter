// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using Bindery.Ra2.Bot.Strategy;

namespace Bindery.Ra2.Bot.Claude;

/// <summary>
/// Stable failure codes for a Claude proposal that produced no intent. The
/// scheduler logs them under <see cref="DecisionRecordKinds.ProposalFailed"/>;
/// the arena counts them. Codes never change meaning.
/// </summary>
public static class ClaudeFailureCodes
{
    public const string Refusal = "claude.refusal";
    public const string MaxTokens = "claude.max_tokens";
    public const string UnexpectedStop = "claude.unexpected_stop";
    public const string ParseFailed = "claude.parse_failed";
    public const string MappingFailed = "claude.mapping_failed";
    public const string RefinePlaybookSwitch = "claude.refine_playbook_switch";
    public const string RefineNoActiveIntent = "claude.refine_no_active_intent";
    public const string RefineNotStrategicPlan = "claude.refine_not_strategic_plan";
    public const string Timeout = "claude.timeout";
    public const string Cancelled = "claude.cancelled";
    public const string RateLimited = "claude.rate_limited";
    public const string ServerError = "claude.server_error";
    public const string Connection = "claude.connection";
    public const string InvalidRequest = "claude.invalid_request";
    public const string Unauthorized = "claude.unauthorized";
    public const string ClientError = "claude.client_error";
    public const string PromptFailed = "claude.prompt_failed";
}

/// <summary>
/// Why a <see cref="ClaudeStrategist.ProposeAsync"/> call returned null, with
/// whatever it cost (a refusal or truncated reply still bills tokens, which the
/// inference-cost metric must count).
/// </summary>
/// <param name="Code">One of <see cref="ClaudeFailureCodes"/>.</param>
/// <param name="Detail">Human-readable specifics (parse error, stop details, exception message).</param>
/// <param name="SnapshotVersion">Snapshot the failed proposal was based on.</param>
/// <param name="Time">Game time of that snapshot.</param>
/// <param name="Cost">Tokens and latency spent; zero tokens when no reply arrived.</param>
/// <param name="RawResponse">Model text, when a reply arrived.</param>
public sealed record ClaudeProposalFailure(
    string Code,
    string Detail,
    long SnapshotVersion,
    GameTime Time,
    ProposalCost Cost,
    string? RawResponse)
{
    /// <summary>A decision-log record for this failure, for callers that log failures from the strategist's own account.</summary>
    public DecisionRecord ToDecisionRecord(string strategistId) =>
        new(
            DecisionRecordKinds.ProposalFailed,
            Time,
            SnapshotVersion,
            BotJson.ToElement(new { strategist = strategistId, code = Code, detail = Detail, cost = Cost, rawResponse = RawResponse }));
}

/// <summary>
/// The LLM strategist: builds a fog-safe prompt from the
/// <see cref="StrategistContext"/>, asks Claude for an <see cref="IntentDraft"/>
/// under a JSON schema, and maps it to a <see cref="StrategicIntent"/> proposal.
/// It never throws into the runtime: every failure (refusal, truncation,
/// malformed JSON, timeout, rate limit, server or network error) yields null,
/// and the reason is kept in <see cref="LastFailure"/> and raised through
/// <see cref="ProposalFailed"/>. A returned proposal is still only a proposal;
/// the validator decides whether it takes effect.
/// </summary>
public sealed class ClaudeStrategist : IStrategist
{
    private readonly IMessageClient client;
    private readonly ClaudeStrategistOptions options;
    private readonly IntentPromptBuilder promptBuilder;
    private readonly IntentDraftMapper mapper;
    private readonly object gate = new();
    private ClaudeProposalFailure? lastFailure;
    private string? lastRawResponse;

    /// <param name="client">Model transport; <see cref="AnthropicMessageClient"/> in production, a fake in tests.</param>
    /// <param name="options">Null uses the strategic defaults.</param>
    /// <param name="promptBuilder">Null uses default history/event limits.</param>
    /// <param name="mapper">Null uses default expiry bounds.</param>
    public ClaudeStrategist(
        IMessageClient client,
        ClaudeStrategistOptions? options = null,
        IntentPromptBuilder? promptBuilder = null,
        IntentDraftMapper? mapper = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.options = options ?? new ClaudeStrategistOptions();
        if (this.options.MaxTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxTokens must be positive.");
        }
        if (!(this.options.RequestTimeoutSeconds > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RequestTimeoutSeconds must be positive.");
        }
        if (this.options.Effort is not ("low" or "medium" or "high" or "xhigh" or "max"))
        {
            throw new ArgumentOutOfRangeException(nameof(options), $"Unknown effort '{this.options.Effort}'.");
        }
        this.promptBuilder = promptBuilder ?? new IntentPromptBuilder();
        this.mapper = mapper ?? new IntentDraftMapper();
    }

    public string Id => options.ResolvedId;

    public IntentSource Source => IntentSource.Llm;

    public ClaudeStrategistOptions Options => options;

    /// <summary>Most recent failure, or null when the most recent call succeeded.</summary>
    public ClaudeProposalFailure? LastFailure
    {
        get
        {
            lock (gate)
            {
                return lastFailure;
            }
        }
    }

    /// <summary>Raw model text from the most recent reply (success or failure), for the decision log.</summary>
    public string? LastRawResponse
    {
        get
        {
            lock (gate)
            {
                return lastRawResponse;
            }
        }
    }

    /// <summary>Raised on every null result, on the thread that completed the call.</summary>
    public event EventHandler<ClaudeProposalFailure>? ProposalFailed;

    /// <summary>The request this strategist would send for a context; exposed so tests and the arena can inspect exactly what leaves the process.</summary>
    public ModelRequest BuildRequest(StrategistContext context)
    {
        string model = options.ResolvedModel;
        IntentPrompt prompt = promptBuilder.Build(context, options.Mode, options.Personality, options.Vocabulary);
        return new ModelRequest(
            Model: model,
            SystemPrompt: prompt.SystemPrompt,
            UserContent: prompt.UserBlocks,
            JsonSchema: IntentDraftSchema.Json,
            Effort: ModelCapabilities.SupportsEffort(model) ? options.Effort : null,
            MaxTokens: options.MaxTokens,
            AdaptiveThinking: ModelCapabilities.SupportsAdaptiveThinking(model),
            ServerFallbacks: options.EnableServerFallbacks && ModelCapabilities.SupportsServerFallbacks(model));
    }

    public async Task<StrategistProposal?> ProposeAsync(StrategistContext context, CancellationToken cancellationToken = default)
    {
        StrategicFeatures features = context.Features;
        string model = options.ResolvedModel;
        ProposalCost noCost = new(0, 0, 0, 0, model);

        if (options.Mode == StrategistMode.Refine && context.ActiveIntent is null)
        {
            return Fail(ClaudeFailureCodes.RefineNoActiveIntent, "Refine mode needs an active intent to refine.", features, noCost, null);
        }
        if (options.Mode == StrategistMode.Refine && context.ActiveIntent!.Source != IntentSource.Llm)
        {
            // A refinement is offered as a primary proposal, and a primary renewal turns a fallback or emergency
            // placeholder into a committed plan; the fast model would then lock the strategic model out of the
            // placeholder slot it is supposed to take over at once. Only the strategic model's own plan is tuned.
            return Fail(
                ClaudeFailureCodes.RefineNotStrategicPlan,
                $"Refine mode tunes only the strategic model's own plan; the active intent comes from {context.ActiveIntent.Source}.",
                features,
                noCost,
                null);
        }

        ModelRequest request;
        try
        {
            request = BuildRequest(context);
        }
        catch (Exception ex)
        {
            return Fail(ClaudeFailureCodes.PromptFailed, ex.Message, features, noCost, null);
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        ModelReply reply;
        using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
            try
            {
                // WaitAsync bounds the wait even if a client ignores its token; the
                // abandoned task's eventual result or fault is simply dropped.
                reply = await client.CompleteAsync(request, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Fail(ClaudeFailureCodes.Cancelled, "Caller cancelled the request.", features, Elapsed(stopwatch, model), null);
            }
            catch (OperationCanceledException)
            {
                string detail = string.Create(CultureInfo.InvariantCulture, $"No reply within {options.RequestTimeoutSeconds:0.#} s.");
                return Fail(ClaudeFailureCodes.Timeout, detail, features, Elapsed(stopwatch, model), null);
            }
            catch (ModelClientException ex)
            {
                return Fail(CodeFor(ex.Kind), ex.Message, features, Elapsed(stopwatch, model), null);
            }
            catch (Exception ex)
            {
                return Fail(ClaudeFailureCodes.ClientError, $"{ex.GetType().Name}: {ex.Message}", features, Elapsed(stopwatch, model), null);
            }
        }
        stopwatch.Stop();

        string servedBy = string.IsNullOrEmpty(reply.ModelId) ? model : reply.ModelId;
        ProposalCost cost = new(
            stopwatch.Elapsed.TotalSeconds,
            reply.Usage.InputTokens,
            reply.Usage.OutputTokens,
            reply.Usage.CacheReadTokens,
            servedBy,
            reply.Usage.CacheCreationTokens,
            PriceTable.CostUsd(reply.Usage.Attempts, servedBy));
        string raw = reply.Text;
        lock (gate)
        {
            lastRawResponse = raw;
        }

        // Stop reason first: a refusal or a max-tokens cut can carry partial text
        // that happens to parse, and must not be treated as a decision.
        switch (reply.StopReason)
        {
            case "end_turn":
                break;
            case "refusal":
                return Fail(ClaudeFailureCodes.Refusal, reply.StopDetail ?? "Model declined the request.", features, cost, raw);
            case "max_tokens":
                return Fail(ClaudeFailureCodes.MaxTokens, $"Reply truncated at max_tokens={options.MaxTokens}.", features, cost, raw);
            default:
                return Fail(ClaudeFailureCodes.UnexpectedStop, $"stop_reason '{reply.StopReason}'.", features, cost, raw);
        }

        IntentDraft? draft = IntentDraftSchema.TryParse(raw, out string? parseError);
        if (draft is null)
        {
            return Fail(ClaudeFailureCodes.ParseFailed, parseError ?? "unparseable", features, cost, raw);
        }

        string intentId = string.Create(CultureInfo.InvariantCulture, $"{Id}/{features.SnapshotVersion}");
        IntentMappingResult mapped = mapper.Map(draft, features, intentId);
        if (mapped.Intent is not { } intent)
        {
            return Fail(ClaudeFailureCodes.MappingFailed, mapped.Error ?? "unmappable", features, cost, raw);
        }

        if (options.Mode == StrategistMode.Refine)
        {
            StrategicIntent active = context.ActiveIntent!;
            if (!string.Equals(intent.PlaybookId, active.PlaybookId, StringComparison.Ordinal))
            {
                return Fail(
                    ClaudeFailureCodes.RefinePlaybookSwitch,
                    $"Refine mode returned playbook '{intent.PlaybookId}' but the active playbook is '{active.PlaybookId}'.",
                    features,
                    cost,
                    raw);
            }
            intent = Refined(active, intent);
        }
        else
        {
            // Fields above the strategist's vocabulary tier come from the chosen playbook (build step 6).
            intent = IntentVocabulary.Restrict(intent, options.Vocabulary, context.Playbooks, features).Intent;
        }

        lock (gate)
        {
            lastFailure = null;
        }
        // A refinement names the plan it refines, so the scheduler drops it if that plan ended while it was in flight.
        return new StrategistProposal(intent, cost, raw, options.Mode == StrategistMode.Refine ? context.ActiveIntent!.IntentId : null);
    }

    /// <summary>
    /// Refine mode changes parameters only: everything else is the active intent's, so a fast model cannot shift
    /// posture, budget or objectives through the back door. Confidence stays the strategic model's, because the
    /// arbiter measures posture hysteresis against the incumbent's confidence and a fast model writing 0.95 there
    /// would lock the strategic model out of posture changes. Expiry never extends past the active intent's: the
    /// strategic model decides how long a plan lives, and a refinement that arrives after the plan it was based on
    /// expired is then already expired and refused instead of re-installing that plan over whatever replaced it.
    /// Identity and issue time, and the model's own assumptions and rationale, are the new proposal's; an army-value
    /// attack condition that tracked the old <c>attackArmyValue</c> moves with the new one (<see cref="AttackArmyThreshold"/>).
    /// </summary>
    private static StrategicIntent Refined(StrategicIntent active, StrategicIntent proposed) =>
        active with
        {
            IntentId = proposed.IntentId,
            Source = IntentSource.Llm,
            BasedOnSnapshotVersion = proposed.BasedOnSnapshotVersion,
            IssuedAt = proposed.IssuedAt,
            ExpiresAt = proposed.ExpiresAt < active.ExpiresAt ? proposed.ExpiresAt : active.ExpiresAt,
            PlaybookParameters = proposed.PlaybookParameters,
            AttackConditions = AttackArmyThreshold.Retarget(
                active.AttackConditions, AttackArmyThreshold.Of(active.PlaybookParameters), AttackArmyThreshold.Of(proposed.PlaybookParameters)),
            Assumptions = proposed.Assumptions,
            Rationale = proposed.Rationale,
        };

    private static ProposalCost Elapsed(Stopwatch stopwatch, string model)
    {
        stopwatch.Stop();
        return new ProposalCost(stopwatch.Elapsed.TotalSeconds, 0, 0, 0, model);
    }

    private static string CodeFor(ModelFailureKind kind) => kind switch
    {
        ModelFailureKind.RateLimited => ClaudeFailureCodes.RateLimited,
        ModelFailureKind.ServerError => ClaudeFailureCodes.ServerError,
        ModelFailureKind.Connection => ClaudeFailureCodes.Connection,
        ModelFailureKind.InvalidRequest => ClaudeFailureCodes.InvalidRequest,
        ModelFailureKind.Unauthorized => ClaudeFailureCodes.Unauthorized,
        _ => ClaudeFailureCodes.ClientError,
    };

    private StrategistProposal? Fail(string code, string detail, StrategicFeatures features, ProposalCost cost, string? raw)
    {
        ClaudeProposalFailure failure = new(code, detail, features.SnapshotVersion, features.Time, cost, raw);
        lock (gate)
        {
            lastFailure = failure;
        }
        try
        {
            ProposalFailed?.Invoke(this, failure);
        }
        catch (Exception)
        {
            // A faulty subscriber must not turn a recorded failure into an exception in the runtime.
        }
        return null;
    }
}
