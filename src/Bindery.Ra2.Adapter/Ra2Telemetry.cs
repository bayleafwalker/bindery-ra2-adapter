// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;

namespace Bindery.Ra2.Adapter;

/// <summary>
/// Raw event names emitted by the Bindery fork of the ra2yrcpp bridge. The
/// bridge owns protobuf decoding; this adapter owns stable event vocabulary
/// and provenance after an observation has crossed that boundary.
/// </summary>
public static class Ra2TelemetryEventTypes
{
    public const string MatchStarted = "ra2.match.started";
    public const string PlayerJoined = "ra2.player.joined";
    public const string UnitQueued = "ra2.unit.queued";
    public const string UnitCreated = "ra2.unit.created";
    public const string UnitDestroyed = "ra2.unit.destroyed";
    public const string UnitKilled = "ra2.unit.killed";
    public const string BuildingPlaced = "ra2.building.placed";
    public const string BuildingDestroyed = "ra2.building.destroyed";
    public const string CreditsSampled = "ra2.credits.sampled";
    public const string PowerSampled = "ra2.power.sampled";
    public const string OrderIssued = "ra2.order.issued";
    public const string SelectionChanged = "ra2.selection.changed";
    public const string PlayerDefeated = "ra2.player.defeated";
    public const string MatchEnded = "ra2.match.ended";
}

public sealed record Ra2YrcppEndpoint(string Host, int Port)
{
    public IPEndPoint ToIpEndpoint(IPAddress address) => new(address ?? throw new ArgumentNullException(nameof(address)), Port);

    public static Ra2YrcppEndpoint Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        int separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1 || !int.TryParse(value[(separator + 1)..], out int port) || port is < 1 or > 65535)
            throw new ArgumentException("telemetry endpoint must be host:port", nameof(value));
        return new Ra2YrcppEndpoint(value[..separator].Trim('[', ']'), port);
    }
}

public sealed record Ra2TelemetryCapture(
    string Protocol,
    Ra2YrcppEndpoint Endpoint,
    bool RawEventsObserved,
    long? RawEventCount,
    string? RecordingPath);

/// <summary>
/// Deliberate seam for the native ra2yrcpp fork. The protobuf/TCP framing and
/// generated messages belong to that native component, so this repository does
/// not invent a second decoder or vendor its generated code.
/// </summary>
public interface IRa2TelemetrySource
{
    Ra2TelemetryCapture Capture { get; }

    IAsyncEnumerable<RawObservation> ReadAsync(CancellationToken cancellationToken = default);
}
