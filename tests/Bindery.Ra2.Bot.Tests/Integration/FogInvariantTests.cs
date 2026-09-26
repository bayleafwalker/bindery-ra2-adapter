// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Sim;
using Bindery.Ra2.Bot.Strategy;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Integration;

/// <summary>
/// Invariant 1 (fog) checked on every frame of real matches, for both players: no object or event of the other
/// player may reach a belief frame from a region that frame does not see. A differential probe only catches the
/// leaks its perturbation exercises; this check catches a unit reported between two region centres and an event
/// delivered from fog, the two channels the probe could not see.
/// </summary>
public sealed class FogInvariantTests
{
    public static TheoryData<string, int, string> Matches() => new()
    {
        { "twin-valley", 1, "rush" },
        { "twin-valley", 2, "rush" },
        { "twin-valley", 3, "balanced" },
        { "river-crossing", 1, "rush" },
        { "fortress-choke", 2, "turtle" },
    };

    [Theory]
    [MemberData(nameof(Matches))]
    public void No_hidden_object_or_event_reaches_a_belief_frame(string mapId, int seed, string opponent)
    {
        SimMap map = SimMaps.All.Single(m => m.Map.MapId == mapId);
        using MatchHarness match = MatchHarness.Create(new PlaybookSelector(), opponent, map, seed, maxSeconds: 600);
        List<string> violations = [];
        int enemyObjects = 0, enemyEvents = 0;
        match.RunUntil(600, () =>
        {
            foreach (PlayerId player in match.Sim.Players)
            {
                ObservationFrame frame = match.Sim.Observe(player);
                enemyObjects += frame.Entities.Count(e => e.Owner != player);
                enemyEvents += frame.Events.Count(e => e.Owner != player);
                violations.AddRange(SimLeakageProbe.FogViolations(frame).Select(v => $"p{player.Value} {v}"));
            }
        });
        // The check must have had something to look at, or passing it proves nothing.
        Assert.True(enemyObjects > 0 && enemyEvents > 0, $"no enemy contact to check ({enemyObjects} objects, {enemyEvents} events)");
        Assert.True(violations.Count == 0, $"{violations.Count} fog violations, first: {string.Join(" | ", violations.Take(5))}");
    }
}
