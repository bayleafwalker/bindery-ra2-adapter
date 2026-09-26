// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arbitration;
using Bindery.Ra2.Bot.Runtime;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arbitration;

public sealed class CommandGateTests
{
    private static readonly EntityId A = new(1), B = new(2), C = new(3);
    private readonly DecisionLog log = new();
    private readonly BotMetrics metrics = new();
    private readonly LeaseManager leases = new();
    private readonly BudgetLedger ledger = new();
    private readonly CommandGate gate;

    public CommandGateTests()
    {
        gate = new CommandGate(leases, ledger, Fx.Rules, log, metrics);
        leases.TryAcquire(LeaseKey.Unit(A), "squad", 1, Fx.T(0), 0, 60);
        leases.TryAcquire(LeaseKey.Unit(B), "squad", 1, Fx.T(0), 0, 60);
        leases.TryAcquire(LeaseKey.Unit(C), "harvest", 1, Fx.T(0), 0, 60);
    }

    private GateResult Filter(params GameCommand[] commands) => gate.Filter(commands, Fx.T(1), 15);

    [Fact]
    public void Command_on_leased_units_passes()
    {
        GateResult result = Filter(new MoveCommand("squad", [A, B], new Cell(1, 1)));
        Assert.Single(result.Passed);
        Assert.Empty(result.Dropped);
    }

    [Fact]
    public void Command_with_any_unleased_unit_is_dropped_whole_and_logged()
    {
        GateResult result = Filter(new AttackMoveCommand("squad", [A, C, new EntityId(99)], new Cell(1, 1)));
        Assert.Empty(result.Passed);
        DroppedCommand dropped = Assert.Single(result.Dropped);
        Assert.Equal(CommandGate.LeaseMissing, dropped.Reason);
        Assert.Equal([C, new EntityId(99)], dropped.Offenders);

        DecisionRecord record = Assert.Single(log.OfKind(DecisionRecordKinds.CommandDropped));
        Assert.Equal("squad", record.Data.GetProperty("controller").GetString());
        Assert.Equal(1, metrics.CommandsDropped);
    }

    [Fact]
    public void Every_unit_command_kind_is_lease_checked()
    {
        GameCommand[] foreign =
        [
            new AttackCommand("squad", [C], A),
            new StopCommand("squad", [C]),
            new DeployCommand("squad", C),
            new RepairCommand("squad", C, null),
            new HarvestCommand("squad", C, new Cell(3, 3)),
            new SellCommand("squad", C),
        ];
        GateResult result = Filter(foreign);
        Assert.Empty(result.Passed);
        Assert.All(result.Dropped, d => Assert.Equal(CommandGate.LeaseMissing, d.Reason));

        Assert.Single(Filter(new HarvestCommand("harvest", C, new Cell(3, 3))).Passed);
    }

    [Fact]
    public void Expired_lease_no_longer_authorises()
    {
        GateResult result = gate.Filter([new StopCommand("squad", [A])], Fx.T(60), 900);
        Assert.Equal(CommandGate.LeaseMissing, Assert.Single(result.Dropped).Reason);
    }

    [Fact]
    public void Empty_unit_list_is_dropped()
    {
        Assert.Equal(CommandGate.UnitsEmpty, Assert.Single(Filter(new MoveCommand("squad", [], new Cell(0, 0))).Dropped).Reason);
    }

    [Fact]
    public void Production_needs_a_reservation_and_cannot_spend_it_twice()
    {
        ledger.BeginPeriod(Fx.T(0), 1000, 0, new BudgetShares(0, 1, 0, 0));
        Assert.Equal(CommandGate.BudgetNoAccount, Assert.Single(Filter(new ProduceCommand("ops", "mtnk", QueueKind.Vehicle)).Dropped).Reason);

        ledger.Reserve(BudgetPools.Army, "ops", 1000);
        GateResult result = Filter(
            new ProduceCommand("ops", "mtnk", QueueKind.Vehicle),
            new ProduceCommand("ops", "mtnk", QueueKind.Vehicle));
        Assert.Single(result.Passed);
        Assert.Equal(CommandGate.BudgetInsufficient, Assert.Single(result.Dropped).Reason);
        Assert.Equal(900, ledger.Spent(BudgetPools.Army));
    }

    [Fact]
    public void Unknown_type_is_dropped_and_placement_needs_a_budget_account()
    {
        ledger.BeginPeriod(Fx.T(0), 1000, 0, new BudgetShares(1, 0, 0, 0));
        ledger.Reserve(BudgetPools.Economy, "ops", 0);
        Assert.Equal(CommandGate.TypeUnknown, Assert.Single(Filter(new ProduceCommand("ops", "zzz", QueueKind.Vehicle)).Dropped).Reason);
        Assert.Single(Filter(new PlaceBuildingCommand("ops", "gapowr", new Cell(4, 4))).Passed);
        Assert.Equal(CommandGate.BudgetNoAccount, Assert.Single(Filter(new PlaceBuildingCommand("squad", "gapowr", new Cell(4, 4))).Dropped).Reason);
    }

    [Fact]
    public void Superweapon_launch_needs_a_budget_account_and_no_other_lease_on_the_building()
    {
        ledger.BeginPeriod(Fx.T(0), 1000, 0, new BudgetShares(1, 0, 0, 0));
        ledger.Reserve(BudgetPools.Economy, "ops", 0);
        Assert.Single(Filter(new LaunchSuperweaponCommand("ops", new EntityId(50), new Cell(1, 1))).Passed);
        Assert.Equal(CommandGate.BudgetNoAccount, Assert.Single(Filter(new LaunchSuperweaponCommand("squad", new EntityId(50), new Cell(1, 1))).Dropped).Reason);
        Assert.Equal(CommandGate.LeaseMissing, Assert.Single(Filter(new LaunchSuperweaponCommand("ops", C, new Cell(1, 1))).Dropped).Reason);
    }

    [Fact]
    public void Rally_point_passes_unless_another_controller_owns_the_factory()
    {
        Assert.Single(Filter(new SetRallyPointCommand("ops", new EntityId(50), new Cell(1, 1))).Passed);
        Assert.Single(Filter(new SetRallyPointCommand("ops", C, new Cell(1, 1))).Dropped);
        Assert.Single(Filter(new SetRallyPointCommand("harvest", C, new Cell(1, 1))).Passed);
    }
}
