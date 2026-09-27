// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Adapter.Channel;

/// <summary>
/// Where the channel is in its loop. One session runs matches back to back
/// until it is asked to drain or runs out of matches.
/// </summary>
public enum ChannelPhase
{
    Idle,
    /// <summary>Between matches: the holding scene is on air.</summary>
    Holding,
    /// <summary>A match is being prepared and launched.</summary>
    Starting,
    /// <summary>A match is running and its capture source is on air.</summary>
    OnAir,
    /// <summary>A match has ended; its record is being written.</summary>
    Recording,
    /// <summary>No further matches will start; resources are being released.</summary>
    Draining,
    Stopped,
}

/// <summary>What happens after each match.</summary>
public enum ChannelContinuation
{
    /// <summary>Start the next match, up to <see cref="ChannelRequest.MaximumMatches"/>.</summary>
    Continue,
    /// <summary>Finish the current match, then stop.</summary>
    DrainAfterMatch,
}

/// <summary>
/// Which rendered client supplies the picture and sound.
/// </summary>
/// <remarks>
/// The broadcast is a capture of a rendered game window. The ra2yrcpp stream
/// is game data -- state and events for the agent, the record and an overlay
/// -- and is never the video feed. A first broadcast captures a player's own
/// view; a spectator client is the neutral view once its path is proven.
/// </remarks>
public sealed record CaptureSource(string ClientInstanceId, ClientClass ClientClass)
{
    public void Validate() => ArgumentException.ThrowIfNullOrWhiteSpace(ClientInstanceId);
}

/// <summary>
/// "Start the RA2 channel": what to play, where to show it, and whether a
/// seat is driven by an agent controller.
/// </summary>
public sealed record ChannelRequest(
    string ChannelId,
    string MapId,
    CaptureSource Capture,
    BroadcastPlan Broadcast,
    int MaximumMatches = 1,
    string? AgentSeatHouse = null,
    TimeSpan? HoldingDuration = null)
{
    /// <summary>Consecutive failed matches after which the channel drains rather than retry.</summary>
    public const int MaximumConsecutiveFailures = 2;

    public TimeSpan EffectiveHoldingDuration => HoldingDuration ?? TimeSpan.FromSeconds(20);

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ChannelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(MapId);
        ArgumentNullException.ThrowIfNull(Capture);
        ArgumentNullException.ThrowIfNull(Broadcast);
        Capture.Validate();
        Broadcast.Validate();
        if (MaximumMatches < 1) throw new ArgumentOutOfRangeException(nameof(MaximumMatches), "a channel runs at least one match");
        if (HoldingDuration is { } holding && holding < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(HoldingDuration));
        if (AgentSeatHouse is not null) ArgumentException.ThrowIfNullOrWhiteSpace(AgentSeatHouse);
    }
}

/// <summary>How a match ended, as far as the evidence can say.</summary>
public enum ChannelMatchOutcome
{
    /// <summary>Every client ran and exited cleanly and the control plane agrees.</summary>
    Completed,
    /// <summary>The clients exited but the lifecycle evidence is incomplete.</summary>
    Incomplete,
    /// <summary>The match could not be prepared or launched.</summary>
    Failed,
}

/// <summary>
/// One match as the channel keeps it: enough to reproduce it and to compare
/// agent playbooks across matches.
/// </summary>
/// <remarks>
/// <paramref name="Winner"/> stays null unless the telemetry actually named
/// one. Exit codes do not: Yuri's Revenge exits the same way for both sides.
/// The telemetry fields are null when no instrumented stream was attached.
/// </remarks>
public sealed record ChannelMatchRecord(
    string ChannelId,
    int MatchIndex,
    string? RunId,
    string? SessionId,
    string MapId,
    int? Seed,
    string? GoldenApplianceId,
    IReadOnlyList<string> ClientExecutableHashes,
    string AdapterId,
    string AdapterVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    ChannelMatchOutcome Outcome,
    string? Winner,
    string? EvidencePath,
    string? ReplayPath,
    string? DecisionTracePath,
    int PlaybookRevisions,
    CaptureSource Capture,
    bool PublishedPublicly,
    string? Failure = null,
    long? TelemetryEvents = null,
    bool? TelemetrySawMatchEnd = null,
    string? TelemetryIssue = null,
    // Set when a spectator client failed; the players' match stands.
    string? ObserverIssue = null);

/// <summary>What one channel session did, in order.</summary>
public sealed record ChannelSessionSummary(
    string ChannelId,
    IReadOnlyList<ChannelMatchRecord> Matches,
    IReadOnlyList<ChannelPhase> Phases,
    string StopReason);
