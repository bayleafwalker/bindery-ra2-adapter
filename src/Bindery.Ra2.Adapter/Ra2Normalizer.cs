// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bindery.Ra2.Adapter;

public sealed record RawObservation(
    string EventId,
    string CaptureId,
    ulong Sequence,
    string EventType,
    string AdapterId,
    string AdapterVersion,
    DateTimeOffset ReceivedAt,
    JsonElement Payload,
    string RawObjectHash);

public sealed record SourceRange(string CaptureId, ulong FirstSequence, ulong LastSequence, string RawObjectHash);

public sealed record NormalizedObservation(
    string DerivedEventId,
    string EventType,
    JsonElement Payload,
    IReadOnlyList<string> SourceEventIds,
    IReadOnlyList<SourceRange> SourceRanges,
    string SchemaVersion,
    string AdapterVersion,
    string NormalizerId,
    string NormalizerVersion);

public sealed class Ra2Normalizer(string version = "0.1.0")
{
    public const string NormalizerId = "bindery.ra2.normalizer";
    public const string SchemaVersion = "1.0.0";
    public string Version { get; } = version;

    public IReadOnlyList<NormalizedObservation> Normalize(IEnumerable<RawObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        List<RawObservation> ordered = observations.OrderBy(static item => item.Sequence).ThenBy(static item => item.EventId, StringComparer.Ordinal).ToList();
        List<NormalizedObservation> result = new(ordered.Count);
        foreach (RawObservation observation in ordered)
        {
            string normalizedType = observation.EventType switch
            {
                Ra2TelemetryEventTypes.MatchStarted => "game.lifecycle.started",
                Ra2TelemetryEventTypes.MatchEnded => "game.lifecycle.ended",
                Ra2TelemetryEventTypes.PlayerJoined => "game.participant.joined",
                Ra2TelemetryEventTypes.UnitQueued => "game.unit.queued",
                Ra2TelemetryEventTypes.UnitCreated => "game.unit.created",
                Ra2TelemetryEventTypes.UnitDestroyed => "game.unit.destroyed",
                Ra2TelemetryEventTypes.UnitKilled => "game.unit.killed",
                Ra2TelemetryEventTypes.BuildingPlaced => "game.building.placed",
                Ra2TelemetryEventTypes.BuildingDestroyed => "game.building.destroyed",
                Ra2TelemetryEventTypes.CreditsSampled => "game.economy.credits",
                Ra2TelemetryEventTypes.PowerSampled => "game.economy.power",
                Ra2TelemetryEventTypes.OrderIssued => "game.order.issued",
                Ra2TelemetryEventTypes.SelectionChanged => "game.selection.changed",
                Ra2TelemetryEventTypes.PlayerDefeated => "game.participant.defeated",
                _ => "game.observation.unknown",
            };
            string derivedID = StableID(observation.EventId, normalizedType, Version);
            result.Add(new NormalizedObservation(
                derivedID,
                normalizedType,
                observation.Payload.Clone(),
                [observation.EventId],
                [new SourceRange(observation.CaptureId, observation.Sequence, observation.Sequence, observation.RawObjectHash)],
                SchemaVersion,
                observation.AdapterVersion,
                NormalizerId,
                Version));
        }
        return result;
    }

    private static string StableID(string sourceEventID, string normalizedType, string version)
    {
        byte[] input = Encoding.UTF8.GetBytes($"{NormalizerId}\0{version}\0{normalizedType}\0{sourceEventID}");
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }
}
