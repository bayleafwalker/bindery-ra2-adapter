// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Sim;

namespace Bindery.Ra2.Bot.Arena;

/// <summary>Per-player figures for one completed match, used to build the arena report's metrics.</summary>
public sealed record PlayerMatchMetrics(
    int Proposals,
    int Rejected,
    IReadOnlyList<double> LateSeconds,
    int Activations,
    int PostureFlips,
    int CommandsDropped,
    int InvalidCommands,
    double ProductionIdleFraction,
    double AverageCreditsOnHand,
    int AssetValueDestroyedByOpponent,
    int AssetValueLostByPlayer,
    long TokensIn,
    long TokensOut,
    string? Model,
    double Usd,
    int FinalAssetValue)
{
    public double TradeEfficiency => AssetValueLostByPlayer <= 0
        ? (AssetValueDestroyedByOpponent > 0 ? AssetValueDestroyedByOpponent : 1.0)
        : AssetValueDestroyedByOpponent / (double)AssetValueLostByPlayer;
}

/// <summary>One completed match, as reported in <c>results.json</c>.</summary>
public sealed record MatchRecord(
    string Arm,
    string Opponent,
    string Map,
    string Split,
    int Seed,
    int? Winner,
    string Reason,
    double DurationSeconds,
    IReadOnlyDictionary<string, PlayerMatchMetrics> Players);

/// <summary>Runs one arm-vs-opponent match on one map with one seed to completion.</summary>
public static class MatchRunner
{
    private static readonly PlayerId ArmPlayer = new(0);
    private static readonly PlayerId OpponentPlayer = new(1);

    public static MatchRecord Run(ArmSpec arm, string opponent, SimMap map, string split, int seed, double maxSeconds, ArenaRuleset rules, IArenaAgentFactory factory)
    {
        SimSettings settings = new(seed, maxSeconds, [new SimPlayer(ArmPlayer, Faction.Allied), new SimPlayer(OpponentPlayer, Faction.Soviet)]);
        SkirmishSimulation sim = new(map, rules, settings);

        ObservationMode mode = arm.Oracle ? ObservationMode.Oracle : ObservationMode.Belief;
        IArenaAgent armAgent = factory.Create(arm, ArmPlayer, Faction.Allied, sim.Map, seed);
        IArenaAgent opponentAgent = factory.Create(new ArmSpec(opponent, arm.Oracle, false), OpponentPlayer, Faction.Soviet, sim.Map, seed);

        Dictionary<PlayerId, int> destroyedValueOf = new() { [ArmPlayer] = 0, [OpponentPlayer] = 0 };
        Dictionary<PlayerId, double> creditsSampleSum = new() { [ArmPlayer] = 0, [OpponentPlayer] = 0 };
        Dictionary<PlayerId, int> idleSamples = new() { [ArmPlayer] = 0, [OpponentPlayer] = 0 };
        int samples = 0;

        int maxFrames = (int)(maxSeconds * GameTime.FramesPerSecond) + GameTime.FramesPerSecond;
        for (int frame = 0; frame < maxFrames && !sim.MatchEnded; frame++)
        {
            ObservationFrame armFrame = sim.Observe(ArmPlayer, mode);
            ObservationFrame opponentFrame = sim.Observe(OpponentPlayer, mode);
            foreach (GameCommand c in armAgent.Tick(armFrame)) sim.Submit(ArmPlayer, c);
            foreach (GameCommand c in opponentAgent.Tick(opponentFrame)) sim.Submit(OpponentPlayer, c);

            sim.Step();

            if (sim.Time.Frame % GameTime.FramesPerSecond == 0)
            {
                samples++;
                ObservationFrame oracle0 = sim.Observe(ArmPlayer, ObservationMode.Oracle);
                ObservationFrame oracle1 = sim.Observe(OpponentPlayer, ObservationMode.Oracle);
                foreach (GameEvent e in oracle0.Events)
                {
                    if (e.Kind == GameEventKind.EntityDestroyed && e.Owner is { } owner && rules.TryGet(e.TypeId ?? string.Empty, out UnitRule rule))
                    {
                        destroyedValueOf[owner] = destroyedValueOf.GetValueOrDefault(owner) + rule.Cost;
                    }
                }
                creditsSampleSum[ArmPlayer] += oracle0.Credits;
                creditsSampleSum[OpponentPlayer] += oracle1.Credits;
                if (IsProductionIdle(oracle0)) idleSamples[ArmPlayer]++;
                if (IsProductionIdle(oracle1)) idleSamples[OpponentPlayer]++;
            }
        }

        string reason = sim.MatchEnded ? sim.EndReason ?? "unknown" : "frame_budget_exhausted";
        double durationSeconds = sim.Time.Seconds;

        Dictionary<string, PlayerMatchMetrics> perPlayer = new()
        {
            ["arm"] = BuildMetrics(ArmPlayer, armAgent.Stats, sim, destroyedValueOf, creditsSampleSum, idleSamples, samples),
            ["opponent"] = BuildMetrics(OpponentPlayer, opponentAgent.Stats, sim, destroyedValueOf, creditsSampleSum, idleSamples, samples),
        };

        return new MatchRecord(arm.ToString(), opponent, map.Map.MapId, split, seed, sim.Winner?.Value, reason, durationSeconds, perPlayer);
    }

    private static bool IsProductionIdle(ObservationFrame frame)
    {
        List<ProductionQueueState> factoryQueues = frame.Queues.Where(q => q.Factories > 0).ToList();
        if (factoryQueues.Count == 0) return false;
        return factoryQueues.All(q => q.Items.Count == 0);
    }

    private static PlayerMatchMetrics BuildMetrics(
        PlayerId player, ArenaAgentStats stats, SkirmishSimulation sim,
        Dictionary<PlayerId, int> destroyedValueOf, Dictionary<PlayerId, double> creditsSampleSum,
        Dictionary<PlayerId, int> idleSamples, int samples)
    {
        PlayerId opponent = player == ArmPlayer ? OpponentPlayer : ArmPlayer;
        return new PlayerMatchMetrics(
            Proposals: stats.Proposals,
            Rejected: stats.Rejected,
            LateSeconds: stats.LateSeconds,
            Activations: stats.Activations,
            PostureFlips: stats.PostureFlips,
            CommandsDropped: stats.CommandsDropped,
            InvalidCommands: sim.RejectedCommandCount(player),
            ProductionIdleFraction: samples == 0 ? 0 : idleSamples[player] / (double)samples,
            AverageCreditsOnHand: samples == 0 ? 0 : creditsSampleSum[player] / samples,
            AssetValueDestroyedByOpponent: destroyedValueOf.GetValueOrDefault(opponent),
            AssetValueLostByPlayer: destroyedValueOf.GetValueOrDefault(player),
            TokensIn: stats.TokensIn,
            TokensOut: stats.TokensOut,
            Model: stats.Model,
            Usd: stats.Usd,
            FinalAssetValue: sim.AssetValue(player));
    }
}
