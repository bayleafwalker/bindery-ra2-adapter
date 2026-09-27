// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot;

namespace Bindery.Ra2.Adapter.Bot;

/// <summary>
/// Translates every <see cref="GameCommand"/> subtype into a
/// <see cref="Ra2CommandEnvelope"/> and buffers it; <see cref="FlushAsync"/>
/// drains the buffer through an <see cref="IRa2CommandTransport"/> in
/// submission order. Buffering (rather than sending inline from
/// <see cref="Submit"/>) lets the runtime batch a whole tick's commands
/// through one transport round trip instead of one per command.
/// </summary>
public sealed class Ra2CommandSink : ICommandSink
{
    private readonly IRa2CommandTransport transport;
    private readonly Queue<Ra2CommandEnvelope> buffer = new();

    public Ra2CommandSink(IRa2CommandTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        this.transport = transport;
    }

    /// <summary>Envelopes buffered and not yet flushed.</summary>
    public int BufferedCount => buffer.Count;

    public void Submit(GameCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        buffer.Enqueue(ToEnvelope(command));
    }

    /// <summary>
    /// Sends every buffered envelope, in submission order, then clears the buffer. An envelope leaves the buffer
    /// only after the transport accepted it: when a send throws (an IO fault, or cancellation) the failed envelope
    /// and everything behind it stay buffered, so the exception reaches the caller and a retry resends them in the
    /// order the bot issued them instead of silently losing one order the planner believes went out.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        while (buffer.Count > 0)
        {
            Ra2CommandEnvelope envelope = buffer.Peek();
            await transport.SendAsync(envelope, cancellationToken).ConfigureAwait(false);
            buffer.Dequeue();
        }
    }

    /// <summary>Maps one <see cref="GameCommand"/> to its <c>bindery.ra2.bot-command/v1</c> envelope.</summary>
    public static Ra2CommandEnvelope ToEnvelope(GameCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command switch
        {
            ProduceCommand c => Build(c.Controller, "produce", new { typeId = c.TypeId, queue = c.Queue.ToString() }),
            CancelProductionCommand c => Build(c.Controller, "cancel_production", new { typeId = c.TypeId, queue = c.Queue.ToString() }),
            PlaceBuildingCommand c => Build(c.Controller, "place_building", new { typeId = c.TypeId, cell = CellFields(c.Cell) }),
            SellCommand c => Build(c.Controller, "sell", new { building = c.Building.Value }),
            MoveCommand c => Build(c.Controller, "move", new { units = UnitValues(c.Units), destination = CellFields(c.Destination) }),
            AttackMoveCommand c => Build(c.Controller, "attack_move", new { units = UnitValues(c.Units), destination = CellFields(c.Destination) }),
            AttackCommand c => Build(c.Controller, "attack", new { units = UnitValues(c.Units), target = c.Target.Value }),
            StopCommand c => Build(c.Controller, "stop", new { units = UnitValues(c.Units) }),
            DeployCommand c => Build(c.Controller, "deploy", new { unit = c.Unit.Value }),
            RepairCommand c => Build(c.Controller, "repair", new { unit = c.Unit.Value, depot = c.Depot?.Value }),
            HarvestCommand c => Build(c.Controller, "harvest", new { harvester = c.Harvester.Value, ore = CellFields(c.Ore) }),
            SetRallyPointCommand c => Build(c.Controller, "set_rally_point", new { factory = c.Factory.Value, cell = CellFields(c.Cell) }),
            LaunchSuperweaponCommand c => Build(c.Controller, "launch_superweapon", new { building = c.Building.Value, target = CellFields(c.Target) }),
            _ => throw new NotSupportedException($"unmapped GameCommand type: {command.GetType().FullName}"),
        };
    }

    private static uint[] UnitValues(IReadOnlyList<EntityId> units) => [.. units.Select(static u => u.Value)];

    private static object CellFields(Cell cell) => new { x = cell.X, y = cell.Y };

    private static Ra2CommandEnvelope Build(string controller, string kind, object fields) =>
        new(Ra2CommandEnvelope.CurrentSchemaVersion, kind, controller, BotJson.ToElement(fields));
}
