// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Strategy;

/// <summary>
/// The <c>llm+fast</c> arm's primary slot: a slow strategist that chooses the
/// playbook, and a fast one that may only re-parameterise the active intent
/// between the slow strategist's turns. The scheduler runs this strategist at the
/// fast cadence (5 s in the spec). A request goes to the fast strategist only while
/// the active intent is the slow strategist's own choice (or a refinement of it), the
/// slow strategist was served less than <see cref="StrategicEverySeconds"/> ago, and the
/// request carries no new event; every other request goes to the slow strategist.
/// </summary>
/// <remarks>
/// <para>What became of a proposal is read from the next request's <see cref="StrategistContext.ActiveIntent"/>:
/// the scheduler starts a primary request only after the previous one has been collected, so by then the arbiter
/// has applied it (the active intent carries its id) or not (failed, null, late-discarded, rejected or refused).
/// Reading it there keeps this class deterministic without any callback from the runtime.</para>
/// <para>Two consequences. A fallback or emergency placeholder is never handed to the fast strategist: its
/// renewal would promote the placeholder to a primary commitment and lock out the slow strategist's next choice,
/// and the fast strategist is not allowed to choose a playbook in its place. And a slow request that produced
/// nothing (failure, no opinion) does not count as the slow strategist's turn. A slow answer that was delivered
/// but not applied while the slow strategist's own commitment is active counts as its turn (the arbiter kept
/// the commitment on purpose), so a refused switch does not turn every fast slot into a slow call.</para>
/// <para>"Event-driven" is judged from the request's <see cref="StrategistContext.Trigger"/>: every
/// <c>event:*</c> and <c>replan:*</c> request goes to the slow strategist, including one the scheduler deferred
/// until after an in-flight request, whose features no longer show the event (and so also when the slow
/// strategist's own intent is active while the event discarded its answer). Contexts without a trigger fall back to
/// the features: any event newer than the previous request.</para>
/// </remarks>
public sealed class TwoSpeedStrategist : IStrategist
{
    private readonly IStrategist slow;
    private readonly IStrategist fast;
    private GameTime? lastSlowServed;
    private GameTime? lastRequest;
    private string? ownedIntentId;
    private PendingRequest? pending;

    public TwoSpeedStrategist(IStrategist slow, IStrategist fast, double strategicEverySeconds = 20, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(slow);
        ArgumentNullException.ThrowIfNull(fast);
        this.slow = slow;
        this.fast = fast;
        StrategicEverySeconds = strategicEverySeconds;
        Id = id ?? $"{slow.Id}+{fast.Id}";
    }

    public string Id { get; }

    public IntentSource Source => slow.Source;

    public double StrategicEverySeconds { get; }

    public int SlowRequests { get; private set; }

    public int FastRequests { get; private set; }

    public Task<StrategistProposal?> ProposeAsync(StrategistContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        GameTime now = context.Features.Time;
        StrategicIntent? active = context.ActiveIntent;
        Settle(active);

        bool owned = active is not null && string.Equals(active.IntentId, ownedIntentId, StringComparison.Ordinal);
        bool triggered = context.Trigger is { } trigger
            && (trigger.StartsWith("event:", StringComparison.Ordinal) || trigger.StartsWith("replan:", StringComparison.Ordinal));
        bool newEvent = triggered || (lastRequest is { } previous && context.Features.Events.Any(e => e.Time > previous));
        lastRequest = now;
        bool slowDue = !owned
            || lastSlowServed is null
            || now.SecondsSince(lastSlowServed.Value) >= StrategicEverySeconds
            || newEvent;

        Task<StrategistProposal?> task;
        if (slowDue)
        {
            SlowRequests++;
            task = slow.ProposeAsync(context, cancellationToken);
        }
        else
        {
            FastRequests++;
            task = fast.ProposeAsync(context, cancellationToken);
        }
        pending = new PendingRequest(task, slowDue, now, owned);
        return task;
    }

    /// <summary>Resolves the previous request against the intent that is active now.</summary>
    private void Settle(StrategicIntent? active)
    {
        if (pending is not { } previous) return;
        pending = null;
        StrategistProposal? result = previous.Task.IsCompletedSuccessfully ? previous.Task.Result : null;
        bool applied = result is not null && active is not null
            && string.Equals(active.IntentId, result.Intent.IntentId, StringComparison.Ordinal);
        bool stillOwned = active is not null && string.Equals(active.IntentId, ownedIntentId, StringComparison.Ordinal);
        if (previous.Slow)
        {
            if (applied) ownedIntentId = active!.IntentId;
            if (applied || (result is not null && stillOwned)) lastSlowServed = previous.AskedAt;
        }
        else if (applied && previous.Owned)
        {
            // A refinement of the slow strategist's choice (a renewal) stays the slow strategist's commitment.
            ownedIntentId = active!.IntentId;
        }
    }

    private sealed record PendingRequest(Task<StrategistProposal?> Task, bool Slow, GameTime AskedAt, bool Owned);
}
