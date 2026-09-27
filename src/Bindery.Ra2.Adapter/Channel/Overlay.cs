// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Bindery.Ra2.Adapter.Channel;

public sealed record OverlayHouse(string House, long? Credits, bool Defeated);

/// <summary>What the viewer sees on top of the capture: a scoreboard from the instrumented stream.</summary>
public sealed record OverlayState(
    string ChannelId,
    int MatchIndex,
    string MapId,
    TimeSpan Elapsed,
    IReadOnlyList<OverlayHouse> Houses,
    bool Ended,
    string? Winner);

/// <summary>Where the overlay goes: an OBS text source, a file, a test.</summary>
public interface IOverlaySink
{
    Task UpdateAsync(OverlayState state, CancellationToken cancellationToken);
}

/// <summary>
/// Builds the scoreboard from the full instrumented stream.
/// </summary>
/// <remarks>
/// This is the spectator view by design -- it is what the audience sees. It
/// must never be handed to a player controller; agents read through
/// <see cref="PlayerObservationFilter"/> only.
/// </remarks>
public sealed class MatchOverlay(ChannelMatchContext context, PayloadFields? fields = null)
{
    private readonly PayloadFields fields = fields ?? new PayloadFields();
    private readonly MatchTelemetryTracker tracker = new();
    private readonly SortedDictionary<string, long?> credits = new(StringComparer.Ordinal);
    private DateTimeOffset? startedAt;
    private DateTimeOffset? lastAt;

    public void Observe(RawObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        tracker.Observe(observation);
        startedAt ??= observation.ReceivedAt;
        lastAt = observation.ReceivedAt;
        if (Read(observation.Payload, fields.House) is { } house)
        {
            if (!credits.ContainsKey(house) && observation.EventType is Ra2TelemetryEventTypes.PlayerJoined or Ra2TelemetryEventTypes.CreditsSampled or Ra2TelemetryEventTypes.PlayerDefeated)
                credits[house] = null;
            if (observation.EventType == Ra2TelemetryEventTypes.CreditsSampled
                && observation.Payload.TryGetProperty(fields.Credits, out JsonElement value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out long amount))
                credits[house] = amount;
        }
    }

    /// <summary>Whether this observation changes the story, not just the numbers.</summary>
    public static bool IsHeadline(RawObservation observation) =>
        observation.EventType is Ra2TelemetryEventTypes.MatchStarted or Ra2TelemetryEventTypes.PlayerJoined or Ra2TelemetryEventTypes.PlayerDefeated or Ra2TelemetryEventTypes.MatchEnded;

    public OverlayState State
    {
        get
        {
            HashSet<string> defeated = new(tracker.Defeated, StringComparer.Ordinal);
            return new OverlayState(
                context.ChannelId,
                context.MatchIndex,
                context.MapId,
                startedAt is { } start && lastAt is { } last ? last - start : TimeSpan.Zero,
                credits.Select(pair => new OverlayHouse(pair.Key, pair.Value, defeated.Contains(pair.Key))).ToArray(),
                tracker.Ended,
                tracker.Winner);
        }
    }

    private static string? Read(JsonElement payload, string field) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(field, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>Plain text for an OBS text source.</summary>
public static class OverlayText
{
    public static string Render(OverlayState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        StringBuilder text = new();
        text.Append("Match ").Append(state.MatchIndex.ToString(CultureInfo.InvariantCulture))
            .Append(" · ").Append(state.MapId)
            .Append(" · ").Append(((int)state.Elapsed.TotalMinutes).ToString("00", CultureInfo.InvariantCulture))
            .Append(':').Append(state.Elapsed.Seconds.ToString("00", CultureInfo.InvariantCulture));
        foreach (OverlayHouse house in state.Houses)
        {
            text.Append('\n').Append(house.House);
            if (house.Defeated) text.Append(" — defeated");
            else if (house.Credits is { } credits) text.Append(" — $").Append(credits.ToString("N0", CultureInfo.InvariantCulture));
        }
        if (state.Ended) text.Append('\n').Append(state.Winner is null ? "Match over" : $"Winner: {state.Winner}");
        return text.ToString();
    }
}
