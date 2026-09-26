// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Tactics;

/// <summary>
/// Sends damaged vehicles to a service depot. Vehicles below
/// <see cref="RepairControllerOptions.HealthFractionThreshold"/> (default
/// 40%) are leased under owner <c>"repair"</c> at a priority (15) that can
/// only preempt a squad's hold on a unit once that squad's minimum hold time
/// has elapsed, per <see cref="ILeaseManager"/>'s preemption rule.
/// </summary>
/// <remarks>
/// <para>Only a building with role <see cref="UnitRole.Support"/> counts as a depot: in RA2 only the service
/// depot repairs vehicles, and in the rules fixture it is the only Support building. A yard, barracks or war
/// factory never repairs anything, so sending a vehicle there would park it out of every squad for good.</para>
/// <para>Hysteresis: a vehicle enters repair below the entry threshold and stays until it reaches
/// <see cref="RepairControllerOptions.RepairedFraction"/>. With one threshold for both, the lease lapsed at 40%,
/// the squad took the unit back off the pad, and it ping-ponged between the squad and the depot.</para>
/// <para>A vehicle whose health does not rise for <see cref="RepairControllerOptions.StallSeconds"/> is given back
/// (the lease is released) and not taken again for <see cref="RepairControllerOptions.RetryCooldownSeconds"/>:
/// a depot that is blocked, unpowered or unreachable must not hold a unit out of the army forever.</para>
/// </remarks>
public sealed class RepairController(RepairControllerOptions options) : ITacticalController
{
    private sealed class Progress
    {
        public GameTime Since;
        public double Health;
    }

    private readonly Dictionary<EntityId, Progress> progress = [];
    private readonly Dictionary<EntityId, GameTime> cooldownUntil = [];

    public string Id => "repair";

    public IReadOnlyList<GameCommand> Tick(BeliefSnapshot belief, IReadOnlyList<SquadOrder> squads, ILeaseManager leases, IRulesDatabase rules)
    {
        ArgumentNullException.ThrowIfNull(belief);
        ArgumentNullException.ThrowIfNull(leases);

        HashSet<EntityId> alive = belief.Own.Select(static e => e.Id).ToHashSet();
        foreach (EntityId gone in progress.Keys.Where(id => !alive.Contains(id)).ToList()) progress.Remove(gone);
        foreach (EntityId gone in cooldownUntil.Keys.Where(id => !alive.Contains(id) || cooldownUntil[id] <= belief.Time).ToList()) cooldownUntil.Remove(gone);

        List<OwnEntity> depots = belief.Own
            .Where(static e => e.Kind == EntityKind.Building && e.Role == UnitRole.Support)
            .OrderBy(static e => e.Id.Value)
            .ToList();

        List<GameCommand> commands = [];
        foreach (OwnEntity vehicle in belief.Own.Where(static e => e.Kind == EntityKind.Vehicle).OrderBy(static e => e.Id.Value))
        {
            LeaseKey key = LeaseKey.Unit(vehicle.Id);
            string? currentOwner = leases.OwnerOf(key, belief.Time);
            bool underRepair = currentOwner == options.Owner;
            double threshold = underRepair ? options.RepairedFraction : options.HealthFractionThreshold;
            OwnEntity? depot = depots
                .Where(d => d.Position.DistanceTo(vehicle.Position) <= options.MaxDepotRangeCells)
                .OrderBy(d => d.Position.DistanceTo(vehicle.Position)).ThenBy(static d => d.Id.Value)
                .FirstOrDefault();

            if (vehicle.HealthFraction >= threshold || depot is null || cooldownUntil.ContainsKey(vehicle.Id))
            {
                GiveBack(vehicle.Id, key, underRepair, leases);
                continue;
            }

            if (underRepair && progress.TryGetValue(vehicle.Id, out Progress? p))
            {
                if (vehicle.HealthFraction > p.Health + 1e-6)
                {
                    p.Health = vehicle.HealthFraction;
                    p.Since = belief.Time;
                }
                else if (belief.Time.SecondsSince(p.Since) >= options.StallSeconds)
                {
                    GiveBack(vehicle.Id, key, underRepair, leases);
                    cooldownUntil[vehicle.Id] = belief.Time.Plus(options.RetryCooldownSeconds);
                    continue;
                }
            }

            bool held = underRepair
                ? leases.Renew(key, options.Owner, belief.Time, options.TtlSeconds)
                : leases.TryAcquire(key, options.Owner, options.Priority, belief.Time, options.MinHoldSeconds, options.TtlSeconds) is not null;
            if (!held) continue;
            if (!underRepair || !progress.ContainsKey(vehicle.Id)) progress[vehicle.Id] = new Progress { Since = belief.Time, Health = vehicle.HealthFraction };

            commands.Add(new RepairCommand(options.Owner, vehicle.Id, depot.Id));
        }
        return commands;
    }

    private void GiveBack(EntityId vehicle, LeaseKey key, bool underRepair, ILeaseManager leases)
    {
        progress.Remove(vehicle);
        if (underRepair) leases.Release(key, options.Owner);
    }
}
