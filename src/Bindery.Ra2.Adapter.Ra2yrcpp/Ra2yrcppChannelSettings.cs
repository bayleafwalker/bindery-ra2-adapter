// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Adapter.Channel;

namespace Bindery.Ra2.Adapter.Ra2yrcpp;

/// <summary>
/// Live telemetry for the channel: the ra2yrcpp service of one client in the
/// match, read in the spectator view so the tracker and overlay see every
/// house. An agent seat's filter still cuts it down to its own house.
/// </summary>
public sealed class Ra2yrcppLiveTelemetrySettings
{
    /// <summary><c>host:port</c> of the service, 14521 by default in the fork.</summary>
    public string Endpoint { get; init; } = string.Empty;

    public int PollMilliseconds { get; init; } = 500;

    public void Validate()
    {
        Ra2yrcppClient.UriFor(Ra2YrcppEndpoint.Parse(Endpoint));
        if (PollMilliseconds < 1) throw new ArgumentOutOfRangeException(nameof(PollMilliseconds), "pollMilliseconds must be positive");
    }

    public IRa2TelemetrySource Create()
    {
        Validate();
        return new Ra2yrcppTelemetrySource(Ra2YrcppEndpoint.Parse(Endpoint), new Ra2yrcppTelemetryOptions(PollInterval: TimeSpan.FromMilliseconds(PollMilliseconds)));
    }
}

/// <summary>
/// An agent in one player seat: the rules playbook controller, a routine,
/// and the live command sink into that player's own client.
/// </summary>
public sealed class Ra2yrcppAgentSeatSettings
{
    /// <summary>Plan and trace only.</summary>
    public const string IdleRoutine = "idle";

    /// <summary>Deploy the opening MCV once (<see cref="DeployMcvRoutineController"/>).</summary>
    public const string DeployMcvRoutine = "deploy_mcv";

    /// <summary>The house the seat plays, as the game names it: the player's name.</summary>
    public string House { get; init; } = string.Empty;

    /// <summary>The player client whose seat the agent takes.</summary>
    public string ClientInstanceId { get; init; } = string.Empty;

    public string Routine { get; init; } = IdleRoutine;

    public string ControllerVersion { get; init; } = "0.1.0";

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(House);
        ArgumentException.ThrowIfNullOrWhiteSpace(ClientInstanceId);
        if (Routine is not (IdleRoutine or DeployMcvRoutine))
            throw new ArgumentException($"routine is {IdleRoutine} or {DeployMcvRoutine}, not {Routine}");
        ToAssignment().Validate();
    }

    public AgentSeatAssignment ToAssignment()
    {
        PlaybookController controller = Controller();
        return new(House, ClientInstanceId, ControllerDeclaration.Agent(controller.ControllerId, controller.ControllerVersion));
    }

    /// <summary>A fresh seat for one match; the caller disposes the sink when the match ends.</summary>
    public (AgentSeat Seat, Ra2yrcppCommandSink Commands) CreateSeat(AgentSeatLaunch launch)
    {
        Validate();
        ArgumentNullException.ThrowIfNull(launch);
        // Orders go only to the agent client's own service, named by its launch.
        if (!string.Equals(launch.Assignment.ClientInstanceId, ClientInstanceId, StringComparison.Ordinal) || !string.Equals(launch.Assignment.House, House, StringComparison.Ordinal))
            throw new InvalidOperationException("the match assigns a different agent seat than these settings");
        if (!string.Equals(launch.Launch.PlayerName, House, StringComparison.Ordinal))
            throw new InvalidOperationException($"client {ClientInstanceId} plays {launch.Launch.PlayerName}, not the seat's house {House}");
        if (string.IsNullOrWhiteSpace(launch.Launch.CommandEndpoint))
            throw new InvalidOperationException($"client {ClientInstanceId}'s launch has no commandEndpoint for its ra2yrcpp service");
        Ra2yrcppCommandSink sink = new(House, Ra2YrcppEndpoint.Parse(launch.Launch.CommandEndpoint));
        return (new AgentSeat(Controller(), sink, new PlayerObservationFilter(House)), sink);
    }

    private PlaybookController Controller() => new(
        House,
        new RulePlaybookPlanner(),
        Routine == DeployMcvRoutine ? new DeployMcvRoutineController() : new IdleRoutineController(),
        controllerVersion: ControllerVersion);
}

/// <summary>How the channel tool's telemetry and seat settings fit together.</summary>
public static class Ra2yrcppChannelSettings
{
    /// <summary>
    /// One telemetry source at most, and an agent seat only with live
    /// telemetry: a recording is not the match the seat's commands act on.
    /// </summary>
    public static void Validate(Ra2yrcppLiveTelemetrySettings? liveTelemetry, string? telemetryRecording, Ra2yrcppAgentSeatSettings? agentSeat)
    {
        if (liveTelemetry is not null && !string.IsNullOrWhiteSpace(telemetryRecording))
            throw new ArgumentException("set liveTelemetry or telemetryRecording, not both");
        liveTelemetry?.Validate();
        if (agentSeat is null) return;
        agentSeat.Validate();
        if (liveTelemetry is null)
            throw new ArgumentException("an agent seat needs liveTelemetry: a recording is not the match its orders act on");
    }
}
