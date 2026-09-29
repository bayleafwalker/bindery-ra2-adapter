// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Arbitration;
using Bindery.Ra2.Bot.Strategy;

namespace Bindery.Ra2.Bot.Runtime;

/// <summary>Tunables for <see cref="StrategyScheduler"/>. Freshness defaults match <see cref="ValidatorOptions"/>.</summary>
/// <param name="StrategicCadenceSeconds">Primary proposals are requested this often (spec: 20 s, sensible range 10–30 s).</param>
/// <param name="MajorEventSeverity">A new strategic event at or above this severity triggers a request immediately.</param>
/// <param name="MaxProposalAgeSeconds">A result arriving more than this after its snapshot is discarded as late (invariant 2).</param>
/// <param name="StaleEventSeverity">An event at or above this severity after the snapshot makes a pending result late.</param>
/// <param name="StrategistTimeoutSeconds">Game seconds after which an unanswered request is cancelled and its slot freed.</param>
/// <param name="FallbackRetrySeconds">Minimum game seconds between fallback (and emergency) attempts that were not explicitly requested.</param>
/// <param name="RunDeterministicStrategistsInline">
/// When true, requests to strategists whose <see cref="IStrategist.Source"/> is not <see cref="IntentSource.Llm"/>
/// (and which are not <see cref="IFrameAwareStrategist"/>) are awaited inside the tick, so a deterministic strategist that
/// happens to run on another thread still answers on the same frame in every replay. LLM strategists are never awaited.
/// </param>
/// <param name="InlineWaitTimeoutSeconds">Wall-clock bound on an inline wait; past it the request continues asynchronously.</param>
/// <param name="Personality">Passed through to <see cref="StrategistContext.Personality"/>.</param>
/// <param name="EmergencyPlaybookId">Playbook whose defaults become the intent when no strategist produced one.</param>
/// <param name="EmergencyIntentSeconds">Lifetime of that emergency intent.</param>
public sealed record SchedulerOptions(
    double StrategicCadenceSeconds = 20,
    double MajorEventSeverity = 0.6,
    double MaxProposalAgeSeconds = 15,
    double StaleEventSeverity = 0.7,
    double StrategistTimeoutSeconds = 60,
    double FallbackRetrySeconds = 5,
    bool RunDeterministicStrategistsInline = false,
    double InlineWaitTimeoutSeconds = 30,
    string? Personality = null,
    string? EmergencyPlaybookId = "generic-defend",
    double EmergencyIntentSeconds = 60)
{
    public static SchedulerOptions Default { get; } = new();
}

/// <summary>What one scheduler tick did, for the runtime's operational trigger.</summary>
public sealed record SchedulerTickReport(bool IntentChanged, int ResultsCollected, int RequestsStarted);

