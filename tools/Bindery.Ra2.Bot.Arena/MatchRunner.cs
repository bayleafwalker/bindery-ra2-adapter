// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Sim;

namespace Bindery.Ra2.Bot.Arena;

/// <summary>Per-player figures for one completed match, used to build the arena report's metrics.</summary>
public sealed record PlayerMatchMetrics(
    Faction Faction,
    int Proposals,
    int Rejected,
    int LateDiscarded,
    IReadOnlyList<double> LateSeconds,
    int Activations,
    int PostureFlips,
    int CommandsDropped,
    IReadOnlyDictionary<string, int> DroppedByReason,
    int InvalidCommands,
    int FogRejections,
    int ShadowProposals,
    int ProposalsFailed,
    double ProductionIdleFraction,
    double AverageCreditsOnHand,
    int AssetValueDestroyedByOpponent,
    int AssetValueLostByPlayer,
    long TokensIn,
    long TokensOut,
    string? Model,
    double Usd,
    int FinalAssetValue,
    int UnitsBuilt,
    int BuildingsBuilt,
    int PeakArmyValue,
    string? DecisionLogHash,
    IReadOnlyList<string> Labels,
    double? FirstAttackSeconds = null)
{
    /// <summary>Seconds each playbook was active (the arm's intent timeline).</summary>
    public IReadOnlyDictionary<string, double> PlaybookSeconds { get; init; } = new Dictionary<string, double>();

    /// <summary>Seconds each posture was active.</summary>
    public IReadOnlyDictionary<string, double> PostureSeconds { get; init; } = new Dictionary<string, double>();

    /// <summary>Shadow proposals whose request also got a primary proposal (see <see cref="Analysis.ShadowAgreement"/>).</summary>
    public int ShadowCompared { get; init; }

    /// <summary>Of those, shadow proposals naming the same playbook as the primary.</summary>
    public int ShadowAgreed { get; init; }

    /// <summary>Primary requests the distilled strategist answered; 0 for other arms.</summary>
    public int DistilledDecisions { get; init; }

    /// <summary>Of those, requests escalated to the LLM.</summary>
    public int DistilledEscalations { get; init; }

    public double TradeEfficiency => AssetValueLostByPlayer <= 0
        ? (AssetValueDestroyedByOpponent > 0 ? AssetValueDestroyedByOpponent : 1.0)
        : AssetValueDestroyedByOpponent / (double)AssetValueLostByPlayer;
}

/// <summary>One completed match, as reported in <c>results.json</c>.</summary>
/// <param name="Winner">0 when the arm won, 1 when the opponent won, null on a draw.</param>
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
/// <remarks>
/// The arm plays Allied on odd seeds and Soviet on even seeds (the opponent takes the other
/// faction), so the approximate fixture's faction asymmetry does not bias an arm's results
/// one way. The arm is always player 0 and starts in the map's first start region.
/// </remarks>
public static class MatchRunner
{
    public static readonly PlayerId ArmPlayer = new(0);
    public static readonly PlayerId OpponentPlayer = new(1);

    public static Faction ArmFaction(int seed) => seed % 2 == 1 ? Faction.Allied : Faction.Soviet;

