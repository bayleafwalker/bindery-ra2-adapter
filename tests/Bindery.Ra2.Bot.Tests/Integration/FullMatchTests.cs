// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Arbitration;
using Bindery.Ra2.Bot.Runtime;
using Bindery.Ra2.Bot.Strategy;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Integration;

/// <summary>End-to-end: the real bot against a real opponent bot in the region simulator.</summary>
public sealed class FullMatchTests
{
    private sealed record Outcome(
        bool Ended,
        double Seconds,
        string LogHash,
        string StateHash,
        long Activations,
        long ComponentFailures,
        IReadOnlyList<DroppedCommand> Dropped,
        IReadOnlyList<DecisionRecord> Log);

    private static Outcome PlayFull(int seed)
    {
        using MatchHarness match = MatchHarness.Create(new PlaybookSelector(), "rush", seed: seed);
        match.RunUntil(1300);
        return new Outcome(
            match.Sim.MatchEnded,
            match.Sim.Time.Seconds,
            match.ArmLog.ComputeHash(),
            match.Sim.ComputeStateHash(),
            match.Arm.Metrics.Activations,
            match.Arm.Metrics.ComponentFailures + match.Opponent!.Metrics.ComponentFailures,
            [.. match.ArmDropped],
            [.. match.ArmLog.Records]);
    }

    [Fact]
    public void A_full_match_completes_cleanly_and_replays_to_the_same_decision_log_hash()
    {
        Outcome first = PlayFull(seed: 1);
        Outcome second = PlayFull(seed: 1);

        Assert.True(first.Ended, "the match must end (elimination or the 1200 s limit)");
        Assert.InRange(first.Seconds, 1, 1200 + 1);
        Assert.Equal(0, first.ComponentFailures);
        Assert.DoesNotContain(first.Log, r => r.Kind is RuntimeRecordKinds.PlanFailed or RuntimeRecordKinds.TacticsFailed);
        Assert.True(first.Activations > 0);
        Assert.Contains(first.Log, r => r.Kind == DecisionRecordKinds.IntentActivated);

        // Every controller in the standard bot commands only units it leased this frame; the tactical
        // order (preempting controllers before squads) is what makes this hold.
        Assert.DoesNotContain(first.Dropped, d => d.Reason == CommandGate.LeaseMissing);

        Assert.Equal(first.LogHash, second.LogHash);
        Assert.Equal(first.StateHash, second.StateHash);
    }

