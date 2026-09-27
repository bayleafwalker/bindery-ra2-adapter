// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Sim;

/// <summary>
/// Perturbs hidden state belonging to players other than <c>observer</c> so a
/// test can assert that a belief-mode <see cref="ObservationFrame"/> (or,
/// upstream, a strategist context built from it) never moved: enemy credits,
/// enemy production queues, and enemy units placed in a region the observer
/// cannot currently see are all facts the observer has no legitimate way to
/// know. If perturbing them changes what the observer sees, fog is leaking.
/// Two of the perturbations only show through combat: hidden enemies are
/// wounded, and an enemy unit is placed just across a border inside the reach
/// of the observer's longest-range weapon. A leak through fire or kill events
/// takes seconds to appear, so a probe compares over a long window. One hidden
/// unit is announced through the event path (a completed build and a new object),
/// so broken event gating changes the observer's frame at once.
/// </summary>
public static class SimLeakageProbe
{
    public static void PerturbHidden(SkirmishSimulation sim, PlayerId observer)
    {
        ArgumentNullException.ThrowIfNull(sim);
        IReadOnlySet<RegionId> visible = sim.VisibleRegionsForProbe(observer);
        RegionId unseenRegion = sim.Map.Regions.Select(r => r.Id).FirstOrDefault(r => !visible.Contains(r), sim.StartRegionOf(observer));

        foreach (PlayerId other in sim.Players)
        {
            if (other == observer) continue;

            sim.DebugAdjustCredits(other, 12_345);
            sim.DebugEnqueue(other, QueueKind.Building, "__leakage-probe__");
            sim.DebugSpawnAcrossBorder(other, observer);

            RegionId hiddenRegion = visible.Contains(sim.StartRegionOf(other)) ? unseenRegion : sim.StartRegionOf(other);
            if (!visible.Contains(hiddenRegion))
            {
                string? anyType = FirstKnownUnitType(sim);
                if (anyType is not null)
                {
                    sim.DebugSpawnSilently(other, anyType, hiddenRegion);
                    // The same kind of object again, announced through the event path: event gating must hold it back too.
                    sim.DebugSpawnAnnounced(other, anyType, hiddenRegion);
                }
            }
        }
        sim.DebugWoundHidden(observer);
    }

    /// <summary>
    /// Fog violations in one belief-mode frame: every object or event of another player (and every kill this player
    /// is told about) must stand in a cell whose region is in <see cref="ObservationFrame.VisibleRegions"/>. A
    /// differential probe only finds a leak that its perturbation happens to exercise; this per-frame check finds any
    /// hidden position that reached the frame, whatever put it there (a unit between two region centres, an event in
    /// fog). A superweapon launch is announced to every player by design, so its target cell is exempt.
    /// </summary>
    public static IReadOnlyList<string> FogViolations(ObservationFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        List<string> violations = [];
        if (frame.Mode != ObservationMode.Belief) return violations;
        bool Visible(Cell cell) => frame.Map.RegionOf(cell) is { } region && frame.VisibleRegions.Contains(region.Id);
        foreach (ObservedEntity e in frame.Entities)
        {
            if (e.Owner != frame.Self && !Visible(e.Position))
            {
                violations.Add(FormattableString.Invariant($"t={frame.Time.Seconds:0.0} entity {e.Id.Value} {e.TypeId} at ({e.Position.X},{e.Position.Y}) is in a hidden region"));
            }
        }
        foreach (GameEvent ev in frame.Events)
        {
            if (ev.Kind == GameEventKind.SuperweaponLaunched) continue;
            bool aboutOther = ev.Owner != frame.Self || ev.Kind == GameEventKind.EntityKilledByUs;
            if (!aboutOther) continue;
            if (ev.Position is not { } cell || !Visible(cell))
            {
                violations.Add(FormattableString.Invariant($"t={frame.Time.Seconds:0.0} event {ev.Kind} {ev.TypeId} of player {ev.Owner?.Value} at {ev.Position} is not in a visible region"));
            }
        }
        return violations;
    }

    private static string? FirstKnownUnitType(SkirmishSimulation sim) =>
        sim.Observe(sim.Players[0], ObservationMode.Oracle).Entities.Select(e => e.TypeId).FirstOrDefault();
}
