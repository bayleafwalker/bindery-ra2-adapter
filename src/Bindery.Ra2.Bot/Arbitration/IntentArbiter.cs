// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Runtime;
using Bindery.Ra2.Bot.Strategy;

namespace Bindery.Ra2.Bot.Arbitration;

/// <summary>Which strategist slot produced a proposal; the arbiter treats slots differently.</summary>
public enum ProposalRole
{
    /// <summary>The configured strategist (selector, bandit, LLM, ...).</summary>
    Primary,

    /// <summary>The deterministic fallback, run when no usable intent exists or the active one aborted/expired.</summary>
    Fallback,

    /// <summary>Recorded for comparison only; never offered to the arbiter.</summary>
    Shadow,

    /// <summary>A playbook-default intent built when no strategist produced anything.</summary>
    Emergency,
}

public enum ArbitrationOutcome { Activated, Renewed, Refused }

/// <param name="Reason">Stable reason: <c>no_active</c>, <c>switch</c>, <c>yield</c>, <c>override:abort</c>,
/// <c>override:base_threat</c>, <c>renewal</c>, <c>not_validated</c>, <c>expired</c>, <c>commitment</c>,
/// <c>hysteresis</c>, <c>abort_firing</c>, <c>emergency_only_when_idle</c>, <c>shadow</c>.</param>
public sealed record ArbitrationDecision(ArbitrationOutcome Outcome, string Reason, StrategicIntent? Intent);

/// <summary>Tunables for <see cref="IntentArbiter"/> (invariant 3).</summary>
/// <param name="DefaultMinCommitSeconds">Commitment when the playbook declares none (or is unknown).</param>
/// <param name="PostureConfidenceMargin">A posture change needs challenger confidence at least this much above the incumbent's.</param>
/// <param name="BaseThreatOverrideRatio">A <see cref="ConditionMetric.BaseThreatRatio"/> above this overrides commitment.</param>
/// <param name="HistoryCapacity">Most recent history entries kept for strategist context; a renewal chain takes at most two (renewals are folded).</param>
public sealed record ArbiterOptions(
    double DefaultMinCommitSeconds = 45,
    double PostureConfidenceMargin = 0.15,
    double BaseThreatOverrideRatio = 1.5,
    int HistoryCapacity = 32)
{
    public static ArbiterOptions Default { get; } = new();
}

/// <summary>
/// Holds the active intent and decides whether a validated proposal replaces it
/// (invariant 3). A strategist that changes its mind every cadence is worse than
/// one that commits to a mediocre plan, so a switch inside the playbook's minimum
/// commitment window is refused unless the active intent's abort trigger fires or
/// the base is under a threat the active plan was not built for, and a posture
/// change additionally needs a confidence margin.
/// </summary>
/// <remarks>
/// Conventions where the spec is silent:
/// <list type="bullet">
/// <item>A proposal with the same playbook and posture as the active intent is a
/// <em>renewal</em>: it is always accepted, refreshes parameters and expiry, and does
/// not restart the commitment clock (<see cref="ActiveSince"/>).</item>
/// <item>An intent installed by the fallback or emergency slot is a placeholder: it
/// yields to any primary proposal without commitment or hysteresis, otherwise a slow
/// LLM would be locked out for a full commitment window by the placeholder that
/// covered its latency.</item>
/// <item>An override (abort trigger or base threat) also skips the posture margin: the
/// incumbent's plan is failing, so its confidence is no longer a fair bar.</item>
/// <item>The base-threat override is spent once per threat episode: the intent installed while the threat holds
/// (by the override or otherwise) was chosen knowing about it and gets its normal commitment, and a Defend or
/// Turtle incumbent is never overridden by it. Otherwise a strategist wavering under attack would flip the posture
/// every cadence for as long as the threat lasts. The episode ends when the threat stops holding.</item>
/// <item>A firing abort trigger ends the intent, like expiry, and asks for the fallback: an aborted plan must not
/// keep driving the planner until a different plan happens to arrive.</item>
/// <item>A replan request is acknowledged by an accepted intent only when that intent was based on a snapshot at
/// or after the request; an answer computed before the trigger fired does not answer it.</item>
/// <item>An intent whose own abort trigger already holds is refused (<c>abort_firing</c>): it would abort on
/// arrival, and accepting it lets a strategist re-install the very plan that just aborted.</item>
/// <item>Triggers are edge-detected: each abort, replan or base-threat condition asks
/// for a replan once when it starts holding, not every frame while it holds.</item>
/// </list>
/// </remarks>
public sealed class IntentArbiter
{
    private const double Epsilon = 1e-9;