    public static MatchRecord Run(ArmSpec arm, string opponent, SimMap map, string split, int seed, double maxSeconds, IRulesDatabase rules, IArenaAgentFactory factory, Action<IReadOnlyList<DecisionRecord>>? armLog = null, TextWriter? trace = null, BenchmarkSettings? benchmark = null)
    {
        Faction armFaction = ArmFaction(seed);
        Faction opponentFaction = armFaction == Faction.Allied ? Faction.Soviet : Faction.Allied;
        SimSettings settings = (benchmark ?? BenchmarkSettings.Standard).ToSimSettings(seed, maxSeconds, ArmPlayer, armFaction, OpponentPlayer, opponentFaction);
        SkirmishSimulation sim = new(map, rules, settings);

        ObservationMode mode = arm.Oracle ? ObservationMode.Oracle : ObservationMode.Belief;
        using IArenaAgent armAgent = factory.Create(arm, ArmPlayer, armFaction, sim.Map, seed);
        using IArenaAgent opponentAgent = factory.Create(new ArmSpec(opponent, false, false), OpponentPlayer, opponentFaction, sim.Map, seed);

        Dictionary<PlayerId, int> destroyedValueOf = new() { [ArmPlayer] = 0, [OpponentPlayer] = 0 };
        Dictionary<PlayerId, double> creditsSampleSum = new() { [ArmPlayer] = 0, [OpponentPlayer] = 0 };
        Dictionary<PlayerId, int> idleSamples = new() { [ArmPlayer] = 0, [OpponentPlayer] = 0 };
        Dictionary<PlayerId, int> unitsBuilt = new() { [ArmPlayer] = 0, [OpponentPlayer] = 0 };
        Dictionary<PlayerId, int> buildingsBuilt = new() { [ArmPlayer] = 0, [OpponentPlayer] = 0 };
        Dictionary<PlayerId, int> peakArmy = new() { [ArmPlayer] = 0, [OpponentPlayer] = 0 };
        Dictionary<PlayerId, double?> firstAttack = new() { [ArmPlayer] = null, [OpponentPlayer] = null };
        int cheapest = rules.All.Where(static r => r.Cost > 0).Select(static r => r.Cost).DefaultIfEmpty(0).Min();
        int samples = 0;

        int maxFrames = (int)(maxSeconds * GameTime.FramesPerSecond) + GameTime.FramesPerSecond;
        for (int frame = 0; frame < maxFrames && !sim.MatchEnded; frame++)
        {
            ObservationFrame armFrame = sim.Observe(ArmPlayer, mode);
            ObservationFrame opponentFrame = sim.Observe(OpponentPlayer, ObservationMode.Belief);
            foreach (GameCommand c in armAgent.Tick(armFrame)) sim.Submit(ArmPlayer, c);
            foreach (GameCommand c in opponentAgent.Tick(opponentFrame)) sim.Submit(OpponentPlayer, c);

            sim.Step();

            ObservationFrame oracle = sim.Observe(ArmPlayer, ObservationMode.Oracle);
            foreach (GameEvent e in oracle.Events)
            {
                if (e.Owner is not { } owner || !rules.TryGet(e.TypeId ?? string.Empty, out UnitRule rule)) continue;
                if (e.Kind == GameEventKind.EntityDestroyed && e.Detail is "combat" or "superweapon") destroyedValueOf[owner] += rule.Cost;
                if (e.Kind == GameEventKind.EntityCreated)
                {
                    if (rule.Kind == EntityKind.Building) buildingsBuilt[owner]++;
                    else unitsBuilt[owner]++;
                }
            }

            if (trace is not null)
            {
                foreach (GameEvent e in oracle.Events.Where(static e => e.Kind == GameEventKind.EntityDestroyed))
                {
                    trace.WriteLine($"{sim.Time.Seconds:0} destroyed {e.Owner} {e.TypeId} at {sim.Map.RegionOf(e.Position ?? default)?.Name} ({e.Detail})");
                }
                if (sim.Time.Frame % (GameTime.FramesPerSecond * 10) == 0) Trace(trace, sim, rules);
            }

            if (sim.Time.Frame % GameTime.FramesPerSecond == 0)
            {
                samples++;
                foreach (PlayerId p in new[] { ArmPlayer, OpponentPlayer })
                {
                    ObservationFrame view = p == ArmPlayer ? oracle : sim.Observe(p, ObservationMode.Oracle);
                    creditsSampleSum[p] += view.Credits;
                    if (IsProductionIdle(view, p, rules, cheapest)) idleSamples[p]++;
                    int army = view.Entities
                        .Where(e => e.Owner == p && rules.TryGet(e.TypeId, out UnitRule r) && r.Kind != EntityKind.Building && r.Damage > 0)
                        .Sum(e => rules.Get(e.TypeId).Cost);
                    peakArmy[p] = Math.Max(peakArmy[p], army);
                    firstAttack[p] ??= FirstAttack(view, p, sim.Map, rules);
                }
            }
        }

        string reason = sim.MatchEnded ? sim.EndReason ?? "unknown" : "frame_budget_exhausted";
        double durationSeconds = sim.Time.Seconds;
        bool? armWon = sim.Winner is { } w ? w == ArmPlayer : null;
        double armAssets = sim.AssetValue(ArmPlayer), opponentAssets = sim.AssetValue(OpponentPlayer);
        armAgent.Finish(armWon, armAssets, opponentAssets);
        armLog?.Invoke(armAgent.DecisionLog);
        opponentAgent.Finish(armWon is { } aw ? !aw : null, opponentAssets, armAssets);

        PlayerMatchMetrics Metrics(PlayerId p, Faction faction, IArenaAgent agent) =>
            BuildMetrics(p, faction, agent.Stats, sim, destroyedValueOf, creditsSampleSum, idleSamples, samples, unitsBuilt[p], buildingsBuilt[p], peakArmy[p]) with { FirstAttackSeconds = firstAttack[p] };

        Dictionary<string, PlayerMatchMetrics> perPlayer = new()
        {
            ["arm"] = Metrics(ArmPlayer, armFaction, armAgent),
            ["opponent"] = Metrics(OpponentPlayer, opponentFaction, opponentAgent),
        };
        int? winner = sim.Winner is { } winnerId ? winnerId.Value : null;
        return new MatchRecord(arm.ToString(), opponent, map.Map.MapId, split, seed, winner, reason, durationSeconds, perPlayer);
    }

