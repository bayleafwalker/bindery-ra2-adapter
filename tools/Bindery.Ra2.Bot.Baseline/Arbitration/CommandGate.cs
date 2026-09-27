// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Baseline.Runtime;

namespace Bindery.Ra2.Bot.Baseline.Arbitration;

/// <summary>A command the gate refused, with a stable reason code.</summary>
/// <param name="Reason"><c>lease.missing</c>, <c>units.empty</c>, <c>budget.no_account</c>,
/// <c>budget.insufficient</c>, <c>type.unknown</c> or <c>command.unknown</c>.</param>
/// <param name="Offenders">Units the controller did not hold, for unit commands.</param>
public sealed record DroppedCommand(GameCommand Command, string Reason, IReadOnlyList<EntityId> Offenders);

public sealed record GateResult(IReadOnlyList<GameCommand> Passed, IReadOnlyList<DroppedCommand> Dropped);

/// <summary>
/// Last line of invariant 4: every command leaving the bot is checked against
/// leases and the budget ledger, whatever layer produced it. A controller bug can
/// then cost a dropped (and logged) order, never two controllers fighting over a
/// unit or credits spent twice.
/// </summary>
/// <remarks>
/// Policy per command:
/// <list type="bullet">
/// <item>Unit commands (<see cref="MoveCommand"/>, <see cref="AttackMoveCommand"/>, <see cref="AttackCommand"/>,
/// <see cref="StopCommand"/>, <see cref="DeployCommand"/>, <see cref="RepairCommand"/>, <see cref="HarvestCommand"/>)
/// need the issuing controller to hold the live unit lease of every unit. A command with any unleased unit is
/// dropped <em>whole</em>, not stripped: a group order executed by part of the group splits a squad, and an
/// unleased unit in an order is a controller bug that should be visible, not papered over. An order with no
/// units is dropped as <c>units.empty</c>.</item>
/// <item><see cref="SellCommand"/> is irreversible, so it needs the building's lease too.</item>
/// <item><see cref="SetRallyPointCommand"/> is harmless unless someone else owns the factory: it passes unless
/// another controller holds the factory's lease.</item>
/// <item><see cref="ProduceCommand"/> costs the type's rules cost, spent from the controller's ledger
/// reservations (the role's pool first, see <see cref="BudgetPools.ForRole"/>); dropped when the reservations
/// cannot cover it. Spending is recorded, so two orders cannot pass on one reservation.</item>
/// <item><see cref="PlaceBuildingCommand"/> and <see cref="CancelProductionCommand"/> cost nothing now (RA2 charges
/// while building), but only a budgeted controller (one with a ledger account this period) may issue them.</item>
/// </list>
/// Each drop is logged as <c>command.dropped</c> in input order.
/// </remarks>
public sealed class CommandGate
{
    public const string LeaseMissing = "lease.missing";
    public const string UnitsEmpty = "units.empty";
    public const string BudgetNoAccount = "budget.no_account";
    public const string BudgetInsufficient = "budget.insufficient";
    public const string TypeUnknown = "type.unknown";
    public const string CommandUnknown = "command.unknown";

    private readonly ILeaseManager leases;
    private readonly BudgetLedger ledger;
    private readonly IRulesDatabase rules;
    private readonly IDecisionLog? log;
    private readonly BotMetrics? metrics;

    public CommandGate(ILeaseManager leases, BudgetLedger ledger, IRulesDatabase rules, IDecisionLog? log = null, BotMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(leases);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(rules);
        this.leases = leases;
        this.ledger = ledger;
        this.rules = rules;
        this.log = log;
        this.metrics = metrics;
    }

    public GateResult Filter(IReadOnlyList<GameCommand> commands, GameTime now, long snapshotVersion)
    {
        ArgumentNullException.ThrowIfNull(commands);
        List<GameCommand> passed = [];
        List<DroppedCommand> dropped = [];
        foreach (GameCommand command in commands)
        {
            DroppedCommand? drop = Check(command, now);
            if (drop is null)
            {
                passed.Add(command);
                continue;
            }
            dropped.Add(drop);
            log?.Write(new DecisionRecord(DecisionRecordKinds.CommandDropped, now, snapshotVersion, BotJson.ToElement(new
            {
                controller = command.Controller,
                command = command.GetType().Name,
                reason = drop.Reason,
                offenders = drop.Offenders.Select(static e => e.Value).ToList(),
            })));
        }
        if (metrics is not null)
        {
            metrics.CommandsPassed += passed.Count;
            metrics.CommandsDropped += dropped.Count;
        }
        return new GateResult(passed, dropped);
    }

    private DroppedCommand? Check(GameCommand command, GameTime now) => command switch
    {
        MoveCommand c => CheckUnits(c, c.Units, now),
        AttackMoveCommand c => CheckUnits(c, c.Units, now),
        AttackCommand c => CheckUnits(c, c.Units, now),
        StopCommand c => CheckUnits(c, c.Units, now),
        DeployCommand c => CheckUnits(c, [c.Unit], now),
        RepairCommand c => CheckUnits(c, [c.Unit], now),
        HarvestCommand c => CheckUnits(c, [c.Harvester], now),
        SellCommand c => CheckUnits(c, [c.Building], now),
        SetRallyPointCommand c => CheckRally(c, now),
        ProduceCommand c => CheckProduce(c),
        PlaceBuildingCommand c => ledger.HasAccount(c.Controller) ? null : Drop(c, BudgetNoAccount),
        CancelProductionCommand c => ledger.HasAccount(c.Controller) ? null : Drop(c, BudgetNoAccount),
        _ => Drop(command, CommandUnknown),
    };

    private DroppedCommand? CheckUnits(GameCommand command, IReadOnlyList<EntityId> units, GameTime now)
    {
        if (units.Count == 0) return Drop(command, UnitsEmpty);
        List<EntityId> offenders = [];
        foreach (EntityId unit in units)
        {
            if (!string.Equals(leases.OwnerOf(LeaseKey.Unit(unit), now), command.Controller, StringComparison.Ordinal))
            {
                offenders.Add(unit);
            }
        }
        return offenders.Count == 0 ? null : new DroppedCommand(command, LeaseMissing, offenders);
    }

    private DroppedCommand? CheckRally(SetRallyPointCommand command, GameTime now)
    {
        string? owner = leases.OwnerOf(LeaseKey.Unit(command.Factory), now);
        return owner is null || string.Equals(owner, command.Controller, StringComparison.Ordinal)
            ? null
            : new DroppedCommand(command, LeaseMissing, [command.Factory]);
    }

    private DroppedCommand? CheckProduce(ProduceCommand command)
    {
        if (!rules.TryGet(command.TypeId, out UnitRule rule)) return Drop(command, TypeUnknown);
        if (!ledger.HasAccount(command.Controller)) return Drop(command, BudgetNoAccount);
        return ledger.TrySpendAny(command.Controller, BudgetPools.ForRole(rule.Role), Math.Max(0, rule.Cost))
            ? null
            : Drop(command, BudgetInsufficient);
    }

    private static DroppedCommand Drop(GameCommand command, string reason) => new(command, reason, []);
}
