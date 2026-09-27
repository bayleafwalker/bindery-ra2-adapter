// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Sim;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary>
/// The arm's start side rotates with the seed (seeds 1-2 west, 3-4 east, ...), so any four consecutive seeds play
/// every faction from every start once: a map-side advantage can no longer land entirely on the arm.
/// </summary>
public sealed class SideRotationTests
{
    private sealed class FirstFrameRecorder(IArenaAgent inner, Action<ObservationFrame> first) : IArenaAgent
    {
        private bool seen;

        public ArenaAgentStats Stats => inner.Stats;

        public IReadOnlyList<DecisionRecord> DecisionLog => inner.DecisionLog;

        public IReadOnlyList<GameCommand> Tick(ObservationFrame frame)
        {
            if (!seen) first(frame);
            seen = true;
            return [];
        }

        public void Finish(bool? won, double ownAssetValue, double enemyAssetValue) => inner.Finish(won, ownAssetValue, enemyAssetValue);

        public void Dispose() => inner.Dispose();
    }

    private sealed class RecordingFactory(IArenaAgentFactory inner) : IArenaAgentFactory
    {
        public Dictionary<PlayerId, RegionId> StartOf { get; } = [];

        public IArenaAgent Create(ArmSpec arm, PlayerId player, Faction faction, MapInfo map, int seed) =>
            new FirstFrameRecorder(inner.Create(arm, player, faction, map, seed), frame =>
                StartOf[player] = frame.Map.RegionOf(frame.Entities.First(e => e.Owner == player).Position)!.Id);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(8, true)]
    public void The_arm_starts_east_on_every_second_pair_of_seeds(int seed, bool east)
    {
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();
        SimMap map = SimMaps.TwinValley;
        RecordingFactory factory = new(new BotAgentFactory(rules, PlaybookLibrary.LoadDefault(), new ArenaRunContext(llmFake: true, null)));

        MatchRunner.Run(new ArmSpec("selector", false, true), "ai-rush", map, "training", seed, 2, rules, factory);

        Assert.Equal(east, MatchRunner.ArmStartsEast(seed));
        Assert.Equal(map.StartRegions[east ? 1 : 0], factory.StartOf[MatchRunner.ArmPlayer]);
        Assert.Equal(map.StartRegions[east ? 0 : 1], factory.StartOf[MatchRunner.OpponentPlayer]);
    }
}