/// <summary>
/// Owns the asynchronous proposal lifecycle between strategists and the arbiter.
/// The tick loop must never wait on a strategist (an LLM answers in seconds, the
/// game advances 15 frames per second), so requests are started as tasks and
/// polled with <see cref="Task.IsCompleted"/>; every result is checked for
/// lateness and validated against the context current <em>at arrival</em>, not
/// the one it was asked about.
/// </summary>
/// <remarks>
/// Three slots, each with at most one request in flight:
/// <list type="bullet">
/// <item><b>Primary</b>: asked at start, every <see cref="SchedulerOptions.StrategicCadenceSeconds"/>, on a new
/// event with severity ≥ <see cref="SchedulerOptions.MajorEventSeverity"/>, and when the arbiter requests a replan.
/// Triggers that arrive while a request is in flight are remembered and served when it finishes, or at once when
/// its answer could no longer be applied (overtaken by a severe event or past
/// <see cref="SchedulerOptions.MaxProposalAgeSeconds"/>): that request is then cancelled and logged as
/// <c>superseded</c>, so a slow or hung strategist cannot hold the slot until the timeout while the bot has no
/// strategic answer to a base attack.</item>
/// <item><b>Fallback</b> (deterministic): asked when there is no active intent, when the active intent is an
/// emergency placeholder, or when the arbiter asks for it (expiry, abort).</item>
/// <item><b>Shadow</b> (optional): asked alongside the primary with the same context; its results are validated
/// and logged as <c>strategy.shadow</c> with what the arbiter <em>would</em> have done, and never applied. A shadow
/// answer the primary path would have discarded as late is logged with <c>wouldBe</c> Refused and
/// <c>wouldBeReason</c> <c>late:&lt;reason&gt;</c>, so shadow-vs-live comparisons do not credit it; it does not count
/// in <see cref="BotMetrics.LateDiscarded"/>, which measures the live path.</item>
/// </list>
/// If after all that there is still no active intent, the emergency intent (a playbook's defaults) is validated
/// and installed, so the planner always has direction. Requests unanswered after
/// <see cref="SchedulerOptions.StrategistTimeoutSeconds"/> of game time are cancelled; all are cancelled on dispose.
/// </remarks>
public sealed class StrategyScheduler : IDisposable
{
    private readonly IStrategist primary;
    private readonly IStrategist fallback;
    private readonly IStrategist? shadow;
    private readonly IIntentValidator validator;
    private readonly IntentArbiter arbiter;
    private readonly IRulesDatabase rules;
    private readonly IPlaybookLibrary playbooks;
    private readonly IDecisionLog log;
    private readonly BotMetrics metrics;
    private readonly CancellationTokenSource root = new();

    private Request? primaryRequest;
    private Request? fallbackRequest;
    private Request? shadowRequest;
    private string? pendingTrigger;
    private GameTime? lastPrimaryStart;
    private GameTime? lastFallbackStart;
    private GameTime? lastEmergencyAttempt;
    private GameTime lastMajorEventTime = new(long.MinValue);
    private bool disposed;

    public StrategyScheduler(
        IStrategist primary,
        IStrategist fallback,
        IStrategist? shadow,
        IIntentValidator validator,
        IntentArbiter arbiter,
        IRulesDatabase rules,
        IPlaybookLibrary playbooks,
        IDecisionLog log,
        SchedulerOptions? options = null,
        BotMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(playbooks);
        ArgumentNullException.ThrowIfNull(log);
        this.primary = primary;
        this.fallback = fallback;
        this.shadow = shadow;
        this.validator = validator;
        this.arbiter = arbiter;
        this.rules = rules;
        this.playbooks = playbooks;
        this.log = log;
        this.metrics = metrics ?? new BotMetrics();
        Options = options ?? SchedulerOptions.Default;
        if (!(Options.StrategicCadenceSeconds > 0)) throw new ArgumentOutOfRangeException(nameof(options), "Strategic cadence must be positive.");
        if (!(Options.StrategistTimeoutSeconds > 0)) throw new ArgumentOutOfRangeException(nameof(options), "Strategist timeout must be positive.");
    }

    public SchedulerOptions Options { get; }

    public IntentArbiter Arbiter => arbiter;

    public bool PrimaryInFlight => primaryRequest is not null;

    public bool FallbackInFlight => fallbackRequest is not null;

    public bool ShadowInFlight => shadowRequest is not null;

    /// <summary>
    /// Runs one frame of the proposal lifecycle. Never blocks on a strategist
    /// except in the opt-in inline mode for deterministic strategists.
    /// </summary>
    public SchedulerTickReport Tick(BeliefSnapshot belief, StrategicFeatures features)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(belief);
        ArgumentNullException.ThrowIfNull(features);
        StrategicIntent? before = arbiter.Active;
        int collected = 0, started = 0;
        GameTime now = features.Time;

        arbiter.Update(features);
        string? replanReason = arbiter.ReplanReason;
        NotifyFrame(now);
        MarkInvalidatingEvents(features);

        collected += Poll(ref primaryRequest, belief, features);
        collected += Poll(ref fallbackRequest, belief, features);
        collected += Poll(ref shadowRequest, belief, features);

