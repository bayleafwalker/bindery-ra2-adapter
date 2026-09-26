// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Sim;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Sim;

public sealed class SkirmishSimulationDeterminismTests
{
    // Invariant 6 (Determinism): same seed, same commands -> same final state hash.
    [Fact]
    public void Replaying_the_same_commands_with_the_same_seed_reaches_the_same_hash()
    {
        TestRules rules = new();
        SimRecorder recorder = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 42, maxSeconds: 30));

        PlayerId p0 = new(0);
        ObservationFrame frame = recorder.Simulation.Observe(p0, ObservationMode.Oracle);
        EntityId mcv = frame.Entities.Single(e => e.Owner == p0 && e.TypeId == TestRules.Mcv).Id;
        recorder.Submit(p0, new DeployCommand("test", mcv));
        recorder.Step();
        for (int i = 0; i < 20; i++) recorder.Step();

        string firstHash = recorder.StateHash;
        recorder.Reset(42);
        string secondHash = recorder.StateHash;

        Assert.Equal(firstHash, secondHash);
    }

    // The failure case this forces: a different seed (different rally-point jitter for a newly produced
    // unit) must NOT collide with the original hash, proving StateHash is actually sensitive to the RNG
    // stream rather than trivially constant.
    [Fact]
    public void A_different_seed_produces_a_different_hash()
    {
        TestRules rules = new();
        PlayerId p0 = new(0);

        string HashAfterProducingAUnit(int seed)
        {
            SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed, maxSeconds: 60));
            SimTestHelpers.DeployStartingMcv(sim, p0);
            SimTestHelpers.BuildBuilding(sim, rules, p0, TestRules.Barracks);
            sim.Submit(p0, new ProduceCommand("test", TestRules.Weak, QueueKind.Infantry));
            sim.Advance(10);
            return sim.ComputeStateHash();
        }

        Assert.NotEqual(HashAfterProducingAUnit(1), HashAfterProducingAUnit(2));
    }

    [Fact]
    public void Two_independent_simulations_with_the_same_seed_and_commands_agree()
    {
        TestRules rules = new();
        SimMap map = TestMaps.TwoPlayerCombat();
        SimSettings settings = SimTestHelpers.TwoPlayers(seed: 7, maxSeconds: 40);

        SkirmishSimulation a = new(map, rules, settings);
        SkirmishSimulation b = new(map, rules, settings);

        SimTestHelpers.DeployStartingMcv(a, new PlayerId(0));
        SimTestHelpers.DeployStartingMcv(b, new PlayerId(0));
        for (int i = 0; i < 30; i++) { a.Step(); b.Step(); }

        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
    }

    // The hash is determinism evidence, so it must cover every piece of state a later frame depends on: a unit
    // nudged by less than a cell, or a queue that differs, is a diverged simulation even when nothing whole-cell moved.
    [Fact]
    public void The_state_hash_sees_sub_cell_movement_and_queue_contents()
    {
        TestRules rules = new();
        SkirmishSimulation a = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 1, maxSeconds: 30));
        SkirmishSimulation b = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 1, maxSeconds: 30));
        PlayerId p0 = new(0);
        ObservedEntity mcv = b.Observe(p0, ObservationMode.Oracle).Entities.Single(e => e.Owner == p0 && e.TypeId == TestRules.Mcv);

        b.Submit(p0, new MoveCommand("test", [mcv.Id], new Cell(mcv.Position.X + 20, mcv.Position.Y)));
        a.Step();
        b.Step();
        b.Submit(p0, new StopCommand("test", [mcv.Id]));
        a.Step();
        b.Step();
        Assert.Equal(
            a.Observe(p0, ObservationMode.Oracle).Entities.Select(static e => e.Position),
            b.Observe(p0, ObservationMode.Oracle).Entities.Select(static e => e.Position));
        Assert.NotEqual(a.ComputeStateHash(), b.ComputeStateHash());

        SkirmishSimulation c = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 1, maxSeconds: 30));
        SkirmishSimulation d = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 1, maxSeconds: 30));
        d.DebugEnqueue(new PlayerId(1), QueueKind.Building, TestRules.Power);
        Assert.NotEqual(c.ComputeStateHash(), d.ComputeStateHash());
    }
}
