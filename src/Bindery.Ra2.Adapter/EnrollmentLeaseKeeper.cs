// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text.Json;

namespace Bindery.Ra2.Adapter;

/// <summary>
/// Keeps one enrollment's lease alive. The control plane expires an
/// enrollment a short while after enrollment or its last heartbeat (two
/// minutes in bindery-core) and then answers its reports with 410 Gone, so a
/// live match longer than one lease needs a heartbeat running beside it.
/// The first heartbeat is sent at once; each next one is due a third of the
/// way into the lease the control plane just granted. A failed heartbeat is
/// recorded, never thrown: if they fail until the lease runs out, the client's
/// next report meets the control plane's own lost/410 handling.
/// </summary>
internal sealed class EnrollmentLeaseKeeper : IAsyncDisposable
{
    /// <summary>Floor on the heartbeat interval, and the retry delay after a failure.</summary>
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(5);

    private readonly Func<CancellationToken, Task<DateTimeOffset>> heartbeat;
    private readonly TimeProvider time;
    private readonly CancellationTokenSource stop;
    private readonly object gate = new();
    private Task loop = Task.CompletedTask;
    private Task? stopping;
    private int count;
    private string? lastError;

    private EnrollmentLeaseKeeper(Func<CancellationToken, Task<DateTimeOffset>> heartbeat, TimeProvider time, CancellationToken cancellationToken)
    {
        this.heartbeat = heartbeat;
        this.time = time;
        stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    /// <summary>Heartbeats the control plane accepted.</summary>
    public int HeartbeatCount => Volatile.Read(ref count);

    /// <summary>The most recent failed heartbeat, or null if none failed.</summary>
    public string? LastError => Volatile.Read(ref lastError);

    /// <summary>Starts heartbeating until <see cref="StopAsync"/> or until <paramref name="cancellationToken"/> is cancelled.</summary>
    public static EnrollmentLeaseKeeper Start(Func<CancellationToken, Task<DateTimeOffset>> heartbeat, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);
        ArgumentNullException.ThrowIfNull(time);
        EnrollmentLeaseKeeper keeper = new(heartbeat, time, cancellationToken);
        keeper.loop = Task.Run(() => keeper.RunAsync(keeper.stop.Token), CancellationToken.None);
        return keeper;
    }

    public static EnrollmentLeaseKeeper Start(BinderyAdapterClient controlPlane, AdapterConfiguration configuration, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(controlPlane);
        ArgumentNullException.ThrowIfNull(configuration);
        return Start(token => controlPlane.HeartbeatAsync(configuration, token), time, cancellationToken);
    }

    /// <summary>Stops heartbeating and waits for any heartbeat in flight. Safe to call more than once.</summary>
    public Task StopAsync()
    {
        lock (gate) return stopping ??= StopCoreAsync();
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task StopCoreAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        await loop.ConfigureAwait(false);
        stop.Dispose();
    }

    internal static TimeSpan NextInterval(TimeSpan remaining)
    {
        TimeSpan third = remaining / 3;
        return third < MinimumInterval ? MinimumInterval : third;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                DateTimeOffset expiresAt = await heartbeat(cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref count);
                wait = NextInterval(expiresAt - time.GetUtcNow());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or JsonException or NotSupportedException or ArgumentException or OperationCanceledException)
            {
                Volatile.Write(ref lastError, $"{exception.GetType().Name}: {exception.Message}");
                // A lost enrollment cannot be revived; stop asking.
                if (exception is HttpRequestException { StatusCode: HttpStatusCode.Gone }) return;
                wait = MinimumInterval;
            }
            try
            {
                await Task.Delay(wait, time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
