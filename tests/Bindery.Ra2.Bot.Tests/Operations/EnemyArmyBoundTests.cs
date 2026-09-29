// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Operations;
using Bindery.Ra2.Bot.Tuning;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Operations;

/// <summary>The base-sighting cap bounds only the base-freshness term of the attack gate's evidence weight.</summary>
public sealed class EnemyArmyBoundTests
{
    private static readonly OperationalOptions Options = new();

    private static EnemyFeatures Enemy(double confidence, double armyAge) =>
        new(Trend.Flat(500), confidence, new Dictionary<UnitRole, double>(), new HashSet<string>(), [], armyAge, armyAge, false);

    private static double OldFormula(EnemyFeatures e, double baseAge, OperationalOptions o)
    {
        double Fresh(double a) => o.EnemyEvidenceSeconds <= 0 || double.IsNaN(a) ? 0 : Math.Max(0, 1 - (a / o.EnemyEvidenceSeconds));
        return Math.Max(Math.Clamp(e.ArmyValueConfidence, 0, 1) * Fresh(e.NewestObservationAgeSeconds), Fresh(baseAge));
    }

    public static TheoryData<double, double> Ages()
    {
        TheoryData<double, double> data = [];
        foreach (double armyAge in new[] { 0, 10, 30, 59, 60, 90, double.PositiveInfinity })
            foreach (double baseAge in new[] { 0, 5, 20, 45, 60, 200, double.PositiveInfinity })
                data.Add(armyAge, baseAge);
        return data;
    }

    [Theory]
    [MemberData(nameof(Ages))]
    public void Cap_of_one_equals_the_uncapped_formula(double armyAge, double baseAge)
    {
        foreach (double confidence in new[] { 0, 0.4, 1 })
        {
            EnemyFeatures e = Enemy(confidence, armyAge);
            Assert.Equal(OldFormula(e, baseAge, Options), EnemyArmyBound.EvidenceWeight(e, baseAge, Options with { BaseSightingWeightCap = 1.0 }));
        }
    }

    [Fact]
    public void Half_cap_with_no_army_sighting_bounds_the_weight_and_raises_the_required_ratio()
    {
        OperationalOptions capped = Options with { BaseSightingWeightCap = 0.5 };
        EnemyFeatures none = Enemy(0, double.PositiveInfinity);
        double midpoint = (Options.MinAttackForceRatio + Options.SeenAttackForceRatio) / 2;
        foreach (double baseAge in new[] { 0, 5, 30, 60 })
        {
            Assert.True(EnemyArmyBound.EvidenceWeight(none, baseAge, capped) <= 0.5);
            Assert.True(EnemyArmyBound.RequiredForceRatio(none, baseAge, capped) >= midpoint - 1e-9);
        }
        Assert.Equal(0.5, EnemyArmyBound.EvidenceWeight(none, 0, capped), 9);
    }

    [Fact]
    public void Army_sighting_is_not_capped()
    {
        EnemyFeatures fresh = Enemy(1, 0);
        Assert.Equal(1.0, EnemyArmyBound.EvidenceWeight(fresh, double.PositiveInfinity, Options with { BaseSightingWeightCap = 0 }), 9);
        Assert.Equal(1.0, EnemyArmyBound.EvidenceWeight(fresh, 0, Options with { BaseSightingWeightCap = 0.3 }), 9);
    }

    [Fact]
    public void Knob_parses_and_is_diagnostic_only()
    {
        CliOptions parsed = CliOptions.Parse(["run", "--knob", "BaseSightingWeightCap=0.5"]);
        Assert.Equal(0.5, parsed.ArmKnobs["BaseSightingWeightCap"]);
        Assert.Single(TuningKnobs.Diagnostic, k => k.Name == "BaseSightingWeightCap");
        Assert.DoesNotContain(TuningKnobs.Operational, k => k.Name == "BaseSightingWeightCap");
        Assert.Equal(0.5, TuningKnobs.Get(TuningKnobs.Set(new OperationalOptions(), "BaseSightingWeightCap", 0.5), "BaseSightingWeightCap"));
    }
}
