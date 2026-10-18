// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bindery.Ra2.Adapter;

/// <summary>How a <see cref="CaptureBatchShipper"/> buffers and gives up.</summary>
/// <param name="Capacity">Observations waiting behind the batch in flight while the control plane is unreachable; beyond it they are dropped and counted.</param>
/// <param name="FlushTimeout">How long closing may wait for the buffer to drain, and separately for the close itself.</param>
/// <param name="InitialBackoff">First wait after a failed batch; it doubles up to <paramref name="MaxBackoff"/>.</param>
/// <param name="MaxBackoff">Longest wait between attempts at one batch.</param>
public sealed record CaptureShipperOptions(
    int Capacity = 10_000,
    TimeSpan? FlushTimeout = null,
    TimeSpan? InitialBackoff = null,
    TimeSpan? MaxBackoff = null)
{
    internal TimeSpan EffectiveFlushTimeout => FlushTimeout ?? TimeSpan.FromSeconds(10);

    internal TimeSpan EffectiveInitialBackoff => InitialBackoff ?? TimeSpan.FromSeconds(1);

    internal TimeSpan EffectiveMaxBackoff => MaxBackoff ?? TimeSpan.FromSeconds(30);
}

/// <summary>What a shipper did, for evidence. Never thrown; always readable.</summary>
public sealed record CaptureShipperSummary(
    string CaptureId,
    long Offered,
    long AcknowledgedThrough,
    long Dropped,
    int BatchesAcknowledged,
    int Retries,
    bool Stopped,
    bool Closed,
    string? LastError,
    string? CloseError);

/// <summary>
/// Streams one client's semantic observations into its capture without ever
/// making the game wait. <see cref="TryEnqueue"/> is the only call on the
/// game's side: it takes a lock, appends to a bounded spool and returns. A
/// background loop forms contiguous batches within the offer's limits and
/// sends each until it is acknowledged, resending the same bytes under the
/// same idempotency key, so a retry after a lost response is a no-op on the
/// control plane.
/// </summary>
/// <remarks>
/// The shipper owns the stream's sequence numbers, from zero, because the
/// control plane counts completeness from zero and a telemetry source's own
/// numbering is local to it. Every observation offered gets a sequence, even
/// one that is then dropped, so a drop is a gap the close can name rather than
/// something that silently never happened. When the control plane refuses a
/// batch outright (a 4xx other than 408 or 429), resending it cannot help:
/// shipping stops, and everything not yet acknowledged is counted as dropped.
/// </remarks>
public sealed class CaptureBatchShipper : IAsyncDisposable
{
    // Room for what the control plane adds to each event when it encodes the
    // batch canonically: ids, adapter identity, receive time.
    private const int CanonicalOverheadPerEvent = 512;
    private const long DefaultMaxBatchBytes = 4 << 20;
    private const int DefaultMaxBatchEvents = 4096;

    private static readonly JsonSerializerOptions Wire = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly BinderyAdapterClient client;
    private readonly string leaseToken;
    private readonly CaptureStreamOffer offer;
    private readonly CaptureShipperOptions options;
    private readonly TimeProvider time;
    private readonly CancellationTokenSource stop;
    private readonly SemaphoreSlim signal = new(0);
    private readonly object gate = new();
    private readonly Queue<WireEvent> spool = new();
    private readonly List<SequenceRange> dropped = [];
    private readonly long batchBytes;
    private readonly int batchEvents;
    private Task loop = Task.CompletedTask;
    private CaptureBatch? inFlight;
    private ulong nextSequence;
    private long offered;
    private long droppedCount;
    private long acknowledgedThrough = -1;
    private int batchesAcknowledged;
    private int retries;
    private bool closing;
    private bool stopped;
    private bool closed;
    private string? lastError;
    private string? closeError;
    private Task<CaptureShipperSummary>? closeTask;

    private CaptureBatchShipper(BinderyAdapterClient client, string leaseToken, CaptureStreamOffer offer, CaptureShipperOptions options, TimeProvider time, CancellationToken cancellationToken)
    {
        this.client = client;
        this.leaseToken = leaseToken;
        this.offer = offer;
        this.options = options;
        this.time = time;
        stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        batchBytes = offer.MaxBatchBytes > 0 ? offer.MaxBatchBytes : DefaultMaxBatchBytes;
        batchEvents = offer.MaxBatchEvents > 0 ? offer.MaxBatchEvents : DefaultMaxBatchEvents;
    }

