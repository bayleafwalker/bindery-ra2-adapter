// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Arbitration;

namespace Bindery.Ra2.Bot.Runtime;

/// <summary>
/// Replays a recorded strategist from a decision log (invariant 6). LLM runs
/// are not reproducible by re-asking the model, so the log records every answer;
/// this strategist hands the same answers back, on the same game frame they
/// originally arrived, so a replayed match produces the same decision log.
/// </summary>
/// <remarks>
/// Requests are matched to recorded <c>strategy.proposal</c> and
/// <c>strategy.proposal_failed</c> records of one <see cref="ProposalRole"/> (and
/// optionally one strategist id) by the request's snapshot version, falling back to
/// its frame. A recorded answer is delivered when the scheduler's
/// <see cref="OnFrame"/> reaches the frame it was logged on, which reproduces the
/// original latency; recorded failures are replayed as the same outcome (null,
/// exception with the recorded message, cancellation) and recorded timeouts never
/// complete, so the scheduler times them out on the same frame. A request with no
/// recording answers null immediately and counts in <see cref="Misses"/>.
/// Unless overridden, <see cref="Id"/> and <see cref="Source"/> impersonate the recorded
/// strategist (from its <c>strategy.request</c> records), so a faithful replay writes a
/// byte-identical decision log.
/// </remarks>
public sealed class ReplayStrategist : IStrategist, IFrameAwareStrategist
{
    private readonly Dictionary<long, Queue<Entry>> byVersion = [];
    private readonly Dictionary<long, Queue<Entry>> byFrame = [];
    private readonly List<(Entry Entry, TaskCompletionSource<StrategistProposal?> Completion)> pending = [];
    private long currentFrame = long.MinValue;

    public ReplayStrategist(
        IEnumerable<DecisionRecord> records,
        ProposalRole role = ProposalRole.Primary,
        string? recordedStrategistId = null,
        string? id = null,
        IntentSource? source = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        string roleName = role.ToString();
        string? recordedId = null;
        IntentSource? recordedSource = null;
        foreach (DecisionRecord record in records)
        {
            if (recordedId is null
                && string.Equals(record.Kind, RuntimeRecordKinds.Request, StringComparison.Ordinal)
                && record.Data.ValueKind == JsonValueKind.Object
                && string.Equals(ReadString(record.Data, "role"), roleName, StringComparison.Ordinal)
                && (recordedStrategistId is null || string.Equals(ReadString(record.Data, "strategistId"), recordedStrategistId, StringComparison.Ordinal)))
            {
                recordedId = ReadString(record.Data, "strategistId");
                recordedSource = Enum.TryParse(ReadString(record.Data, "source"), out IntentSource parsed) ? parsed : null;
            }

            bool proposal = string.Equals(record.Kind, DecisionRecordKinds.Proposal, StringComparison.Ordinal);
            bool failed = string.Equals(record.Kind, DecisionRecordKinds.ProposalFailed, StringComparison.Ordinal);
            if (!proposal && !failed) continue;
            JsonElement data = record.Data;
            if (data.ValueKind != JsonValueKind.Object) continue;
            if (!string.Equals(ReadString(data, "role"), roleName, StringComparison.Ordinal)) continue;
            if (recordedStrategistId is not null && !string.Equals(ReadString(data, "strategistId"), recordedStrategistId, StringComparison.Ordinal)) continue;

            Entry entry = new(
                data.GetProperty("requestVersion").GetInt64(),
                data.GetProperty("requestFrame").GetInt64(),
                record.Time.Frame,
                proposal ? ReadProposal(data) : null,
                failed ? ReadString(data, "reason") ?? "no_opinion" : null,
                failed ? ReadString(data, "message") : null);
            Enqueue(byVersion, entry.RequestVersion, entry);
            Enqueue(byFrame, entry.RequestFrame, entry);
            Recorded++;
        }
        Id = id ?? recordedId ?? "replay";
        Source = source ?? recordedSource ?? IntentSource.Scripted;
    }

    public string Id { get; }

    public IntentSource Source { get; }

