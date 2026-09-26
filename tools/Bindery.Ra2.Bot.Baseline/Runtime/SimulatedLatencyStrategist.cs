// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace Bindery.Ra2.Bot.Baseline.Runtime;

/// <summary>
/// Makes a slow (typically LLM) strategist latency-faithful in a simulator that runs
/// faster than real time. Each request is answered by the inner strategist while the
/// simulation waits (so the result does not depend on thread scheduling), and the
/// answer is then released to the scheduler only once game time has advanced by the
/// request's latency: the measured wall-clock latency (optionally scaled), or a fixed
/// simulated latency when one is configured (for fake clients that answer instantly).
/// </summary>
/// <remarks>
/// The released proposal's <see cref="ProposalCost.LatencySeconds"/> is the game-time
/// latency applied. With a fixed latency and a deterministic inner strategist the whole
/// match is deterministic; with measured latency it reproduces what a live match would
/// have seen at that speed. A request that is still held when it is cancelled completes
/// as cancelled.
/// </remarks>
public sealed class SimulatedLatencyStrategist : IStrategist, IFrameAwareStrategist
{
    private readonly IStrategist inner;
    private readonly double? fixedLatencySeconds;
    private readonly double wallScale;
    private readonly TimeSpan wallTimeout;
    private readonly List<(long DueFrame, TaskCompletionSource<StrategistProposal?> Completion)> pending = [];
    private readonly Dictionary<TaskCompletionSource<StrategistProposal?>, StrategistProposal?> held = [];
    private readonly object gate = new();

    /// <param name="fixedLatencySeconds">Game seconds every answer takes; null to use the measured wall-clock latency.</param>
    /// <param name="wallScale">Multiplier on measured latency (1 = real time).</param>
    /// <param name="wallTimeoutSeconds">Longest wall-clock wait for the inner strategist.</param>
    public SimulatedLatencyStrategist(IStrategist inner, double? fixedLatencySeconds = null, double wallScale = 1.0, double wallTimeoutSeconds = 120)
    {
        ArgumentNullException.ThrowIfNull(inner);
        this.inner = inner;
        this.fixedLatencySeconds = fixedLatencySeconds;
        this.wallScale = wallScale;
        wallTimeout = TimeSpan.FromSeconds(wallTimeoutSeconds);
    }

    public string Id => inner.Id;

    public IntentSource Source => inner.Source;

    public IStrategist Inner => inner;

    public void OnFrame(GameTime now)
    {
        List<TaskCompletionSource<StrategistProposal?>> due = [];
        lock (gate)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (pending[i].DueFrame <= now.Frame)
                {
                    due.Add(pending[i].Completion);
                    pending.RemoveAt(i);
                }
            }
        }
        due.Reverse();
        foreach (TaskCompletionSource<StrategistProposal?> completion in due) completion.TrySetResult(Held(completion));
    }

    public Task<StrategistProposal?> ProposeAsync(StrategistContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Stopwatch watch = Stopwatch.StartNew();
        StrategistProposal? proposal;
        try
        {
            Task<StrategistProposal?> task = inner.ProposeAsync(context, cancellationToken);
            proposal = task.Wait(wallTimeout, cancellationToken) ? task.Result : null;
        }
        catch (OperationCanceledException)
        {
            return Task.FromCanceled<StrategistProposal?>(cancellationToken.IsCancellationRequested ? cancellationToken : new CancellationToken(true));
        }
        catch (AggregateException ex) when (ex.InnerException is not null)
        {
            return Task.FromException<StrategistProposal?>(ex.InnerException);
        }
        double latency = fixedLatencySeconds ?? watch.Elapsed.TotalSeconds * wallScale;
        long due = context.Features.Time.Plus(Math.Max(0, latency)).Frame;
        StrategistProposal? released = proposal is null ? null : proposal with { Cost = proposal.Cost with { LatencySeconds = latency } };

        TaskCompletionSource<StrategistProposal?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            held[completion] = released;
            pending.Add((due, completion));
        }
        cancellationToken.Register(() =>
        {
            lock (gate)
            {
                pending.RemoveAll(p => ReferenceEquals(p.Completion, completion));
                held.Remove(completion);
            }
            completion.TrySetCanceled(cancellationToken);
        });
        if (due <= context.Features.Time.Frame) OnFrame(context.Features.Time);
        return completion.Task;
    }

    private StrategistProposal? Held(TaskCompletionSource<StrategistProposal?> completion)
    {
        lock (gate)
        {
            held.Remove(completion, out StrategistProposal? proposal);
            return proposal;
        }
    }
}