    public static CaptureBatchShipper Start(
        BinderyAdapterClient client,
        string leaseToken,
        CaptureStreamOffer offer,
        CaptureShipperOptions? options,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(time);
        options ??= new CaptureShipperOptions();
        if (options.Capacity < 1) throw new ArgumentOutOfRangeException(nameof(options), "capacity must be positive");
        CaptureBatchShipper shipper = new(client, leaseToken, offer, options, time, cancellationToken);
        shipper.loop = Task.Run(shipper.RunAsync, CancellationToken.None);
        return shipper;
    }

    public string CaptureId => offer.CaptureId;

    public CaptureShipperSummary Summary
    {
        get
        {
            lock (gate)
                return new CaptureShipperSummary(offer.CaptureId, offered, acknowledgedThrough, droppedCount, batchesAcknowledged, retries, stopped, closed, lastError, closeError);
        }
    }

    /// <summary>
    /// Hands one observation to the stream. Never waits: it returns false when
    /// the observation was dropped -- the spool is full, shipping stopped -- or
    /// when the stream is closing and takes nothing more.
    /// </summary>
    public bool TryEnqueue(RawObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (gate)
        {
            if (closing) return false;
            ulong sequence = nextSequence++;
            offered++;
            if (stopped || spool.Count >= options.Capacity)
            {
                DropLocked(sequence, sequence);
                return false;
            }
            spool.Enqueue(new WireEvent(
                $"{offer.CaptureId}:{sequence:D12}",
                sequence,
                null,
                observation.ReceivedAt,
                observation.EventType,
                null,
                observation.Payload.Clone()));
        }
        Wake();
        return true;
    }