    /// <summary>Recorded answers loaded.</summary>
    public int Recorded { get; }

    /// <summary>Requests that had no recorded answer.</summary>
    public int Misses { get; private set; }

    public static ReplayStrategist FromNdjson(TextReader reader, ProposalRole role = ProposalRole.Primary, string? recordedStrategistId = null) =>
        new(DecisionLogCodec.ReadAll(reader), role, recordedStrategistId);

    public void OnFrame(GameTime now)
    {
        currentFrame = now.Frame;
        for (int i = 0; i < pending.Count;)
        {
            (Entry entry, TaskCompletionSource<StrategistProposal?> completion) = pending[i];
            if (completion.Task.IsCompleted)
            {
                pending.RemoveAt(i);
                continue;
            }
            if (entry.DeliverFrame <= now.Frame && Deliver(entry, completion))
            {
                pending.RemoveAt(i);
                continue;
            }
            i++;
        }
    }

    public Task<StrategistProposal?> ProposeAsync(StrategistContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        long frame = context.Features.Time.Frame;
        if (frame > currentFrame) currentFrame = frame;
        Entry? entry = Take(byVersion, context.Features.SnapshotVersion) ?? Take(byFrame, frame);
        if (entry is null)
        {
            Misses++;
            return Task.FromResult<StrategistProposal?>(null);
        }

        TaskCompletionSource<StrategistProposal?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (entry.DeliverFrame <= currentFrame && Deliver(entry, completion)) return completion.Task;
        cancellationToken.Register(static state => ((TaskCompletionSource<StrategistProposal?>)state!).TrySetCanceled(), completion);
        pending.Add((entry, completion));
        return completion.Task;
    }

    /// <summary>Completes a replayed request; false for a recorded timeout, which must never complete.</summary>
    private static bool Deliver(Entry entry, TaskCompletionSource<StrategistProposal?> completion)
    {
        if (entry.Proposal is not null)
        {
            completion.TrySetResult(entry.Proposal);
            return true;
        }
        switch (entry.FailureReason)
        {
            case "timeout":
                return false;
            case "exception":
                completion.TrySetException(new InvalidOperationException(entry.FailureMessage));
                return true;
            case "cancelled":
                completion.TrySetCanceled();
                return true;
            default:
                completion.TrySetResult(null);
                return true;
        }
    }

    private static Entry? Take(Dictionary<long, Queue<Entry>> index, long key)
    {
        if (!index.TryGetValue(key, out Queue<Entry>? queue)) return null;
        while (queue.Count > 0)
        {
            Entry entry = queue.Dequeue();
            if (entry.Used) continue;
            entry.Used = true;
            return entry;
        }
        return null;
    }

    private static StrategistProposal ReadProposal(JsonElement data)
    {
        StrategicIntent intent = IntentJson.FromElement(data.GetProperty("intent"));
        ProposalCost cost = data.TryGetProperty("cost", out JsonElement costJson) && costJson.ValueKind == JsonValueKind.Object
            ? costJson.Deserialize<ProposalCost>(BotJson.Options) ?? new ProposalCost(0, 0, 0, 0, null)
            : new ProposalCost(0, 0, 0, 0, null);
        return new StrategistProposal(intent, cost, ReadString(data, "rawResponse"));
    }

    private static string? ReadString(JsonElement data, string name) =>
        data.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void Enqueue(Dictionary<long, Queue<Entry>> index, long key, Entry entry)
    {
        if (!index.TryGetValue(key, out Queue<Entry>? queue)) index[key] = queue = new Queue<Entry>();
        queue.Enqueue(entry);
    }

    private sealed class Entry(long requestVersion, long requestFrame, long deliverFrame, StrategistProposal? proposal, string? failureReason, string? failureMessage)
    {
        public long RequestVersion { get; } = requestVersion;
        public long RequestFrame { get; } = requestFrame;
        public long DeliverFrame { get; } = deliverFrame;
        public StrategistProposal? Proposal { get; } = proposal;
        public string? FailureReason { get; } = failureReason;
        public string? FailureMessage { get; } = failureMessage;
        public bool Used { get; set; }
    }
}
