// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Baseline.Tactics;

/// <summary>
/// Keeps harvesters alive and working: flees a threatened harvester to the
/// nearest own refinery, otherwise sends it to the nearest field this player
/// controls. Leases every own harvester it commands under owner
/// <c>"harvest"</c> so squads and other controllers cannot also move it.
/// </summary>
public sealed class HarvesterSafetyController(HarvesterSafetyOptions options) : ITacticalController
{
    public string Id => "harvest";

    private readonly Dictionary<EntityId, string> lastOrder = [];

    public IReadOnlyList<GameCommand> Tick(BeliefSnapshot belief, IReadOnlyList<SquadOrder> squads, ILeaseManager leases, IRulesDatabase rules)
    {
        ArgumentNullException.ThrowIfNull(belief);
        ArgumentNullException.ThrowIfNull(leases);

        List<EnemyContact> combatContacts = belief.Enemies
            .Where(e => !e.ConfirmedDestroyed &&
                        e.Confidence >= options.MinConfidence &&
                        belief.Time.SecondsSince(e.LastSeenAt) <= options.MaxContactAgeSeconds &&
                        e.Kind is EntityKind.Infantry or EntityKind.Vehicle or EntityKind.Aircraft)
            .ToList();

        List<OwnEntity> harvesters = belief.Own.Where(static e => e.Role == UnitRole.Harvester)
            .OrderBy(static e => e.Id.Value).ToList();
        List<OwnEntity> refineries = belief.Own.Where(static e => e.Role == UnitRole.Economy && e.Kind == EntityKind.Building)
            .OrderBy(static e => e.Id.Value).ToList();

        List<GameCommand> commands = [];
        foreach (OwnEntity harvester in harvesters)
        {
            LeaseKey key = LeaseKey.Unit(harvester.Id);
            string? currentOwner = leases.OwnerOf(key, belief.Time);
            bool held = currentOwner == options.Owner
                ? leases.Renew(key, options.Owner, belief.Time, options.TtlSeconds)
                : leases.TryAcquire(key, options.Owner, options.Priority, belief.Time, options.MinHoldSeconds, options.TtlSeconds) is not null;
            if (!held) continue;

            bool threatened = combatContacts.Any(c => c.LastSeenPosition.DistanceTo(harvester.Position) <= options.ThreatRangeCells);
            if (threatened && refineries.Count > 0)
            {
                OwnEntity safest = refineries.OrderBy(r => r.Position.DistanceTo(harvester.Position)).First();
                if (Issue(harvester.Id, $"flee:{safest.Id.Value}")) commands.Add(new MoveCommand(options.Owner, [harvester.Id], safest.Position));
                continue;
            }

            // Fields last seen empty are skipped; a field never seen with an ore report is assumed to hold ore.
            List<OreField> fields = belief.Map.OreFields
                .Where(o => belief.OreLastSeen is not { } seen || !seen.TryGetValue(o.Region, out int left) || left > 0)
                .ToList();
            OreField? field = fields
                .Where(o => o.Region == harvester.Region || refineries.Any(r => r.Region == o.Region))
                .OrderBy(o => o.Center.DistanceTo(harvester.Position))
                .FirstOrDefault();
            field ??= fields.OrderBy(o => o.Center.DistanceTo(harvester.Position)).FirstOrDefault();
            if (field is not null && Issue(harvester.Id, $"harvest:{field.Region.Value}"))
            {
                commands.Add(new HarvestCommand(options.Owner, harvester.Id, field.Center));
            }
        }
        foreach (EntityId gone in lastOrder.Keys.Where(id => !harvesters.Any(h => h.Id == id)).ToList()) lastOrder.Remove(gone);
        return commands;
    }

    /// <summary>
    /// Orders are edge-triggered: a harvester is told to harvest a field, or to flee, only when that differs
    /// from its last order. Re-issuing the same order every tick would restart the engine's harvest cycle and
    /// no load would ever be delivered.
    /// </summary>
    private bool Issue(EntityId harvester, string order)
    {
        if (lastOrder.TryGetValue(harvester, out string? previous) && previous == order) return false;
        lastOrder[harvester] = order;
        return true;
    }
}
