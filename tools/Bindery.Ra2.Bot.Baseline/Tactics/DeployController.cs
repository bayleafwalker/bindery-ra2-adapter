// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Baseline.Tactics;

/// <summary>
/// Deploys MCVs and other deployable own units. An MCV deploys at the base
/// site (its current position) when it has no own buildings nearby yet
/// (a fresh MCV at the start location or after an expansion move); once a
/// base already exists nearby, later MCVs are treated as reinforcements and
/// left for the operator objective (<c>Expand</c>) to relocate before
/// deploying, so this controller never buries a second base on top of the
/// first.
/// </summary>
public sealed class DeployController(DeployControllerOptions options) : ITacticalController
{
    private const double NearbyBaseRangeCells = 25.0;

    public string Id => "deploy";

    public IReadOnlyList<GameCommand> Tick(BeliefSnapshot belief, IReadOnlyList<SquadOrder> squads, ILeaseManager leases, IRulesDatabase rules)
    {
        ArgumentNullException.ThrowIfNull(belief);
        ArgumentNullException.ThrowIfNull(leases);

        List<OwnEntity> ownBuildings = belief.Own.Where(static e => e.Kind == EntityKind.Building).ToList();
        List<GameCommand> commands = [];
        foreach (OwnEntity mcv in belief.Own.Where(static e => e.Role == UnitRole.Mcv && !e.Deployed).OrderBy(static e => e.Id.Value))
        {
            bool hasNearbyBase = ownBuildings.Any(b => b.Position.DistanceTo(mcv.Position) <= NearbyBaseRangeCells);
            if (hasNearbyBase) continue;

            LeaseKey key = LeaseKey.Unit(mcv.Id);
            string? currentOwner = leases.OwnerOf(key, belief.Time);
            bool held = currentOwner == options.Owner
                ? leases.Renew(key, options.Owner, belief.Time, options.TtlSeconds)
                : leases.TryAcquire(key, options.Owner, options.Priority, belief.Time, options.MinHoldSeconds, options.TtlSeconds) is not null;
            if (!held) continue;

            commands.Add(new DeployCommand(options.Owner, mcv.Id));
        }
        return commands;
    }
}
