// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bindery.Ra2.Bot.Runtime;

/// <summary>
/// In-memory decision log: the evidence every arena metric is computed from and
/// the input a <see cref="ReplayStrategist"/> replays. Written only from the tick
/// thread; not thread-safe.
/// </summary>
public sealed class DecisionLog : IDecisionLog
{
    private readonly List<DecisionRecord> records = [];

    public IReadOnlyList<DecisionRecord> Records => records;

    public void Write(DecisionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        records.Add(record);
    }

    /// <summary>Records of one kind, in write order.</summary>
    public IReadOnlyList<DecisionRecord> OfKind(string kind) =>
        records.Where(r => string.Equals(r.Kind, kind, StringComparison.Ordinal)).ToList();

    /// <summary>SHA-256 of the NDJSON form of the log; see <see cref="DecisionLogCodec.Hash"/>.</summary>
    public string ComputeHash() => DecisionLogCodec.Hash(records);

    public string ToNdjson()
    {
        StringBuilder builder = new();
        foreach (DecisionRecord record in records) builder.Append(DecisionLogCodec.ToLine(record)).Append('\n');
        return builder.ToString();
    }
}

/// <summary>
/// The one serialised form of a decision record, used for files, hashing and
/// replay: one compact JSON object per line,
/// <c>{"kind":…,"frame":…,"snapshotVersion":…,"data":…}</c>, UTF-8, <c>\n</c>
/// separated. Property order is fixed and payloads are written verbatim, so
/// equal logs hash equal on every machine and locale.
/// </summary>
public static class DecisionLogCodec
{
    public static string ToLine(DecisionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArrayBufferWriter<byte> buffer = new();
        Write(record, buffer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static DecisionRecord FromLine(string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        return new DecisionRecord(
            root.GetProperty("kind").GetString() ?? throw new FormatException("Decision record has no kind."),
            new GameTime(root.GetProperty("frame").GetInt64()),
            root.GetProperty("snapshotVersion").GetInt64(),
            root.TryGetProperty("data", out JsonElement data) ? data.Clone() : default);
    }

    /// <summary>Reads every non-blank line of an NDJSON decision log.</summary>
    public static IReadOnlyList<DecisionRecord> ReadAll(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        List<DecisionRecord> records = [];
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line)) records.Add(FromLine(line));
        }
        return records;
    }

    /// <summary>
    /// Lower-case hex SHA-256 over the NDJSON bytes of <paramref name="records"/>
    /// (each line followed by <c>\n</c>). Two runs are identical exactly when their hashes are.
    /// </summary>
    public static string Hash(IEnumerable<DecisionRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        ArrayBufferWriter<byte> buffer = new();
        foreach (DecisionRecord record in records)
        {
            buffer.Clear();
            Write(record, buffer);
            hash.AppendData(buffer.WrittenSpan);
            hash.AppendData("\n"u8);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Write(DecisionRecord record, IBufferWriter<byte> output)
    {
        using Utf8JsonWriter writer = new(output, new JsonWriterOptions { Indented = false, SkipValidation = false });
        writer.WriteStartObject();
        writer.WriteString("kind", record.Kind);
        writer.WriteNumber("frame", record.Time.Frame);
        writer.WriteNumber("snapshotVersion", record.SnapshotVersion);
        writer.WritePropertyName("data");
        if (record.Data.ValueKind == JsonValueKind.Undefined) writer.WriteNullValue();
        else record.Data.WriteTo(writer);
        writer.WriteEndObject();
    }
}

/// <summary>
/// Streams each record as an NDJSON line as it is written, optionally also
/// keeping it in an inner log (so a live run is both on disk and queryable).
/// <see cref="Records"/> is the inner log's records, or empty without one.
/// </summary>
public sealed class NdjsonDecisionLogWriter : IDecisionLog, IDisposable
{
    private readonly TextWriter writer;
    private readonly IDecisionLog? inner;
    private readonly bool leaveOpen;
    private readonly bool flushEachRecord;

    public NdjsonDecisionLogWriter(TextWriter writer, IDecisionLog? inner = null, bool leaveOpen = false, bool flushEachRecord = false)
    {
        ArgumentNullException.ThrowIfNull(writer);
        this.writer = writer;
        this.inner = inner;
        this.leaveOpen = leaveOpen;
        this.flushEachRecord = flushEachRecord;
    }

    /// <summary>Writes UTF-8 (no BOM) NDJSON to a stream.</summary>
    public NdjsonDecisionLogWriter(Stream stream, IDecisionLog? inner = null, bool leaveOpen = false, bool flushEachRecord = false)
        : this(new StreamWriter(stream ?? throw new ArgumentNullException(nameof(stream)), new UTF8Encoding(false), 4096, leaveOpen) { NewLine = "\n" },
               inner, leaveOpen: false, flushEachRecord)
    {
    }

    public IReadOnlyList<DecisionRecord> Records => inner?.Records ?? [];

    public void Write(DecisionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        inner?.Write(record);
        writer.Write(DecisionLogCodec.ToLine(record));
        writer.Write('\n');
        if (flushEachRecord) writer.Flush();
    }

    public void Flush() => writer.Flush();

    public void Dispose()
    {
        writer.Flush();
        if (!leaveOpen) writer.Dispose();
    }
}