    [Fact]
    public void The_bot_builds_an_economy_and_an_army_and_attacks()
    {
        // Against the independent scripted opponent: a live pinned-playbook opponent now runs the same improved
        // planner and, as Soviet, out-rushes the Allied arm before its war factory stands, which says nothing about
        // the arm's own macro. The economy check is on timing, not on a count, because the arm may win before a
        // second harvester would finish: once the war factory stands, a harvester must be queued promptly.
        using MatchHarness match = MatchHarness.CreateVsScripted(new PlaybookSelector(), "ai-turtle", seed: 1);
        int maxRefineries = 0, maxCombat = 0, maxHarvesters = 0;
        double? factoryAt = null, harvesterAt = null;
        bool attacked = false;
        match.RunUntil(900, () =>
        {
            if (match.Sim.Time.Frame % GameTime.FramesPerSecond != 0) return;
            ObservationFrame view = match.Sim.Observe(MatchHarness.ArmPlayer, ObservationMode.Oracle);
            IReadOnlyList<ObservedEntity> own = match.OwnEntities(MatchHarness.ArmPlayer);
            int refineries = 0, combat = 0;
            int harvesters = view.Queues.SelectMany(static q => q.Items).Count(static i => MatchHarness.Rules.Get(i.TypeId).Role == UnitRole.Harvester);
            bool harvester = harvesters > 0;
            foreach (ObservedEntity e in own)
            {
                UnitRule rule = MatchHarness.Rules.Get(e.TypeId);
                if (rule.Role == UnitRole.Harvester) { harvester = true; harvesters++; }
                else if (rule.Role == UnitRole.Economy && rule.Kind == EntityKind.Building) refineries++;
                else if (rule.Kind != EntityKind.Building && rule.Damage > 0) combat++;
                if (rule.Kind == EntityKind.Building && rule.Queue == QueueKind.Building && rule.Role == UnitRole.Production
                    && MatchHarness.Rules.All.Any(r => r.Role == UnitRole.Harvester && r.Prerequisites.Any(p => p.Contains(e.TypeId))))
                {
                    factoryAt ??= match.Sim.Time.Seconds;
                }
            }
            if (harvester) harvesterAt ??= match.Sim.Time.Seconds;
            maxRefineries = Math.Max(maxRefineries, refineries);
            maxHarvesters = Math.Max(maxHarvesters, harvesters);
            maxCombat = Math.Max(maxCombat, combat);
            attacked |= match.Arm.Squads.Any(static s => s.Objective == ObjectiveKind.AttackRegion && s.Engage);
        });

        Assert.True(maxRefineries >= 1, $"refineries: {maxRefineries}");
        Assert.True(factoryAt is not null || match.Sim.Winner == MatchHarness.ArmPlayer, "no war factory and no win");
        if (factoryAt is { } built && match.Sim.Time.Seconds - built > 20)
        {
            Assert.True(harvesterAt is { } h && h - built <= 20, $"war factory at {built:0} s, first harvester queued at {harvesterAt?.ToString("0") ?? "never"}");
        }
        // A count, not only timing: with a war factory standing for a minute the economy goes past one harvester
        // (the default target is three per refinery), unless the match ended first.
        if (factoryAt is { } standing && match.Sim.Time.Seconds - standing > 60)
        {
            Assert.True(maxHarvesters >= 2, $"harvesters owned or queued at once: {maxHarvesters}, war factory from {standing:0} s to {match.Sim.Time.Seconds:0} s");
        }
        Assert.True(maxCombat >= 5, $"combat units at once: {maxCombat}");
        Assert.True(attacked, "the army never went on the attack");
        Assert.Equal(MatchHarness.ArmPlayer, match.Sim.Winner);
        Assert.True(match.ArmLog.OfKind(DecisionRecordKinds.Plan).Count > 100);
    }

    [Fact]
    public void A_runtime_decision_log_round_trips_into_a_decision_dataset()
    {
        using MatchHarness match = MatchHarness.Create(new PlaybookSelector(), "balanced", seed: 1);
        match.RunUntil(400);

        // Through the NDJSON form the log is persisted in, as an offline distillation would read it.
        string ndjson = match.ArmLog.ToNdjson();
        IReadOnlyList<DecisionRecord> reread = DecisionLogCodec.ReadAll(new StringReader(ndjson));
        DecisionDataset dataset = DecisionDataset.FromDecisionLog(reread, DatasetFilter.PrimaryOnly, "twin-valley/1");

        List<DecisionRecord> primaryActivations = match.ArmLog.OfKind(DecisionRecordKinds.IntentActivated)
            .Where(static r => r.Data.GetProperty("role").GetString() == nameof(ProposalRole.Primary))
            .ToList();
        Assert.NotEmpty(primaryActivations);
        Assert.Equal(0, dataset.Skipped);
        Assert.Equal(primaryActivations.Count, dataset.Count);
        for (int i = 0; i < dataset.Count; i++)
        {
            DecisionExample example = dataset.Examples[i];
            JsonElement data = primaryActivations[i].Data;
            Assert.Equal(FeatureVector.Version, example.FeatureVersion);
            Assert.Equal(FeatureVector.Dimension, example.Features.Count);
            Assert.All(example.Features, static v => Assert.True(double.IsFinite(v)));
            Assert.Equal(data.GetProperty("playbookId").GetString(), example.PlaybookId);
            Assert.Equal(Faction.Allied, example.Faction);
            Assert.Equal(IntentSource.Selector, example.Source);
            Assert.Equal("twin-valley/1", example.MatchId);
        }

        // The dataset serialises and a distilled strategist trains on it.
        StringWriter writer = new();
        dataset.WriteNdjson(writer);
        DecisionDataset copy = DecisionDataset.ReadNdjson(new StringReader(writer.ToString()));
        Assert.Equal(dataset.Count, copy.Count);
        DistilledStrategist distilled = new(copy, new PlaybookSelector(), new DistilledOptions(MinExamples: 1));
        Assert.Equal(copy.Count, distilled.TrainedOn);
        Assert.NotEmpty(distilled.Classes);
    }
}
