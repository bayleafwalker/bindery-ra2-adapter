// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Sim;
using Xunit;
using Xunit.Abstractions;

namespace Bindery.Ra2.Bot.Tests.Sim;

/// <summary>
/// Exercises geometry rather than only the region graph: equal frozen opponents play each named map from both
/// starts.  A map-side advantage would appear as a west-start win share far from one half.
/// </summary>
public sealed class MirrorMatchTests(ITestOutputHelper output)
{
    [Theory]
    [MemberData(nameof(MirroredMaps))]
    public void Frozen_mirror_matches_do_not_favour_the_west_start(SimMap map)
    {
        const int seedsPerSide = 24;
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();
        ConcurrentBag<(bool WestWon, bool Draw)> results = [];

        Parallel.ForEach(
            Enumerable.Range(1, seedsPerSide).SelectMany(static seed => new[] { (Seed: seed, Swapped: false), (Seed: seed, Swapped: true) }),
            job =>
            {
                SimMap oriented = job.Swapped ? map with { StartRegions = [map.StartRegions[1], map.StartRegions[0]] } : map;
                BotAgentFactory factory = new(rules, PlaybookLibrary.LoadDefault(), new ArenaRunContext(llmFake: true, null));
                MatchRecord match = MatchRunner.Run(new ArmSpec("balanced", false, true), "balanced", oriented, "mirror", job.Seed, 1200, rules, factory);
                bool westWon = (!job.Swapped && match.Winner == 0) || (job.Swapped && match.Winner == 1);
                results.Add((WestWon: westWon, Draw: match.Winner is null));
            });

        int westWins = results.Count(static r => r.WestWon);
        int decisive = results.Count(static r => !r.Draw);
        double share = decisive == 0 ? 0.5 : westWins / (double)decisive;
        output.WriteLine($"{map.Map.MapId}: west {westWins}/{decisive} decisive, {results.Count(static r => r.Draw)} draws, share {share:P1}");
        Assert.InRange(share, 0.35, 0.65);
    }

    public static IEnumerable<object[]> MirroredMaps()
    {
        yield return [SimMaps.TwinValley];
        yield return [SimMaps.RiverCrossing];
        yield return [SimMaps.OpenSteppe];
    }
}
