// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Serialization;

namespace Bindery.Ra2.Adapter;

/// <summary>
/// The v1 request body used to create an external-runtime match session.
/// Keep this typed so a caller cannot accidentally omit or camel-case one of
/// the coordinator-owned contract fields.
/// </summary>
public sealed record SessionCreationRequest(
    [property: JsonPropertyName("compatibility")] SessionCompatibility Compatibility,
    [property: JsonPropertyName("participant_policy")] ParticipantPolicy ParticipantPolicy,
    [property: JsonPropertyName("placement")] PlacementIntent Placement,
    [property: JsonPropertyName("capture")] CapturePolicy Capture);

public sealed record SessionCompatibility(
    [property: JsonPropertyName("game_family")] string GameFamily,
    [property: JsonPropertyName("game_version")] string GameVersion,
    [property: JsonPropertyName("game_hash")] string GameHash,
    [property: JsonPropertyName("adapter_id")] string AdapterId,
    [property: JsonPropertyName("adapter_version")] string AdapterVersion,
    [property: JsonPropertyName("mod_id")] string ModId,
    [property: JsonPropertyName("mod_hash")] string ModHash,
    [property: JsonPropertyName("map_id")] string MapId,
    [property: JsonPropertyName("map_hash")] string MapHash);

public sealed record ParticipantPolicy(
    [property: JsonPropertyName("required_players")] int RequiredPlayers,
    [property: JsonPropertyName("maximum_players")] int MaximumPlayers,
    [property: JsonPropertyName("maximum_observers")] int MaximumObservers);

public sealed record PlacementIntent(
    [property: JsonPropertyName("allowed_regions")] IReadOnlyList<string> AllowedRegions,
    [property: JsonPropertyName("latency_p95_ms")] int LatencyP95Milliseconds);

public sealed record CapturePolicy(
    [property: JsonPropertyName("semantic_events")] bool SemanticEvents,
    [property: JsonPropertyName("post_match_dump")] bool PostMatchDump,
    [property: JsonPropertyName("observer_preferred")] bool ObserverPreferred);

public sealed record RegionProbe(
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("rtt_ms")] int RttMilliseconds);

/// <summary>
/// The v1 enrollment body independent of an already-enrolled client
/// configuration. This is the input used while preparing a second client for
/// the same session.
/// </summary>
public sealed record ClientEnrollmentRequest(
    string AccountToken,
    string SessionJoinCredential,
    string SessionId,
    string ClientInstanceId,
    ClientClass ClientClass,
    AdapterIdentity Adapter,
    CompatibilityHashes Compatibility,
    IReadOnlyList<RegionProbe>? RegionProbes = null);