        string? trigger = PrimaryTrigger(features);
        pendingTrigger ??= trigger;
        if (pendingTrigger is not null && primaryRequest is not null && LateReason(primaryRequest, now) is not null)
        {
            // Its answer would be discarded as late anyway: free the slot for the request that is wanted now.
            Request superseded = primaryRequest;
            primaryRequest = null;
            superseded.Cancellation.Cancel();
            Observe(superseded.Task);
            superseded.Cancellation.Dispose();
            Failed(superseded, features, "superseded", LateReason(superseded, now));
            collected++;
        }
        if (pendingTrigger is not null && primaryRequest is null)
        {
            string reason = pendingTrigger;
            pendingTrigger = null;
            lastPrimaryStart = now;
            arbiter.AcknowledgeReplan();
            StrategistContext context = Context(features, belief, reason);
            primaryRequest = Start(ProposalRole.Primary, primary, context, reason);
            started++;
            if (shadow is not null && shadowRequest is null)
            {
                shadowRequest = Start(ProposalRole.Shadow, shadow, context, reason);
                started++;
            }
            collected += Poll(ref primaryRequest, belief, features);
            collected += Poll(ref shadowRequest, belief, features);
        }

        bool fallbackWanted = arbiter.FallbackRequested || arbiter.Active is null || arbiter.ActiveRole == ProposalRole.Emergency;
        bool fallbackDue = arbiter.FallbackRequested
            || lastFallbackStart is null
            || now.SecondsSince(lastFallbackStart.Value) >= Options.FallbackRetrySeconds;
        if (fallbackWanted && fallbackDue && fallbackRequest is null)
        {
            string reason = arbiter.FallbackRequested ? $"requested:{replanReason ?? "arbiter"}" : arbiter.Active is null ? "no_intent" : "placeholder";
            arbiter.AcknowledgeFallback();
            lastFallbackStart = now;
            fallbackRequest = Start(ProposalRole.Fallback, fallback, Context(features, belief, reason), reason);
            started++;
            collected += Poll(ref fallbackRequest, belief, features);
        }

        if (arbiter.Active is null
            && (lastEmergencyAttempt is null || now.SecondsSince(lastEmergencyAttempt.Value) >= Options.FallbackRetrySeconds))
        {
            lastEmergencyAttempt = now;
            InstallEmergency(belief, features);
        }

