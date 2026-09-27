// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bindery.Ra2.Adapter.Channel;

/// <summary>
/// Cuts the instrumented stream down to what one house may know.
/// </summary>
/// <remarks>
/// The ra2yrcpp stream is a spectator's view: every house's credits, queues,
/// orders and units. Handing that to a player controller turns a strategy
/// test into clairvoyance, so the filter fails closed. An observation passes
/// only if it is public match lifecycle, is owned by the seat's house, or
/// carries an explicit visibility list that names the house. Anything without
/// an owner or visibility field is withheld.
///
/// The bridge owns the payload shape. <see cref="OwnerField"/> and
/// <see cref="VisibleToField"/> name the fields it uses; until it emits
/// per-house visibility, an agent sees its own house and public events only.
/// </remarks>
public sealed class PlayerObservationFilter
{
    private static readonly HashSet<string> publicEvents = new(StringComparer.Ordinal)
    {
        Ra2TelemetryEventTypes.MatchStarted,
        Ra2TelemetryEventTypes.MatchEnded,
        Ra2TelemetryEventTypes.PlayerJoined,
        Ra2TelemetryEventTypes.PlayerDefeated,
    };

    public PlayerObservationFilter(string house, string ownerField = "house", string visibleToField = "visible_to")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(house);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerField);
        ArgumentException.ThrowIfNullOrWhiteSpace(visibleToField);
        House = house;
        OwnerField = ownerField;
        VisibleToField = visibleToField;
    }

    public string House { get; }

    public string OwnerField { get; }

    public string VisibleToField { get; }

    public bool Admits(RawObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (publicEvents.Contains(observation.EventType)) return true;
        JsonElement payload = observation.Payload;
        if (payload.ValueKind != JsonValueKind.Object) return false;
        if (payload.TryGetProperty(OwnerField, out JsonElement owner)
            && owner.ValueKind == JsonValueKind.String
            && string.Equals(owner.GetString(), House, StringComparison.Ordinal))
            return true;
        if (payload.TryGetProperty(VisibleToField, out JsonElement visible) && visible.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in visible.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String && string.Equals(entry.GetString(), House, StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }
}

/// <summary>An order for the seat's own house, in the command channel's vocabulary.</summary>
public sealed record PlayerCommand(string Kind, JsonElement Arguments);

/// <summary>
/// The command channel's vocabulary. Object orders name object addresses
/// (<c>objects</c>), optionally with the stable IDs seen for them
/// (<c>unique_ids</c>, one per object); the sink drops any the house does not
/// own or whose ID has changed.
/// </summary>
public static class PlayerCommandKinds
{
    /// <summary><c>{objects}</c>: deploy, e.g. an MCV into a construction yard.</summary>
    public const string Deploy = "deploy";
    /// <summary><c>{objects}</c></summary>
    public const string Stop = "stop";
    /// <summary><c>{objects, x, y, z?}</c></summary>
    public const string Move = "move";
    /// <summary><c>{objects, x, y, z?}</c></summary>
    public const string AttackMove = "attack_move";
    /// <summary><c>{objects, target}</c></summary>
    public const string Attack = "attack";
    /// <summary><c>{type, action?: begin|hold|cancel}</c></summary>
    public const string Produce = "produce";
    /// <summary><c>{object, x, y, z?}</c>: a finished building from one of the house's factories.</summary>
    public const string PlaceBuilding = "place_building";
}

/// <summary>
/// A change of plan, made at a meaningful moment -- new threat, stalled
/// economy, tech transition -- rather than every frame.
/// </summary>
public sealed record PlaybookRevision(string Trigger, string Playbook);

/// <summary>What the controller decided after one observation.</summary>
/// <param name="Notes">Why the controller did or did not act -- a trigger fired, a plan was dropped -- for the trace.</param>
public sealed record ControllerStep(IReadOnlyList<PlayerCommand> Commands, PlaybookRevision? Revision = null, IReadOnlyList<string>? Notes = null)
{
    public static ControllerStep None { get; } = new([]);
}

/// <summary>
/// An AI in a player slot. It sees only what its filter admits and acts only
/// through its seat's command sink.
/// </summary>
public interface IPlayerController
{
    string ControllerId { get; }

    string ControllerVersion { get; }

    Task<ControllerStep> ObserveAsync(RawObservation observation, CancellationToken cancellationToken);
}

/// <summary>
/// The per-player command channel into the game. It is bound to one house;
/// the observer client has no sink at all.
/// </summary>
public interface IPlayerCommandSink
{
    string House { get; }

    Task SendAsync(PlayerCommand command, CancellationToken cancellationToken);
}

/// <summary>
/// The order reached the game's command queue, or may have, but its result
/// never came back (a timeout). It may still execute: it is neither sent
/// nor failed.
/// </summary>
public sealed class CommandOutcomeUnknownException(string message, Exception? innerException = null) : Exception(message, innerException);

public enum DecisionTraceKind
{
    SeatOpened,
    ControllerNote,
    PlaybookRevised,
    CommandSent,
    CommandFailed,
    SeatClosed,
    /// <summary>An order that may or may not have executed; see <see cref="CommandOutcomeUnknownException"/>.</summary>
    CommandOutcomeUnknown,
}

public sealed record DecisionTraceEntry(
    long Sequence,
    DateTimeOffset At,
    DecisionTraceKind Kind,
    string House,
    string? SourceEventId,
    string Detail,
    JsonElement? Data = null);

/// <summary>Counts for one seat's run, written with the trace.</summary>
public sealed record AgentSeatSummary(
    string House,
    string ControllerId,
    string ControllerVersion,
    long ObservationsAdmitted,
    long ObservationsWithheld,
    long CommandsSent,
    long CommandsFailed,
    IReadOnlyList<PlaybookRevision> Revisions,
    string TracePath,
    long CommandsOutcomeUnknown = 0);

/// <summary>
/// Runs one controller in one player seat for one match: filtered
/// observations in, commands out, every decision appended to an NDJSON trace
/// that sits beside the match record.
/// </summary>
public sealed class AgentSeat : IAsyncDisposable
{
    public const string TraceFileName = "decision-trace.ndjson";

    private static readonly JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IPlayerController controller;
    private readonly IPlayerCommandSink commands;
    private readonly PlayerObservationFilter filter;
    private readonly TimeProvider clock;
    private long sequence;

    public AgentSeat(IPlayerController controller, IPlayerCommandSink commands, PlayerObservationFilter filter, TimeProvider? clock = null)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.commands = commands ?? throw new ArgumentNullException(nameof(commands));
        this.filter = filter ?? throw new ArgumentNullException(nameof(filter));
        if (!string.Equals(commands.House, filter.House, StringComparison.Ordinal))
            throw new ArgumentException("the command sink and the observation filter must be bound to the same house");
        this.clock = clock ?? TimeProvider.System;
    }

    public string House => filter.House;

    /// <summary>
    /// A seat is made for one match and owns its command sink: disposing
    /// the seat disposes the sink if it holds a connection.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (commands is IAsyncDisposable disposable) await disposable.DisposeAsync().ConfigureAwait(false);
    }

    public async Task<AgentSeatSummary> RunAsync(IRa2TelemetrySource source, string traceDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(traceDirectory);
        Directory.CreateDirectory(traceDirectory);
        string tracePath = Path.Combine(traceDirectory, TraceFileName);
        long admitted = 0, withheld = 0, sent = 0, failed = 0, unknown = 0;
        List<PlaybookRevision> revisions = [];

        await using StreamWriter trace = new(tracePath, append: false);
        await WriteAsync(trace, DecisionTraceKind.SeatOpened, null, $"{controller.ControllerId}@{controller.ControllerVersion}", null, cancellationToken).ConfigureAwait(false);
        string closeReason = "telemetry ended";
        try
        {
            await foreach (RawObservation observation in source.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!filter.Admits(observation))
                {
                    withheld++;
                    continue;
                }
                admitted++;
                ControllerStep step = await controller.ObserveAsync(observation, cancellationToken).ConfigureAwait(false) ?? ControllerStep.None;
                foreach (string note in step.Notes ?? [])
                    await WriteAsync(trace, DecisionTraceKind.ControllerNote, observation.EventId, note, null, cancellationToken).ConfigureAwait(false);
                if (step.Revision is { } revision)
                {
                    revisions.Add(revision);
                    await WriteAsync(trace, DecisionTraceKind.PlaybookRevised, observation.EventId, revision.Trigger, JsonSerializer.SerializeToElement(revision, options), cancellationToken).ConfigureAwait(false);
                }
                foreach (PlayerCommand command in step.Commands)
                {
                    try
                    {
                        await commands.SendAsync(command, cancellationToken).ConfigureAwait(false);
                        sent++;
                        await WriteAsync(trace, DecisionTraceKind.CommandSent, observation.EventId, command.Kind, command.Arguments, cancellationToken).ConfigureAwait(false);
                    }
                    catch (CommandOutcomeUnknownException exception)
                    {
                        // Neither sent nor failed: the game may still carry it out.
                        unknown++;
                        await WriteAsync(trace, DecisionTraceKind.CommandOutcomeUnknown, observation.EventId, $"{command.Kind}: {exception.Message}", command.Arguments, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        // One rejected order is a decision worth keeping, not a reason to leave the seat.
                        failed++;
                        await WriteAsync(trace, DecisionTraceKind.CommandFailed, observation.EventId, $"{command.Kind}: {exception.Message}", command.Arguments, cancellationToken).ConfigureAwait(false);
                    }
                }
                if (observation.EventType == Ra2TelemetryEventTypes.MatchEnded)
                {
                    closeReason = "match ended";
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            closeReason = "cancelled";
            throw;
        }
        finally
        {
            await WriteAsync(trace, DecisionTraceKind.SeatClosed, null, $"{closeReason}; admitted {admitted}, withheld {withheld}", null, CancellationToken.None).ConfigureAwait(false);
        }
        return new AgentSeatSummary(filter.House, controller.ControllerId, controller.ControllerVersion, admitted, withheld, sent, failed, revisions, tracePath, unknown);
    }

    private async Task WriteAsync(StreamWriter trace, DecisionTraceKind kind, string? sourceEventId, string detail, JsonElement? data, CancellationToken cancellationToken)
    {
        DecisionTraceEntry entry = new(++sequence, clock.GetUtcNow(), kind, filter.House, sourceEventId, detail, data);
        await trace.WriteLineAsync(JsonSerializer.Serialize(entry, options).AsMemory(), cancellationToken).ConfigureAwait(false);
        await trace.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
