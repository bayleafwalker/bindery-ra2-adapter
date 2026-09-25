// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Runtime;
using Bindery.Ra2.Bot.Sim;
using Bindery.Ra2.Bot.Strategy;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Integration;

/// <summary>Invariants 1 (fog) and 2 (freshness) forced end to end, in a real runtime loop.</summary>
public sealed class FogAndFreshnessTests
{
    [Fact]
    public void Perturbing_hidden_simulator_state_leaves_the_strategist_context_hash_unchanged()
    {
        using MatchHarness control = MatchHarness.Create(new PlaybookSelector(), "rush", seed: 3);
        using MatchHarness probed = MatchHarness.Create(new PlaybookSelector(), "rush", seed: 3);
        control.RunUntil(90);
        probed.RunUntil(90);
        Assert.Equal(control.Sim.ComputeStateHash(), probed.Sim.ComputeStateHash());

        SimLeakageProbe.PerturbHidden(probed.Sim, MatchHarness.ArmPlayer);
        Assert.NotEqual(control.Sim.ComputeStateHash(), probed.Sim.ComputeStateHash());

        int compared = 0;
        for (int i = 0; i < GameTime.FramesPerSecond * 2; i++)
        {
            control.Frame();
            probed.Frame();
            StrategistContext a = control.Arm.CurrentStrategistContext!;
            StrategistContext b = probed.Arm.CurrentStrategistContext!;
            Assert.Equal(StrategistContextHash.ToJson(a), StrategistContextHash.ToJson(b));
            compared++;
        }
        Assert.Equal(GameTime.FramesPerSecond * 2, compared);
    }

    /// <summary>An LLM-like strategist (source Llm) that always proposes one playbook.</summary>
    private sealed class FakeLlm(string playbookId) : IStrategist
    {
        public string Id => "fake-llm";

        public IntentSource Source => IntentSource.Llm;

        public Task<StrategistProposal?> ProposeAsync(StrategistContext context, CancellationToken cancellationToken = default)
        {
            context.Playbooks.TryGet(playbookId, out Playbook playbook);
            StrategicIntent intent = IntentComposer.Compose(playbook, context.Features, $"fake-llm/{context.Features.SnapshotVersion}", Source, 0.9, "fake");
            return Task.FromResult<StrategistProposal?>(new StrategistProposal(intent, new ProposalCost(0, 100, 10, 0, "fake-model"), "{}"));
        }
    }

    [Fact]
    public void A_proposal_slower_than_the_freshness_window_is_discarded_and_the_fallback_keeps_the_bot_running()
    {
        // 20 s of game-time latency against the 15 s MaxProposalAgeSeconds: every answer is late.
        SimulatedLatencyStrategist slow = new(new FakeLlm("allied-boom"), fixedLatencySeconds: 20);
        using MatchHarness match = MatchHarness.Create(slow, "balanced", seed: 1);
        match.RunUntil(180);

        IReadOnlyList<DecisionRecord> late = match.ArmLog.OfKind(DecisionRecordKinds.LateDiscarded);
        Assert.NotEmpty(late);
        Assert.All(late, static r =>
        {
            Assert.Equal("fake-llm", r.Data.GetProperty("strategistId").GetString());
            Assert.Equal("age", r.Data.GetProperty("reason").GetString());
            Assert.True(r.Data.GetProperty("ageSeconds").GetDouble() > 15);
        });
        Assert.Equal(late.Count, match.Arm.Metrics.LateDiscarded);
        Assert.DoesNotContain(match.ArmLog.OfKind(DecisionRecordKinds.IntentActivated),
            static r => r.Data.GetProperty("source").GetString() == nameof(IntentSource.Llm));
        Assert.NotNull(match.Arm.ActiveIntent);
        Assert.Equal(IntentSource.Selector, match.Arm.ActiveIntent!.Source);
    }

    [Fact]
    public void A_proposal_inside_the_freshness_window_is_applied()
    {
        SimulatedLatencyStrategist timely = new(new FakeLlm("allied-boom"), fixedLatencySeconds: 4);
        using MatchHarness match = MatchHarness.Create(timely, "balanced", seed: 1);
        match.RunUntil(180);

        Assert.Empty(match.ArmLog.OfKind(DecisionRecordKinds.LateDiscarded));
        DecisionRecord activation = match.ArmLog.OfKind(DecisionRecordKinds.IntentActivated)
            .First(static r => r.Data.GetProperty("source").GetString() == nameof(IntentSource.Llm));
        JsonElement data = activation.Data;
        Assert.Equal("allied-boom", data.GetProperty("playbookId").GetString());
        // Applied 4 s after the snapshot it was based on.
        long basedOn = data.GetProperty("basedOnSnapshotVersion").GetInt64();
        Assert.True(activation.SnapshotVersion - basedOn >= 4 * GameTime.FramesPerSecond - 1);
    }
}
