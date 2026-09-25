// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Adapter.Bot;

/// <summary>
/// One occurrence of a <see cref="Ra2BotTelemetryContract"/> field the
/// telemetry payload did not carry. <see cref="Effect"/> is a stable,
/// human-readable description of what the assembler did instead of
/// inventing a value (it never invents one) — for example
/// <c>"entity_excluded"</c> or <c>"event_skipped"</c>.
/// </summary>
public sealed record MissingFieldEntry(string EventType, string Field, string Effect, string DerivedEventId);

/// <summary>
/// Accumulated record of every telemetry field <see cref="Ra2ObservationAssembler"/>
/// expected under <see cref="Ra2BotTelemetryContract"/> but did not receive.
/// This is the audit trail the spec requires: a missing field never becomes a
/// guessed value, it becomes an entry here plus an exclusion, and the count
/// is the thing an operator diagnosing a telemetry-schema drift reads first.
/// </summary>
public sealed record MissingFieldsReport(IReadOnlyList<MissingFieldEntry> Entries)
{
    /// <summary>Occurrence count per field name, ordered by field name for determinism.</summary>
    public IReadOnlyDictionary<string, int> CountsByField =>
        Entries
            .GroupBy(static e => e.Field, StringComparer.Ordinal)
            .OrderBy(static g => g.Key, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.Count(), StringComparer.Ordinal);

    public static MissingFieldsReport Empty { get; } = new([]);
}