    private readonly IPlaybookLibrary playbooks;
    private readonly IDecisionLog? log;
    private readonly BotMetrics? metrics;
    private readonly List<IntentHistoryEntry> history = [];
    private bool abortFiring;
    private bool replanFiring;
    private bool baseThreatFiring;
    private bool baseThreatAnswered;
    private long replanRequestedVersion;

    public IntentArbiter(IPlaybookLibrary playbooks, ArbiterOptions? options = null, IDecisionLog? log = null, BotMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(playbooks);
        this.playbooks = playbooks;
        this.log = log;
        this.metrics = metrics;
        Options = options ?? ArbiterOptions.Default;
    }

    public ArbiterOptions Options { get; }

    public StrategicIntent? Active { get; private set; }

    /// <summary>When the current commitment started; unchanged by renewals.</summary>
    public GameTime ActiveSince { get; private set; }

    /// <summary>Slot that installed <see cref="Active"/>.</summary>
    public ProposalRole ActiveRole { get; private set; }

    /// <summary>
    /// True while the base-threat override has been spent on the current threat episode: a plan was accepted while
    /// the threat held, so the override stays unavailable until the threat clears.
    /// </summary>
    public bool BaseThreatOverrideSpent => baseThreatAnswered;

    /// <summary>Posture of the most recently ended or active intent, for flip counting.</summary>
    public StrategicPosture? LastPosture { get; private set; }

    public IReadOnlyList<IntentHistoryEntry> History => history;

    /// <summary>Set when a trigger, expiry or abort wants the primary strategist asked again.</summary>
    public bool ReplanRequested { get; private set; }

    public string? ReplanReason { get; private set; }

    /// <summary>Set when the active intent expired or aborted and the fallback strategist should answer now.</summary>
    public bool FallbackRequested { get; private set; }

    public void AcknowledgeReplan()
    {
        ReplanRequested = false;
        ReplanReason = null;
    }

    public void AcknowledgeFallback() => FallbackRequested = false;

    /// <summary>Commitment window for an intent: its playbook's <see cref="Playbook.MinCommitSeconds"/> when positive, else the default.</summary>
    public double MinCommitSecondsFor(StrategicIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return playbooks.TryGet(intent.PlaybookId, out Playbook playbook) && playbook.MinCommitSeconds > 0
            ? playbook.MinCommitSeconds
            : Options.DefaultMinCommitSeconds;
    }

    /// <summary>
    /// Per-frame housekeeping: ends an expired or aborted intent (and asks for the fallback),
    /// turns rising edges of replan triggers and base threat into replan requests, and ends a
    /// base-threat episode once the threat stops holding.
    /// </summary>
    public void Update(StrategicFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);
        bool threat = ConditionEvaluator.BaseThreatRatio(features) > Options.BaseThreatOverrideRatio;
        if (!threat) baseThreatAnswered = false;
        if (Active is null) return;
        if (features.Time >= Active.ExpiresAt)
        {
            End(features, "expired");
            FallbackRequested = true;
            RequestReplan("expired", features);
            return;
        }

        bool abort = ConditionEvaluator.AnyOf(Active.AbortTriggers, features);
        if (abort && !abortFiring)
        {
            End(features, "aborted");
            FallbackRequested = true;
            RequestReplan("abort", features);
            return;
        }
        abortFiring = abort;

        bool replan = ConditionEvaluator.AnyOf(Active.ReplanTriggers, features);
        if (replan && !replanFiring) RequestReplan("trigger", features);
        replanFiring = replan;

