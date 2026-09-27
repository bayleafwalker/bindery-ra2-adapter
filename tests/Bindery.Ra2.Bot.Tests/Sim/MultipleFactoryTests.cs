// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Sim;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Sim;

/// <summary>
/// Rules that name RA2's <c>MultipleFactory=</c> set the extra-factory speed-up (each extra factory multiplies build
/// time by it); rules that do not keep the simulator's sqrt(factories) model.
/// </summary>
public sealed class MultipleFactoryTests
{
    /// <summary><see cref="TestRules"/> with a 10 s tank and an optional MultipleFactory.</summary>
    private sealed class SlowTankRules(double? multipleFactory) : IRulesDatabase
    {
        private readonly TestRules inner = new();

        public double? MultipleFactory => multipleFactory;

        public string RulesetId => inner.RulesetId;

        public IReadOnlyCollection<UnitRule> All => [.. inner.All.Select(Slow)];

        public bool TryGet(string typeId, out UnitRule rule)
        {
            bool found = inner.TryGet(typeId, out rule);
            if (found) rule = Slow(rule);
            return found;
        }

        public UnitRule Get(string typeId) => Slow(inner.Get(typeId));

        public bool CanBuild(Faction faction, IReadOnlySet<string> ownedBuildingTypes, string typeId) => inner.CanBuild(faction, ownedBuildingTypes, typeId);

        public IReadOnlyList<string>? PathTo(Faction faction, IReadOnlySet<string> ownedBuildingTypes, string typeId) => inner.PathTo(faction, ownedBuildingTypes, typeId);

        public double Effectiveness(string attacker, string defender) => inner.Effectiveness(attacker, defender);

        private static UnitRule Slow(UnitRule rule) => rule.TypeId == TestRules.Strong ? rule with { BuildSeconds = 10 } : rule;
    }

    private static double ProgressAfterOneSecondWithTwoFactories(double? multipleFactory)
    {
        SlowTankRules rules = new(multipleFactory);
        TestRules buildings = new();
        SkirmishSimulation sim = new(TestMaps.TwoPlayerCombat(), rules, SimTestHelpers.TwoPlayers(seed: 11, maxSeconds: 120));
        PlayerId player = new(0);
        SimTestHelpers.DeployStartingMcv(sim, player);
        SimTestHelpers.BuildBuilding(sim, buildings, player, TestRules.Power, buildSecondsBudget: 3);
        SimTestHelpers.BuildBuilding(sim, buildings, player, TestRules.Power, buildSecondsBudget: 3);
        SimTestHelpers.BuildBuilding(sim, buildings, player, TestRules.WarFactory, buildSecondsBudget: 3);
        SimTestHelpers.BuildBuilding(sim, buildings, player, TestRules.WarFactory, buildSecondsBudget: 3);
        Assert.False(sim.Observe(player).Power.LowPower);

        sim.Submit(player, new ProduceCommand("test", TestRules.Strong, QueueKind.Vehicle));
        sim.Step();
        sim.Advance(1);
        ProductionQueueState vehicles = sim.Observe(player).Queues.Single(q => q.Kind == QueueKind.Vehicle);
        Assert.Equal(2, vehicles.Factories);
        return vehicles.Items.Single().Progress;
    }

    [Fact]
    public void Rules_multiple_factory_sets_the_extra_factory_speed_up()
    {
        // 0.8 per extra factory: build time x 0.8, so 1.25 s of a 10 s item per second.
        Assert.Equal(0.125, ProgressAfterOneSecondWithTwoFactories(0.8), 3);
    }

    [Fact]
    public void Without_multiple_factory_two_factories_run_sqrt_two_faster()
    {
        Assert.Equal(Math.Sqrt(2) / 10, ProgressAfterOneSecondWithTwoFactories(null), 3);
    }
}
