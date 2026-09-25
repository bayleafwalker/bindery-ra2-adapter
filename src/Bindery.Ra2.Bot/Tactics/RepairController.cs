// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Tactics;

/// <summary>
/// Sends damaged vehicles to a service depot. Vehicles below
/// <see cref="RepairControllerOptions.HealthFractionThreshold"/> (default
/// 40%) are leased under owner <c>"repair"</c> at a priority (15) that can
/// only preempt a squad's hold on a unit once that squad's minimum hold time
/// has elapsed, per <see cref="ILeaseManager"/>'s preemption rule.
/// </summary>
public sealed class RepairController(RepairControllerOptions options) : ITacticalController
{
    public string Id => "repair";

    public IReadOnlyList<GameCommand> Tick(BeliefSnapshot belief, IReadOnlyList<SquadOrder> squads, ILeaseManager leases, IRulesDatabase rules)
    {
        ArgumentNullException.ThrowIfNull(belief);
        ArgumentNullException.ThrowIfNull(leases);

        List<OwnEntity> depots = belief.Own
            .Where(static e => e.Kind == EntityKind.Building && e.Role is UnitRole.Production or UnitRole.Support)
            .OrderBy(static e => e.Id.Value)
            .ToList();
        if (depots.Count == 0) return [];

        List<GameCommand> commands = [];
        foreach (OwnEntity vehicle in belief.Own.Where(static e => e.Kind == EntityKind.Vehicle).OrderBy(static e => e.Id.Value))
        {
            if (vehicle.HealthFraction >= options.HealthFractionThreshold) continue;

            OwnEntity? depot = depots
                .Where(d => d.Position.DistanceTo(vehicle.Position) <= options.MaxDepotRangeCells)
                .OrderBy(d => d.Position.DistanceTo(vehicle.Position))
                .FirstOrDefault();
            if (depot is null) continue;

            LeaseKey key = LeaseKey.Unit(vehicle.Id);
            string? currentOwner = leases.OwnerOf(key, belief.Time);
            bool held = currentOwner == options.Owner
                ? leases.Renew(key, options.Owner, belief.Time, options.TtlSeconds)
                : leases.TryAcquire(key, options.Owner, options.Priority, belief.Time, options.MinHoldSeconds, options.TtlSeconds) is not null;
            if (!held) continue;

            commands.Add(new RepairCommand(options.Owner, vehicle.Id, depot.Id));
        }
        return commands;
    }
}