        return new SchedulerTickReport(!ReferenceEquals(before, arbiter.Active), collected, started);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        root.Cancel();
        foreach (Request? request in new[] { primaryRequest, fallbackRequest, shadowRequest })
        {
            if (request is null) continue;
            Observe(request.Task);
            request.Cancellation.Dispose();
        }
        primaryRequest = fallbackRequest = shadowRequest = null;
        root.Dispose();
    }

    private StrategistContext Context(StrategicFeatures features, BeliefSnapshot? belief, string? trigger = null) =>
        new(features, rules, playbooks, arbiter.Active, arbiter.History.ToArray(), Options.Personality,
            belief is null ? null : new SortedSet<string>(belief.OwnBuildingTypes, StringComparer.Ordinal),
            trigger,
            arbiter.Active is null ? null : arbiter.ActiveRole,
            arbiter.Active is null ? null : arbiter.ActiveSince,
            arbiter.Active is null ? null : arbiter.BaseThreatOverrideSpent,
            arbiter.CurrentPhase);

    /// <summary>The context a strategist would receive for these features (and this belief's own buildings) right now, for probes and diagnostics.</summary>
    public StrategistContext ContextFor(StrategicFeatures features, BeliefSnapshot? belief = null)
    {
        ArgumentNullException.ThrowIfNull(features);
        return Context(features, belief);
    }

    private string? PrimaryTrigger(StrategicFeatures features)
    {
        string? eventTrigger = null;
        foreach (StrategicEvent evt in features.Events)
        {
            if (evt.Severity >= Options.MajorEventSeverity && evt.Time > lastMajorEventTime)
            {
                eventTrigger ??= $"event:{evt.Kind}";
            }
        }
        foreach (StrategicEvent evt in features.Events)
        {
            if (evt.Severity >= Options.MajorEventSeverity && evt.Time > lastMajorEventTime) lastMajorEventTime = evt.Time;
        }

        if (lastPrimaryStart is null) return "initial";
        if (arbiter.ReplanRequested) return $"replan:{arbiter.ReplanReason ?? "unspecified"}";
        if (eventTrigger is not null) return eventTrigger;
        return features.Time.SecondsSince(lastPrimaryStart.Value) >= Options.StrategicCadenceSeconds ? "cadence" : null;
    }

    private void NotifyFrame(GameTime now)
    {
        List<IStrategist> seen = [];
        foreach (IStrategist? strategist in new[] { primary, fallback, shadow })
        {
            if (strategist is IFrameAwareStrategist aware && !seen.Any(s => ReferenceEquals(s, strategist)))
            {
                seen.Add(strategist);
                aware.OnFrame(now);
            }
        }
    }

    private void MarkInvalidatingEvents(StrategicFeatures features)
    {
        foreach (Request? request in new[] { primaryRequest, fallbackRequest, shadowRequest })
        {
            if (request is null || request.Invalidator is not null) continue;
            foreach (StrategicEvent evt in features.Events)
            {
                if (evt.Severity >= Options.StaleEventSeverity && evt.Time > request.SnapshotTime)
                {
                    request.Invalidator = evt;
                    break;
                }
            }
        }
    }

    private Request Start(ProposalRole role, IStrategist strategist, StrategistContext context, string trigger)
    {
        StrategicFeatures features = context.Features;
        metrics.Requests++;
        log.Write(new DecisionRecord(RuntimeRecordKinds.Request, features.Time, features.SnapshotVersion, BotJson.ToElement(new
        {
            role,
            strategistId = strategist.Id,
            source = strategist.Source,
            trigger,
        })));

        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(root.Token);
        Task<StrategistProposal?> task;
        try
        {
            task = strategist.ProposeAsync(context, cancellation.Token) ?? Task.FromResult<StrategistProposal?>(null);
        }
        catch (Exception ex)
        {
            task = Task.FromException<StrategistProposal?>(ex);
        }

        if (Options.RunDeterministicStrategistsInline && !task.IsCompleted
            && strategist.Source != IntentSource.Llm && strategist is not IFrameAwareStrategist)
        {
            try
            {
                task.Wait(TimeSpan.FromSeconds(Math.Max(0, Options.InlineWaitTimeoutSeconds)));
            }
            catch (AggregateException)
            {
                // The fault is reported when the task is polled.
            }
        }
        return new Request(role, strategist, task, cancellation, features, trigger);
    }

    private int Poll(ref Request? slot, BeliefSnapshot belief, StrategicFeatures features)
    {
        if (slot is null) return 0;
        Request request = slot;
        GameTime now = features.Time;
        if (!request.Task.IsCompleted)
        {
            if (now.SecondsSince(request.StartedAt) <= Options.StrategistTimeoutSeconds) return 0;
            slot = null;
            request.Cancellation.Cancel();
            Observe(request.Task);
            request.Cancellation.Dispose();
            Failed(request, features, "timeout", null);
            return 1;
        }

        slot = null;
        request.Cancellation.Dispose();
        if (request.Task.IsFaulted)
        {
            Exception? error = request.Task.Exception?.InnerExceptions.FirstOrDefault() ?? request.Task.Exception;
            Failed(request, features, "exception", error?.Message);
            return 1;
        }
        if (request.Task.IsCanceled)
        {
            Failed(request, features, "cancelled", null);
            return 1;
        }
        StrategistProposal? proposal = request.Task.Result;
        if (proposal?.Intent is null)
        {
            Failed(request, features, "no_opinion", null);
            return 1;
        }
        Handle(request, proposal, belief, features);
        return 1;
    }

    private void Failed(Request request, StrategicFeatures features, string reason, string? message)
    {
        metrics.ProposalsFailed++;
        log.Write(new DecisionRecord(DecisionRecordKinds.ProposalFailed, features.Time, features.SnapshotVersion, BotJson.ToElement(new
        {
            role = request.Role,
            strategistId = request.Strategist.Id,
            requestVersion = request.SnapshotVersion,
            requestFrame = request.SnapshotTime.Frame,
            reason,
            message,
        })));
    }

    private void Handle(Request request, StrategistProposal proposal, BeliefSnapshot belief, StrategicFeatures features)
    {
        GameTime now = features.Time;
        StrategicIntent intent = proposal.Intent;
        JsonElement intentJson = IntentJson.ToElement(intent);
        ValidationContext context = new(features, belief, rules, playbooks, arbiter.Active, arbiter.ActiveSince);

        double age = now.SecondsSince(request.SnapshotTime);
        string? late = LateReason(request, now);
        if (request.Role == ProposalRole.Shadow)
        {
            ValidationResult shadowResult = validator.Validate(intent, context);
            ArbitrationDecision wouldBe = late is null
                ? arbiter.Preview(shadowResult, features, ProposalRole.Primary)
                : new ArbitrationDecision(ArbitrationOutcome.Refused, $"late:{late}", intent);
            metrics.ShadowProposals++;
            log.Write(new DecisionRecord(DecisionRecordKinds.ShadowProposal, now, features.SnapshotVersion, BotJson.ToElement(new
            {
                role = request.Role,
                strategistId = request.Strategist.Id,
                requestVersion = request.SnapshotVersion,
                requestFrame = request.SnapshotTime.Frame,
                latencyFrames = now.Frame - request.SnapshotTime.Frame,
                cost = proposal.Cost,
                rawResponse = proposal.RawResponse,
                intent = intentJson,
                accepted = shadowResult.Accepted,
                issues = Issues(shadowResult.Issues),
                wouldBe = wouldBe.Outcome,
                wouldBeReason = wouldBe.Reason,
                // What the shadow strategist was asked with, in the activation record's dataset form, so its choices
                // can be distilled (DatasetFilter.IncludeShadow).
                faction = request.Features.Faction,
                featureVersion = FeatureVector.Version,
                features = FeatureVector.Encode(request.Features),
                featuresSnapshotVersion = request.Features.SnapshotVersion,
                featuresFrame = request.Features.Time.Frame,
                mode = request.Features.Mode,
            })));
            return;
        }

        metrics.Proposals++;
        log.Write(new DecisionRecord(DecisionRecordKinds.Proposal, now, features.SnapshotVersion, BotJson.ToElement(new
        {
            role = request.Role,
            strategistId = request.Strategist.Id,
            requestVersion = request.SnapshotVersion,
            requestFrame = request.SnapshotTime.Frame,
            latencyFrames = now.Frame - request.SnapshotTime.Frame,
            cost = proposal.Cost,
            rawResponse = proposal.RawResponse,
            refinesIntentId = proposal.RefinesIntentId,
            intent = intentJson,
        })));

        if (late is not null)
        {
            Late(request, intent, features, late, age, request.Invalidator);
            return;
        }
        if (proposal.RefinesIntentId is { } refined && !string.Equals(refined, arbiter.Active?.IntentId, StringComparison.Ordinal))
        {
            // A refinement of a plan that ended while it was in flight (aborted, expired or replaced): applying it
            // would reinstate the ended plan as a renewal-looking switch.
            Late(request, intent, features, "refined_intent_ended", age, null);
            return;
        }

        ValidationResult result = validator.Validate(intent, context);
        ArbitrationDecision? decision = result.Accepted ? arbiter.Offer(result, features, request.Role, request.Features) : null;
        log.Write(new DecisionRecord(DecisionRecordKinds.Validation, now, features.SnapshotVersion, BotJson.ToElement(new
        {
            role = request.Role,
            strategistId = request.Strategist.Id,
            intentId = intent.IntentId,
            accepted = result.Accepted,
            issues = Issues(result.Issues),
            arbitration = decision?.Outcome,
            arbitrationReason = decision?.Reason,
        })));

        if (result.Accepted) return;
        ValidationIssue? stale = result.Issues.FirstOrDefault(static i => i.Severity == ValidationSeverity.Reject && ValidationCodes.IsStale(i.Code));
        if (stale is not null) Late(request, intent, features, $"validator:{stale.Code}", age, null);
        else metrics.Rejected++;
    }

    /// <summary>Why an answer to <paramref name="request"/> arriving now would be discarded (invariant 2), or null.</summary>
    private string? LateReason(Request request, GameTime now) =>
        now.SecondsSince(request.SnapshotTime) > Options.MaxProposalAgeSeconds ? "age"
        : request.Invalidator is not null ? "event"
        : null;

    private void Late(Request request, StrategicIntent intent, StrategicFeatures features, string reason, double age, StrategicEvent? evt)
    {
        metrics.LateDiscarded++;
        log.Write(new DecisionRecord(DecisionRecordKinds.LateDiscarded, features.Time, features.SnapshotVersion, BotJson.ToElement(new
        {
            role = request.Role,
            strategistId = request.Strategist.Id,
            intentId = intent.IntentId,
            requestVersion = request.SnapshotVersion,
            reason,
            ageSeconds = age,
            eventKind = evt?.Kind,
            eventFrame = evt?.Time.Frame,
        })));
    }

    private void InstallEmergency(BeliefSnapshot belief, StrategicFeatures features)
    {
        Playbook? playbook = PlaybookIntents.Choose(playbooks, features.Faction, Options.EmergencyPlaybookId);
        if (playbook is null) return;
        StrategicIntent intent = PlaybookIntents.FromPlaybook(
            playbook, features, $"emergency-{features.SnapshotVersion}", IntentSource.Fallback,
            Options.EmergencyIntentSeconds, 0.5, "No strategist produced a valid intent; playbook defaults.");
        ValidationContext context = new(features, belief, rules, playbooks, arbiter.Active, arbiter.ActiveSince);
        ValidationResult result = validator.Validate(intent, context);
        if (!result.Accepted && intent.Objectives.Count > 0)
        {
            intent = intent with { Objectives = [] };
            result = validator.Validate(intent, context);
        }
        ArbitrationDecision? decision = result.Accepted ? arbiter.Offer(result, features, ProposalRole.Emergency) : null;
        log.Write(new DecisionRecord(DecisionRecordKinds.Validation, features.Time, features.SnapshotVersion, BotJson.ToElement(new
        {
            role = ProposalRole.Emergency,
            strategistId = "emergency",
            intentId = intent.IntentId,
            accepted = result.Accepted,
            issues = Issues(result.Issues),
            arbitration = decision?.Outcome,
            arbitrationReason = decision?.Reason,
        })));
    }

    private static object[] Issues(IReadOnlyList<ValidationIssue> issues) =>
        issues.Select(static i => (object)new { code = i.Code, severity = i.Severity, message = i.Message }).ToArray();

    private static void Observe(Task task) =>
        task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private sealed class Request(
        ProposalRole role,
        IStrategist strategist,
        Task<StrategistProposal?> task,
        CancellationTokenSource cancellation,
        StrategicFeatures features,
        string trigger)
    {
        /// <summary>The features the strategist was asked with (logged with an activation as what it saw).</summary>
        public StrategicFeatures Features { get; } = features;
        public ProposalRole Role { get; } = role;
        public IStrategist Strategist { get; } = strategist;
        public Task<StrategistProposal?> Task { get; } = task;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public long SnapshotVersion { get; } = features.SnapshotVersion;
        public GameTime SnapshotTime { get; } = features.Time;
        public GameTime StartedAt { get; } = features.Time;
        public string Trigger { get; } = trigger;
        public StrategicEvent? Invalidator { get; set; }
    }
}

/// <summary>
/// Canonical JSON for an intent in the decision log: parameters are sorted by
/// name so the same intent always serialises to the same bytes, and the form
/// round-trips through <see cref="FromElement"/> for replay.
/// </summary>
public static class IntentJson
{
    public static JsonElement ToElement(StrategicIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        SortedDictionary<string, double> parameters = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, double> pair in intent.PlaybookParameters) parameters[pair.Key] = pair.Value;
        return BotJson.ToElement(intent with { PlaybookParameters = parameters });
    }

    public static StrategicIntent FromElement(JsonElement element) =>
        element.Deserialize<StrategicIntent>(BotJson.Options) ?? throw new FormatException("Intent JSON is null.");
}
