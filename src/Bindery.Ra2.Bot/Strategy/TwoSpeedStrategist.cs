// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Strategy;

/// <summary>
/// The <c>llm+fast</c> arm's primary slot: a slow strategist that chooses the
/// playbook, and a fast one that may only re-parameterise the active intent
/// between the slow strategist's turns. The scheduler runs this strategist at the
/// fast cadence (5 s in the spec); a request goes to the slow strategist when
/// there is no active intent, when the previous slow request is at least
/// <see cref="StrategicEverySeconds"/> old, or when the request is event-driven
/// (the fast strategist is not allowed to answer a changed situation), and to the
/// fast strategist otherwise.
/// </summary>
/// <remarks>
/// "Event-driven" is judged from the features: any event newer than the previous request.
/// The fast strategist is expected to reject playbook switches itself (the Claude
/// strategist's Refine mode does).
/// </remarks>
public sealed class TwoSpeedStrategist : IStrategist
{
    private readonly IStrategist slow;
    private readonly IStrategist fast;
    private GameTime? lastSlow;
    private GameTime? lastRequest;

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
        bool newEvent = lastRequest is { } previous && context.Features.Events.Any(e => e.Time > previous);
        lastRequest = now;
        bool slowDue = context.ActiveIntent is null
            || lastSlow is null
            || now.SecondsSince(lastSlow.Value) >= StrategicEverySeconds
            || newEvent;
        if (slowDue)
        {
            lastSlow = now;
            SlowRequests++;
            return slow.ProposeAsync(context, cancellationToken);
        }
        FastRequests++;
        return fast.ProposeAsync(context, cancellationToken);
    }
}
