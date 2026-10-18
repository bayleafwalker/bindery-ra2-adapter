// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Bindery.Ra2.Adapter;
using Xunit;

namespace Bindery.Ra2.Adapter.Tests;

/// <summary>
/// ERM-301: an ingest outage does not block the game, and retry is idempotent.
/// The game's side of the shipper is <see cref="CaptureBatchShipper.TryEnqueue"/>,
/// which never waits on the network; everything else happens behind it.
/// </summary>
public sealed class CaptureBatchShipperTests
{
    private const string CaptureId = "0198c2c3-4d5e-7f76-8123-456789abcdef";
    private const string Lease = "lease-token";
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_ingest_outage_does_not_block_enqueue()
    {
        // The control plane accepts the connection and never answers.
        FakeIngest ingest = new() { Hang = true };
        ManualClock clock = new(Start);
        await using CaptureBatchShipper shipper = Ship(ingest, clock, new CaptureShipperOptions(Capacity: 10_000));

        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        for (int index = 0; index < 5_000; index++)
            Assert.True(shipper.TryEnqueue(Observation(index)));
        elapsed.Stop();

        // Five thousand enqueues against a control plane that never answers
        // finish in well under a second: nothing on the game's side waited.
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1), $"enqueue took {elapsed.Elapsed}");
        Assert.True(await ingest.WaitForRequestsAsync(1));
        Assert.Equal(5_000, shipper.Summary.Offered);
    }

    [Fact]
    public async Task A_failed_batch_is_retried_byte_for_byte_under_the_same_key()
    {
        FakeIngest ingest = new();
        ingest.FailNext(HttpStatusCode.ServiceUnavailable, 2);
        ManualClock clock = new(Start);
        await using CaptureBatchShipper shipper = Ship(ingest, clock);
        for (int index = 0; index < 3; index++) shipper.TryEnqueue(Observation(index));

        await AdvanceUntilAsync(clock, () => shipper.Summary.AcknowledgedThrough == 2);

        // The first batch takes both failures; every attempt at a batch, under
        // its key, carries the same bytes. How the three events split into
        // batches depends on timing, and does not matter here.
        IngestRequest[] attempts = [.. ingest.Batches];
        IGrouping<string?, IngestRequest>[] byKey = [.. attempts.GroupBy(static attempt => attempt.IdempotencyKey)];
        Assert.Equal(3, byKey[0].Count());
        Assert.All(byKey, group => Assert.Single(group.Select(static attempt => attempt.Body).Distinct()));
        Assert.All(attempts, attempt => Assert.Equal($"Bearer {Lease}", attempt.Authorization));
        Assert.All(attempts, attempt => Assert.Equal($"/v1/captures/{CaptureId}/batches", attempt.Path));
        Assert.Equal(0UL, attempts[0].Range.First);
        Assert.Equal($"{CaptureId}:{attempts[0].Range.First}-{attempts[0].Range.Last}", attempts[0].IdempotencyKey);
        Assert.Equal(2, shipper.Summary.Retries);
        Assert.Equal(0, shipper.Summary.Dropped);
    }

    [Fact]
    public async Task Batches_respect_the_offered_event_limit_and_are_contiguous()
    {
        FakeIngest ingest = new();
        ManualClock clock = new(Start);
        await using CaptureBatchShipper shipper = Ship(ingest, clock, offer: Offer(maxBatchEvents: 4));
        for (int index = 0; index < 10; index++) shipper.TryEnqueue(Observation(index));

        await AdvanceUntilAsync(clock, () => shipper.Summary.AcknowledgedThrough == 9);
        // Batches form while observations are still arriving, so where they
        // split depends on timing; what may not vary is that each fits the
        // limit and together they cover the stream once, in order.
        List<(ulong First, ulong Last)> ranges = [.. ingest.Batches.Select(static batch => batch.Range)];
        ulong expected = 0;
        foreach ((ulong first, ulong last) in ranges)
        {
            Assert.Equal(expected, first);
            Assert.InRange(last - first + 1, 1UL, 4UL);
            expected = last + 1;
        }
        Assert.Equal(10UL, expected);
    }

    [Fact]
    public async Task A_full_spool_drops_and_the_close_makes_every_drop_explicit()
    {
        // Nothing gets through, so the spool fills: the first event is in
        // flight, four more wait in the spool, and the rest are dropped on
        // arrival.
        FakeIngest ingest = new() { Hang = true };
        ManualClock clock = new(Start);
        CaptureBatchShipper shipper = Ship(ingest, clock, new CaptureShipperOptions(Capacity: 4, FlushTimeout: TimeSpan.FromSeconds(5)), Offer(maxBatchEvents: 1));
        Assert.True(shipper.TryEnqueue(Observation(0)));
        Assert.True(await ingest.WaitForRequestsAsync(1));
        int accepted = 1;
        for (int index = 1; index < 10; index++)
            if (shipper.TryEnqueue(Observation(index))) accepted++;
        Assert.Equal(5, accepted);

        // The outage outlasts the flush: every retry fails.
        ingest.FailNext(HttpStatusCode.ServiceUnavailable, int.MaxValue);
        ingest.Hang = false;
        ingest.ReleaseHung(HttpStatusCode.ServiceUnavailable);
        Task<CaptureShipperSummary> closing = shipper.CloseAsync("match ended", CancellationToken.None);
        await AdvanceUntilAsync(clock, () => closing.IsCompleted, step: TimeSpan.FromSeconds(1));
        CaptureShipperSummary summary = await closing;
        await shipper.DisposeAsync();

        // Nothing was acknowledged, so all ten sequences are claimed as gaps.
        CloseRequest close = Assert.Single(ingest.Closes);
        Assert.Equal(9UL, close.FinalSequence);
        Assert.Equal(10UL, close.LocalDrops);
        Assert.Equal([(0UL, 9UL)], close.ObservedGaps);
        Assert.Equal(10, summary.Dropped);
        Assert.True(summary.Closed);
    }

    [Fact]
    public async Task A_stream_that_observed_nothing_closes_empty()
    {
        FakeIngest ingest = new();
        ManualClock clock = new(Start);
        CaptureBatchShipper shipper = Ship(ingest, clock);
        CaptureShipperSummary summary = await shipper.CloseAsync("no observations", CancellationToken.None);
        await shipper.DisposeAsync();

        CloseRequest close = Assert.Single(ingest.Closes);
        Assert.Null(close.FinalSequence);
        Assert.Equal(0UL, close.LocalDrops);
        Assert.Empty(close.ObservedGaps);
        Assert.Contains("\"final_sequence\":null", close.Body, StringComparison.Ordinal);
        Assert.True(summary.Closed);
        Assert.Empty(ingest.Batches);
    }

    [Fact]
    public async Task A_delivered_stream_closes_through_its_last_sequence_without_gaps()
    {
        FakeIngest ingest = new();
        ManualClock clock = new(Start);
        CaptureBatchShipper shipper = Ship(ingest, clock);
        for (int index = 0; index < 5; index++) shipper.TryEnqueue(Observation(index));
        await AdvanceUntilAsync(clock, () => shipper.Summary.AcknowledgedThrough == 4);

        CaptureShipperSummary summary = await shipper.CloseAsync("match ended", CancellationToken.None);
        await shipper.DisposeAsync();
        CloseRequest close = Assert.Single(ingest.Closes);
        Assert.Equal(4UL, close.FinalSequence);
        Assert.Equal(0UL, close.LocalDrops);
        Assert.Empty(close.ObservedGaps);
        Assert.Equal(0, summary.Dropped);
        // Once closed, the stream takes nothing more.
        Assert.False(shipper.TryEnqueue(Observation(5)));
    }

    [Fact]
    public async Task A_refused_batch_stops_shipping_instead_of_retrying_forever()
    {
        FakeIngest ingest = new();
        ingest.FailNext(HttpStatusCode.Conflict, int.MaxValue);
        ManualClock clock = new(Start);
        CaptureBatchShipper shipper = Ship(ingest, clock);
        for (int index = 0; index < 3; index++) shipper.TryEnqueue(Observation(index));
        await AdvanceUntilAsync(clock, () => shipper.Summary.Stopped);

        Assert.Single(ingest.Batches);
        Assert.Contains("409", shipper.Summary.LastError, StringComparison.Ordinal);
        Assert.Equal(3, shipper.Summary.Dropped);
        // Later observations are still taken without blocking, and counted.
        Assert.False(shipper.TryEnqueue(Observation(3)));
        Assert.Equal(4, shipper.Summary.Dropped);
        await shipper.DisposeAsync();
    }

    [Fact]
    public void Offers_carry_the_negotiated_batch_limits()
    {
        CaptureStreamOffer offer = Offer(maxBatchEvents: 7, maxBatchBytes: 1024);
        Assert.Equal(7, offer.MaxBatchEvents);
        Assert.Equal(1024, offer.MaxBatchBytes);
    }

    private static CaptureBatchShipper Ship(FakeIngest ingest, ManualClock clock, CaptureShipperOptions? options = null, CaptureStreamOffer? offer = null)
    {
        BinderyAdapterClient client = new(new HttpClient(ingest) { BaseAddress = new Uri("https://control-plane.test") });
        return CaptureBatchShipper.Start(client, Lease, offer ?? Offer(), options, clock, CancellationToken.None);
    }

    private static CaptureStreamOffer Offer(int maxBatchEvents = 4096, long maxBatchBytes = 4 << 20) =>
        new(CaptureId, ClientClass.Player, "ra2yrcpp", 1 << 20, maxBatchBytes, maxBatchEvents);

    private static RawObservation Observation(int index)
    {
        using JsonDocument payload = JsonDocument.Parse($$"""{"index":{{index}}}""");
        return new RawObservation($"local-{index}", "local-capture", (ulong)index + 1, Ra2TelemetryEventTypes.UnitCreated, "bindery.ra2-adapter", "0.1.0", Start.AddSeconds(index), payload.RootElement.Clone(), "sha256:" + new string('a', 64));
    }

    /// <summary>Advances the clock a step at a time until the condition holds, letting background work run between steps.</summary>
    private static async Task AdvanceUntilAsync(ManualClock clock, Func<bool> condition, TimeSpan? step = null)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition never held");
            await Task.Delay(5);
            clock.Advance(step ?? TimeSpan.FromMilliseconds(500));
        }
    }

    private sealed record IngestRequest(string Path, string? IdempotencyKey, string? Authorization, string Body)
    {
        public (ulong First, ulong Last) Range
        {
            get
            {
                using JsonDocument document = JsonDocument.Parse(Body);
                return (document.RootElement.GetProperty("first_sequence").GetUInt64(), document.RootElement.GetProperty("last_sequence").GetUInt64());
            }
        }
    }

    private sealed record CloseRequest(string Body, ulong? FinalSequence, ulong LocalDrops, List<(ulong, ulong)> ObservedGaps);

    /// <summary>A capture endpoint that can hang, fail, or accept, and records what it was sent.</summary>
    private sealed class FakeIngest : HttpMessageHandler
    {
        private readonly object gate = new();
        private readonly List<TaskCompletionSource<HttpStatusCode>> hung = [];
        private HttpStatusCode failStatus;
        private int failRemaining;
        private ulong acknowledgedThrough;
        private bool anyAcknowledged;

        public bool Hang { get; set; }

        public ConcurrentQueue<IngestRequest> Batches { get; } = new();

        public ConcurrentQueue<CloseRequest> Closes { get; } = new();

        public void FailNext(HttpStatusCode status, int count)
        {
            lock (gate)
            {
                failStatus = status;
                failRemaining = count;
            }
        }

        public void ReleaseHung(HttpStatusCode status)
        {
            lock (gate)
            {
                foreach (TaskCompletionSource<HttpStatusCode> waiting in hung) waiting.TrySetResult(status);
                hung.Clear();
            }
        }

        public async Task<bool> WaitForRequestsAsync(int count)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (Batches.Count < count)
            {
                if (DateTime.UtcNow > deadline) return false;
                await Task.Delay(1);
            }
            return true;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            if (path.EndsWith(":close", StringComparison.Ordinal))
            {
                using JsonDocument document = JsonDocument.Parse(body);
                JsonElement root = document.RootElement;
                ulong? final = root.GetProperty("final_sequence").ValueKind == JsonValueKind.Null ? null : root.GetProperty("final_sequence").GetUInt64();
                List<(ulong, ulong)> gaps = root.TryGetProperty("observed_gaps", out JsonElement gapElement)
                    ? [.. gapElement.EnumerateArray().Select(static gap => (gap[0].GetUInt64(), gap[1].GetUInt64()))]
                    : [];
                Closes.Enqueue(new CloseRequest(body, final, root.GetProperty("local_drops").GetUInt64(), gaps));
                return Json(HttpStatusCode.OK, new { capture_id = CaptureId, status = "closed" });
            }

            IngestRequest recorded = new(path, request.Headers.TryGetValues("Idempotency-Key", out IEnumerable<string>? keys) ? keys.Single() : null, request.Headers.Authorization?.ToString(), body);
            Batches.Enqueue(recorded);
            TaskCompletionSource<HttpStatusCode>? wait = null;
            HttpStatusCode? fail = null;
            lock (gate)
            {
                if (Hang)
                {
                    wait = new TaskCompletionSource<HttpStatusCode>(TaskCreationOptions.RunContinuationsAsynchronously);
                    hung.Add(wait);
                }
                else if (failRemaining > 0)
                {
                    failRemaining--;
                    fail = failStatus;
                }
            }
            if (wait is not null) fail = await wait.Task.WaitAsync(cancellationToken);
            if (fail is { } status) return Json(status, new { code = "UNAVAILABLE", message = "ingest is down" });

            (ulong _, ulong last) = recorded.Range;
            lock (gate)
            {
                acknowledgedThrough = anyAcknowledged ? Math.Max(acknowledgedThrough, last) : last;
                anyAcknowledged = true;
                return Json(HttpStatusCode.OK, new
                {
                    schema_version = "1.0.0",
                    capture_id = CaptureId,
                    execution_id = "0198c2c3-4d5e-7f77-8123-456789abcdef",
                    acknowledged_through = (long)acknowledgedThrough,
                    missing_ranges = Array.Empty<ulong[]>(),
                    raw_object_hash = "sha256:" + new string('b', 64),
                });
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
    }
}