    /// <summary>Diagnostic snapshot for <c>--trace</c>: per player credits, power, own types and where the fighters stand.</summary>
    private static void Trace(TextWriter trace, SkirmishSimulation sim, IRulesDatabase rules)
    {
        foreach (PlayerId p in sim.Players)
        {
            ObservationFrame view = sim.Observe(p, ObservationMode.Oracle);
            List<ObservedEntity> own = [.. view.Entities.Where(e => e.Owner == p)];
            string types = string.Join(",", own.GroupBy(static e => e.TypeId).OrderBy(static g => g.Key, StringComparer.Ordinal).Select(static g => $"{g.Key}x{g.Count()}"));
            string army = string.Join(" ", own.Where(e => rules.TryGet(e.TypeId, out UnitRule r) && r.Kind != EntityKind.Building && r.Damage > 0)
                .GroupBy(e => sim.Map.RegionOf(e.Position)?.Name ?? "?").OrderBy(static g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Key}:{g.Sum(e => rules.Get(e.TypeId).Cost)}"));
            string queues = string.Join(";", view.Queues.Where(static q => q.Items.Count > 0).Select(static q => $"{q.Kind}:{string.Join(",", q.Items.Select(static i => i.TypeId))}"));
            trace.WriteLine($"{sim.Time.Seconds:0} {p} cr={view.Credits} pow={view.Power.Produced}/{view.Power.Drained} [{types}] q[{queues}] army[{army}]");
        }
    }

    /// <summary>
    /// Time-to-first-attack: the first sampled second at which one of the player's combat units stands in a region
    /// holding an enemy structure (an attack reaching the enemy base, not a skirmish in the field); null if never.
    /// </summary>
    private static double? FirstAttack(ObservationFrame oracle, PlayerId player, MapInfo map, IRulesDatabase rules)
    {
        HashSet<RegionId> enemyBase = [.. oracle.Entities
            .Where(e => e.Owner != player && rules.TryGet(e.TypeId, out UnitRule r) && r.Kind == EntityKind.Building)
            .Select(e => map.RegionOf(e.Position)?.Id ?? default)];
        bool present = oracle.Entities.Any(e => e.Owner == player && rules.TryGet(e.TypeId, out UnitRule r)
            && r.Kind != EntityKind.Building && r.Damage > 0 && enemyBase.Contains(map.RegionOf(e.Position)?.Id ?? default));
        return present ? oracle.Time.Seconds : null;
    }

    /// <summary>Spec: at least one factory, nothing queued, and credits for the cheapest buildable item.</summary>
    private static bool IsProductionIdle(ObservationFrame frame, PlayerId player, IRulesDatabase rules, int cheapest)
    {
        bool hasFactory = frame.Entities.Any(e => e.Owner == player && rules.TryGet(e.TypeId, out UnitRule r) && r.Kind == EntityKind.Building && r.Role == UnitRole.Production);
        if (!hasFactory || frame.Credits < cheapest) return false;
        return frame.Queues.All(static q => q.Items.Count == 0);
    }

    private static PlayerMatchMetrics BuildMetrics(
        PlayerId player, Faction faction, ArenaAgentStats stats, SkirmishSimulation sim,
        Dictionary<PlayerId, int> destroyedValueOf, Dictionary<PlayerId, double> creditsSampleSum,
        Dictionary<PlayerId, int> idleSamples, int samples, int unitsBuilt, int buildingsBuilt, int peakArmy)
    {
        PlayerId opponent = player == ArmPlayer ? OpponentPlayer : ArmPlayer;
        return new PlayerMatchMetrics(
            Faction: faction,
            Proposals: stats.Proposals,
            Rejected: stats.Rejected,
            LateDiscarded: stats.LateDiscarded,
            LateSeconds: stats.LateSeconds,
            Activations: stats.Activations,
            PostureFlips: stats.PostureFlips,
            CommandsDropped: stats.CommandsDropped,
            DroppedByReason: new SortedDictionary<string, int>(stats.DroppedByReason, StringComparer.Ordinal),
            InvalidCommands: sim.RejectedCommandCount(player),
            FogRejections: stats.FogRejections,
            ShadowProposals: stats.ShadowProposals,
            ProposalsFailed: stats.ProposalsFailed,
            ProductionIdleFraction: samples == 0 ? 0 : idleSamples[player] / (double)samples,
            AverageCreditsOnHand: samples == 0 ? 0 : creditsSampleSum[player] / samples,
            AssetValueDestroyedByOpponent: destroyedValueOf.GetValueOrDefault(opponent),
            AssetValueLostByPlayer: destroyedValueOf.GetValueOrDefault(player),
            TokensIn: stats.TokensIn,
            TokensOut: stats.TokensOut,
            Model: stats.Model,
            Usd: stats.Usd,
            FinalAssetValue: sim.AssetValue(player),
            UnitsBuilt: unitsBuilt,
            BuildingsBuilt: buildingsBuilt,
            PeakArmyValue: peakArmy,
            DecisionLogHash: stats.DecisionLogHash,
            Labels: [.. stats.Labels])
        {
            DistilledDecisions = stats.DistilledDecisions,
            DistilledEscalations = stats.DistilledEscalations,
            ShadowCompared = stats.ShadowCompared,
            ShadowAgreed = stats.ShadowAgreed,
            PlaybookSeconds = new SortedDictionary<string, double>(stats.PlaybookSeconds, StringComparer.Ordinal),
            PostureSeconds = new SortedDictionary<string, double>(stats.PostureSeconds, StringComparer.Ordinal),
        };
    }
}
