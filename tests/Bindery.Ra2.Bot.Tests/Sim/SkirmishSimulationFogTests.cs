// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Sim;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Sim;

public sealed class SkirmishSimulationFogTests
{
    // Invariant 1 (Fog): belief-mode frames must exclude enemy state the observer has not seen.
    [Fact]
    public void Belief_frame_excludes_enemy_credits_and_far_entities()
    {
        TestRules rules = new();
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 3, maxSeconds: 30));
        PlayerId observer = new(0);
        PlayerId enemy = new(1);

        sim.Step();
        ObservationFrame belief = sim.Observe(observer, ObservationMode.Belief);

        // The contract only ever carries the observer's own credits: there is no enemy-credits field to leak.
        Assert.Equal(observer, belief.Self);
        // The enemy's MCV starts in "start-b", three hops away and out of any starting unit's sight: it must not appear.
        Assert.DoesNotContain(belief.Entities, e => e.Owner == enemy);
        Assert.DoesNotContain(belief.VisibleRegions, r => r.Value == 3);
    }

    [Fact]
    public void Oracle_frame_includes_the_enemy_even_when_unseen()
    {
        TestRules rules = new();
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 3, maxSeconds: 30));
        PlayerId observer = new(0);
        PlayerId enemy = new(1);

        sim.Step();
        ObservationFrame oracle = sim.Observe(observer, ObservationMode.Oracle);

        Assert.Contains(oracle.Entities, e => e.Owner == enemy);
    }

    // This is the arena's own probe: mutating enemy credits, an enemy queue, and an enemy unit in an
    // unseen region must not change one byte of the observer's belief-mode frame.
    [Fact]
    public void Leakage_probe_finds_no_difference_in_belief_frame()
    {
        TestRules rules = new();
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 9, maxSeconds: 30));
        PlayerId observer = new(0);
        sim.Step();

        string before = JsonSerializer.Serialize(sim.Observe(observer, ObservationMode.Belief), BotJson.Options);
        SimLeakageProbe.PerturbHidden(sim, observer);
        string after = JsonSerializer.Serialize(sim.Observe(observer, ObservationMode.Belief), BotJson.Options);

        Assert.Equal(before, after);
    }

    // Forces the failure case: if fog were broken (e.g. the probe's spawned enemy leaked into
    // an oracle-mode frame instead), the two serializations would differ. Oracle mode is exempt
    // from fog by contract, so it is expected to change.
    [Fact]
    public void Leakage_probe_does_change_the_oracle_frame()
    {
        TestRules rules = new();
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 9, maxSeconds: 30));
        PlayerId observer = new(0);
        sim.Step();

        string before = JsonSerializer.Serialize(sim.Observe(observer, ObservationMode.Oracle), BotJson.Options);
        SimLeakageProbe.PerturbHidden(sim, observer);
        string after = JsonSerializer.Serialize(sim.Observe(observer, ObservationMode.Oracle), BotJson.Options);

        Assert.NotEqual(before, after);
    }
}