    /// <summary>
    /// Stops taking observations, waits up to the flush timeout for the spool
    /// to drain, then closes the capture with an account of everything that
    /// did not arrive. Never throws for a control-plane failure: the result
    /// says what happened. Safe to call more than once.
    /// </summary>
    public Task<CaptureShipperSummary> CloseAsync(string endReason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endReason);
        lock (gate) return closeTask ??= CloseCoreAsync(endReason, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate) closing = true;
        await stop.CancelAsync().ConfigureAwait(false);
        await QuietlyAsync(loop).ConfigureAwait(false);
        stop.Dispose();
        signal.Dispose();
    }

    private async Task<CaptureShipperSummary> CloseCoreAsync(string endReason, CancellationToken cancellationToken)
    {
        lock (gate) closing = true;
        Wake();
        Task drained = await Task.WhenAny(loop, Task.Delay(options.EffectiveFlushTimeout, time, cancellationToken)).ConfigureAwait(false);
        if (drained != loop)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await QuietlyAsync(loop).ConfigureAwait(false);
        }

        CaptureCloseRequest close;
        lock (gate)
        {
            // Whatever is still held never reached the control plane, or did
            // without an acknowledgement. Claiming it as dropped is the
            // conservative account: the completeness gate fails a stream that
            // dropped anything, which is the right answer when we cannot tell.
            if (inFlight is { } unacknowledged) DropLocked(unacknowledged.FirstSequence, unacknowledged.LastSequence);
            inFlight = null;
            while (spool.Count > 0)
            {
                WireEvent pending = spool.Dequeue();
                DropLocked(pending.Sequence, pending.Sequence);
            }
            ulong? finalSequence = nextSequence == 0 ? null : nextSequence - 1;
            close = new CaptureCloseRequest(finalSequence, [.. dropped], (ulong)droppedCount, endReason);
        }

        using CancellationTokenSource deadline = new(options.EffectiveFlushTimeout, time);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        try
        {
            await client.CloseCaptureAsync(leaseToken, offer, close, linked.Token).ConfigureAwait(false);
            lock (gate) closed = true;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or InvalidOperationException or JsonException)
        {
            lock (gate) closeError = Describe(exception);
        }
        return Summary;
    }

    private async Task RunAsync()
    {
        TimeSpan backoff = options.EffectiveInitialBackoff;
        CancellationToken cancellationToken = stop.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            CaptureBatch? batch;
            lock (gate)
            {
                batch = inFlight ??= FormBatchLocked();
                if (batch is null && closing) return;
            }
            if (batch is null)
            {
                try { await signal.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            try
            {
                CaptureReceipt receipt = await client.IngestCaptureBatchAsync(leaseToken, offer, batch, cancellationToken).ConfigureAwait(false);
                lock (gate)
                {
                    inFlight = null;
                    batchesAcknowledged++;
                    acknowledgedThrough = Math.Max(acknowledgedThrough, receipt.AcknowledgedThrough);
                }
                backoff = options.EffectiveInitialBackoff;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (IsRetryable(exception))
            {
                lock (gate)
                {
                    retries++;
                    lastError = Describe(exception);
                }
                try { await Task.Delay(backoff, time, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, options.EffectiveMaxBackoff.Ticks));
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or JsonException)
            {
                lock (gate)
                {
                    lastError = Describe(exception);
                    stopped = true;
                    if (inFlight is { } refused) DropLocked(refused.FirstSequence, refused.LastSequence);
                    inFlight = null;
                    while (spool.Count > 0)
                    {
                        WireEvent pending = spool.Dequeue();
                        DropLocked(pending.Sequence, pending.Sequence);
                    }
                }
                return;
            }
        }
    }

    /// <summary>
    /// Takes the longest contiguous run from the head of the spool that fits
    /// the offer's limits. A drop leaves a hole in the sequence, and a batch
    /// may not span one, so a batch ends at the first gap.
    /// </summary>
    private CaptureBatch? FormBatchLocked()
    {
        while (spool.Count > 0)
        {
            List<WireEvent> events = [];
            long budget = batchBytes / 2;
            long used = 0;
            while (spool.Count > 0 && events.Count < batchEvents)
            {
                WireEvent next = spool.Peek();
                if (events.Count > 0 && next.Sequence != events[^1].Sequence + 1) break;
                long size = JsonSerializer.SerializeToUtf8Bytes(next, Wire).Length + CanonicalOverheadPerEvent;
                if (events.Count > 0 && used + size > budget) break;
                spool.Dequeue();
                if (size > budget)
                {
                    // One observation larger than any batch can carry.
                    DropLocked(next.Sequence, next.Sequence);
                    continue;
                }
                events.Add(next);
                used += size;
            }
            if (events.Count == 0) continue;
            ulong first = events[0].Sequence;
            ulong last = events[^1].Sequence;
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(new WireBatch(first, last, events), Wire);
            return new CaptureBatch($"{offer.CaptureId}:{first}-{last}", first, last, events.Count, body);
        }
        return null;
    }

    private void DropLocked(ulong first, ulong last)
    {
        droppedCount += (long)(last - first + 1);
        // Drops do not arrive in order -- an in-flight batch is dropped at
        // close, after later single events may already have been -- so the
        // account is kept sorted and merged rather than appended to.
        dropped.Add(new SequenceRange(first, last));
        dropped.Sort(static (a, b) => a.First.CompareTo(b.First));
        for (int index = dropped.Count - 1; index > 0; index--)
        {
            SequenceRange earlier = dropped[index - 1];
            SequenceRange later = dropped[index];
            if (earlier.Last + 1 < later.First) continue;
            dropped[index - 1] = new SequenceRange(earlier.First, Math.Max(earlier.Last, later.Last));
            dropped.RemoveAt(index);
        }
    }

    private void Wake()
    {
        try
        {
            if (signal.CurrentCount == 0) signal.Release();
        }
        catch (ObjectDisposedException)
        {
            // Disposed: there is no loop left to wake.
        }
    }

    /// <summary>
    /// Worth trying again: the network, a timeout, the control plane
    /// overloaded or failing. A refusal of the batch itself is not.
    /// </summary>
    private static bool IsRetryable(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } status } => (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests,
        TaskCanceledException => true,
        _ => false,
    };

    private static string Describe(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } status } => $"{(int)status} {status}: {exception.Message}",
        _ => $"{exception.GetType().Name}: {exception.Message}",
    };

    private static async Task QuietlyAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private sealed record WireEvent(
        [property: JsonPropertyName("event_id")] string EventId,
        [property: JsonPropertyName("sequence")] ulong Sequence,
        [property: JsonPropertyName("game_tick")] ulong? GameTick,
        [property: JsonPropertyName("producer_time")] DateTimeOffset? ProducerTime,
        [property: JsonPropertyName("event_type")] string EventType,
        [property: JsonPropertyName("payload_version")] string? PayloadVersion,
        [property: JsonPropertyName("payload")] JsonElement Payload);

    private sealed record WireBatch(
        [property: JsonPropertyName("first_sequence")] ulong FirstSequence,
        [property: JsonPropertyName("last_sequence")] ulong LastSequence,
        [property: JsonPropertyName("events")] IReadOnlyList<WireEvent> Events);
}