        if (threat && !baseThreatFiring) RequestReplan("base_threat", features);
        baseThreatFiring = threat;
    }

    /// <summary>What <see cref="Offer"/> would decide, without changing any state. Used for shadow proposals.</summary>
    public ArbitrationDecision Preview(ValidationResult result, StrategicFeatures features, ProposalRole role = ProposalRole.Primary)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(features);
        return Decide(result, features, role);
    }

    /// <summary>
    /// Offers a validation result. Only an accepted result can become active
    /// (invariant 5); the decision is applied, logged and returned.
    /// </summary>
    /// <param name="basis">
    /// The features the strategist was asked with, when older than <paramref name="features"/> (an answer that took
    /// time). The activation record logs them as <c>features</c>, because the decision dataset pairs that vector with
    /// the chosen playbook as what the strategist saw; the decision itself is always made on the current features.
    /// </param>
    public ArbitrationDecision Offer(ValidationResult result, StrategicFeatures features, ProposalRole role = ProposalRole.Primary, StrategicFeatures? basis = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(features);
        if (role == ProposalRole.Shadow)
        {
            return new ArbitrationDecision(ArbitrationOutcome.Refused, "shadow", result.Intent);
        }
        if (Active is not null && features.Time >= Active.ExpiresAt)
        {
            End(features, "expired");
            FallbackRequested = true;
        }

        ArbitrationDecision decision = Decide(result, features, role);
        switch (decision.Outcome)
        {
            case ArbitrationOutcome.Refused:
                if (metrics is not null) metrics.Refused++;
                break;
            case ArbitrationOutcome.Renewed:
                Renew(decision.Intent!, features, role, basis ?? features);
                break;
            case ArbitrationOutcome.Activated:
                Activate(decision.Intent!, features, role, decision.Reason, basis ?? features);
                break;
        }
        return decision;
    }

    private ArbitrationDecision Decide(ValidationResult result, StrategicFeatures features, ProposalRole role)
    {
        if (role == ProposalRole.Shadow) return new ArbitrationDecision(ArbitrationOutcome.Refused, "shadow", result.Intent);
        if (!result.Accepted || result.Intent is null)
        {
            return new ArbitrationDecision(ArbitrationOutcome.Refused, "not_validated", null);
        }
        StrategicIntent challenger = result.Intent;
        GameTime now = features.Time;
        if (challenger.ExpiresAt <= now) return new ArbitrationDecision(ArbitrationOutcome.Refused, "expired", challenger);
        if (ConditionEvaluator.AnyOf(challenger.AbortTriggers, features))
        {
            return new ArbitrationDecision(ArbitrationOutcome.Refused, "abort_firing", challenger);
        }

        StrategicIntent? incumbent = Active is not null && now < Active.ExpiresAt ? Active : null;
        if (incumbent is null) return new ArbitrationDecision(ArbitrationOutcome.Activated, "no_active", challenger);
        if (role == ProposalRole.Emergency)
        {
            return new ArbitrationDecision(ArbitrationOutcome.Refused, "emergency_only_when_idle", challenger);
        }
        if (string.Equals(challenger.PlaybookId, incumbent.PlaybookId, StringComparison.Ordinal) && challenger.Posture == incumbent.Posture)
        {
            return new ArbitrationDecision(ArbitrationOutcome.Renewed, "renewal", challenger);
        }

        bool placeholder = ActiveRole == ProposalRole.Emergency
            || (ActiveRole == ProposalRole.Fallback && role == ProposalRole.Primary);
        if (placeholder) return new ArbitrationDecision(ArbitrationOutcome.Activated, "yield", challenger);

        if (ConditionEvaluator.AnyOf(incumbent.AbortTriggers, features))
        {
            return new ArbitrationDecision(ArbitrationOutcome.Activated, "override:abort", challenger);
        }
        if (!baseThreatAnswered
            && incumbent.Posture is not (StrategicPosture.Defend or StrategicPosture.Turtle)
            && ConditionEvaluator.BaseThreatRatio(features) > Options.BaseThreatOverrideRatio)
        {
            return new ArbitrationDecision(ArbitrationOutcome.Activated, "override:base_threat", challenger);
        }
        if (now.SecondsSince(ActiveSince) < MinCommitSecondsFor(incumbent) - Epsilon)
        {
            return new ArbitrationDecision(ArbitrationOutcome.Refused, "commitment", challenger);
        }
        if (challenger.Posture != incumbent.Posture
            && challenger.Confidence - incumbent.Confidence < Options.PostureConfidenceMargin - Epsilon)
        {
            return new ArbitrationDecision(ArbitrationOutcome.Refused, "hysteresis", challenger);
        }
        return new ArbitrationDecision(ArbitrationOutcome.Activated, "switch", challenger);
    }

    /// <remarks>
    /// The <c>strategy.intent_activated</c> payload (written here and by <see cref="Renew"/>) carries the
    /// <see cref="DecisionDataset"/> contract: <c>faction</c>, <c>featureVersion</c>, <c>features</c>
    /// (<see cref="FeatureVector.Encode"/> of the features the strategist was asked with, whose snapshot version and
    /// frame are <c>featuresSnapshotVersion</c> and <c>featuresFrame</c>) and <c>intent</c>
    /// (canonical <see cref="IntentJson"/>), alongside the arbitration fields.
    /// </remarks>
    private void Activate(StrategicIntent intent, StrategicFeatures features, ProposalRole role, string reason, StrategicFeatures basis)
    {
        StrategicIntent? previous = Active;
        if (previous is not null) End(features, reason == "yield" ? "yielded" : $"replaced:{reason}");

        bool flip = LastPosture is StrategicPosture last && last != intent.Posture;
        Active = intent;
        ActiveSince = features.Time;
        ActiveRole = role;
        LastPosture = intent.Posture;
        abortFiring = ConditionEvaluator.AnyOf(intent.AbortTriggers, features);
        replanFiring = ConditionEvaluator.AnyOf(intent.ReplanTriggers, features);
        baseThreatFiring = ConditionEvaluator.BaseThreatRatio(features) > Options.BaseThreatOverrideRatio;
        // Installed while the threat holds: this plan was chosen knowing about it, so the threat is answered.
        baseThreatAnswered = baseThreatFiring;
        FallbackRequested = false;
        AcknowledgeReplanAnsweredBy(intent);
        AddHistory(new IntentHistoryEntry(intent.IntentId, intent.Source, intent.Posture, intent.PlaybookId, features.Time, null, null));

        if (metrics is not null)
        {
            metrics.Activations++;
            if (flip) metrics.PostureFlips++;
            if (role == ProposalRole.Fallback) metrics.FallbackActivations++;
            if (role == ProposalRole.Emergency) metrics.EmergencyActivations++;
        }
        log?.Write(new DecisionRecord(DecisionRecordKinds.IntentActivated, features.Time, features.SnapshotVersion, BotJson.ToElement(new
        {
            intentId = intent.IntentId,
            source = intent.Source,
            role,
            reason,
            renewal = false,
            playbookId = intent.PlaybookId,
            posture = intent.Posture,
            postureFlip = flip,
            confidence = intent.Confidence,
            previousIntentId = previous?.IntentId,
            basedOnSnapshotVersion = intent.BasedOnSnapshotVersion,
            expiresAtFrame = intent.ExpiresAt.Frame,
            faction = features.Faction,
            featureVersion = FeatureVector.Version,
            features = FeatureVector.Encode(basis),
            featuresSnapshotVersion = basis.SnapshotVersion,
            featuresFrame = basis.Time.Frame,
            mode = features.Mode,
            intent = IntentJson.ToElement(intent),
        })));
    }

    private void Renew(StrategicIntent intent, StrategicFeatures features, ProposalRole role, StrategicFeatures basis)
    {
        StrategicIntent previous = Active!;
        CloseHistory(previous.IntentId, features.Time, "renewed");
        FoldRenewalChain();
        Active = intent;
        // A primary strategist endorsing a placeholder's plan makes it a real commitment.
        if (role == ProposalRole.Primary) ActiveRole = ProposalRole.Primary;
        AddHistory(new IntentHistoryEntry(intent.IntentId, intent.Source, intent.Posture, intent.PlaybookId, features.Time, null, null));
        AcknowledgeReplanAnsweredBy(intent);
        if (metrics is not null) metrics.Renewals++;
        log?.Write(new DecisionRecord(DecisionRecordKinds.IntentActivated, features.Time, features.SnapshotVersion, BotJson.ToElement(new
        {
            intentId = intent.IntentId,
            source = intent.Source,
            role,
            reason = "renewal",
            renewal = true,
            playbookId = intent.PlaybookId,
            posture = intent.Posture,
            postureFlip = false,
            confidence = intent.Confidence,
            previousIntentId = previous.IntentId,
            basedOnSnapshotVersion = intent.BasedOnSnapshotVersion,
            expiresAtFrame = intent.ExpiresAt.Frame,
            faction = features.Faction,
            featureVersion = FeatureVector.Version,
            features = FeatureVector.Encode(basis),
            featuresSnapshotVersion = basis.SnapshotVersion,
            featuresFrame = basis.Time.Frame,
            mode = features.Mode,
            intent = IntentJson.ToElement(intent),
        })));
    }

    private void End(StrategicFeatures features, string reason)
    {
        StrategicIntent ended = Active!;
        double tenure = features.Time.SecondsSince(ActiveSince);
        CloseHistory(ended.IntentId, features.Time, reason);
        Active = null;
        abortFiring = replanFiring = baseThreatFiring = false;
        log?.Write(new DecisionRecord(DecisionRecordKinds.IntentEnded, features.Time, features.SnapshotVersion, BotJson.ToElement(new
        {
            intentId = ended.IntentId,
            playbookId = ended.PlaybookId,
            posture = ended.Posture,
            reason,
            tenureSeconds = tenure,
        })));
    }

    private void RequestReplan(string reason, StrategicFeatures features)
    {
        if (ReplanRequested) return;
        ReplanRequested = true;
        ReplanReason = reason;
        replanRequestedVersion = features.SnapshotVersion;
    }

    /// <summary>Clears a pending replan request only if <paramref name="intent"/> was computed from a snapshot that already saw it.</summary>
    private void AcknowledgeReplanAnsweredBy(StrategicIntent intent)
    {
        if (ReplanRequested && intent.BasedOnSnapshotVersion < replanRequestedVersion) return;
        AcknowledgeReplan();
    }

    private void AddHistory(IntentHistoryEntry entry)
    {
        history.Add(entry);
        int excess = history.Count - Math.Max(1, Options.HistoryCapacity);
        if (excess > 0) history.RemoveRange(0, excess);
    }

    /// <summary>
    /// Keeps a renewal chain at two entries: when the entry just closed as renewed itself continued an earlier
    /// renewal, it is merged into that earlier entry (which keeps the chain's first acceptance and counts the
    /// renewal). Without this every renewal took a slot, and after <see cref="ArbiterOptions.HistoryCapacity"/>
    /// renewals the plan switches before the chain were trimmed away.
    /// </summary>
    private void FoldRenewalChain()
    {
        if (history.Count < 2) return;
        IntentHistoryEntry earlier = history[^2];
        IntentHistoryEntry renewed = history[^1];
        if (!string.Equals(earlier.EndReason, "renewed", StringComparison.Ordinal)
            || earlier.EndedAt is not { } ended || ended != renewed.AcceptedAt
            || !string.Equals(earlier.PlaybookId, renewed.PlaybookId, StringComparison.Ordinal)
            || earlier.Posture != renewed.Posture)
        {
            return;
        }
        history[^2] = earlier with
        {
            EndedAt = renewed.EndedAt,
            EndReason = renewed.EndReason,
            FoldedRenewals = earlier.FoldedRenewals + 1 + renewed.FoldedRenewals,
        };
        history.RemoveAt(history.Count - 1);
    }

    private void CloseHistory(string intentId, GameTime at, string reason)
    {
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].EndedAt is null && string.Equals(history[i].IntentId, intentId, StringComparison.Ordinal))
            {
                history[i] = history[i] with { EndedAt = at, EndReason = reason };
                return;
            }
        }
    }
}
