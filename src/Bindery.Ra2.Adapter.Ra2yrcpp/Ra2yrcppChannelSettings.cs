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
        Ra2YrcppEndpoint.Parse(Endpoint);
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

    /// <summary><c>host:port</c> of that client's own ra2yrcpp service.</summary>
    public string CommandEndpoint { get; init; } = string.Empty;

    public string Routine { get; init; } = IdleRoutine;

    public string ControllerVersion { get; init; } = "0.1.0";

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(House);
        ArgumentException.ThrowIfNullOrWhiteSpace(ClientInstanceId);
        Ra2YrcppEndpoint.Parse(CommandEndpoint);
        if (Routine is not (IdleRoutine or DeployMcvRoutine))
            throw new ArgumentException($"routine is {IdleRoutine} or {DeployMcvRoutine}, not {Routine}");
        ToAssignment().Validate();
    }

    public AgentSeatAssignment ToAssignment()
    {
        PlaybookController controller = Controller();
        return new(House, ClientInstanceId, ControllerDeclaration.Agent(controller.ControllerId, controller.ControllerVersion));
    }

    /// <summary>A fresh seat for one match; the caller disposes the sink when the channel stops.</summary>
    public (AgentSeat Seat, Ra2yrcppCommandSink Commands) CreateSeat()
    {
        Validate();
        Ra2yrcppCommandSink sink = new(House, Ra2YrcppEndpoint.Parse(CommandEndpoint));
        return (new AgentSeat(Controller(), sink, new PlayerObservationFilter(House)), sink);
    }

    private PlaybookController Controller() => new(
        House,
        new RulePlaybookPlanner(),
        Routine == DeployMcvRoutine ? new DeployMcvRoutineController() : new IdleRoutineController(),
        controllerVersion: ControllerVersion);
}
