// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arbitration;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arbitration;

public sealed class ConditionEvaluatorTests
{
    private static ThreatAssessment Threat(RegionId region, double enemy, double own, bool isBase, double localForceRatio = 1) =>
        new(region, enemy, own, localForceRatio, 20, 10, isBase, 0.8);

    [Fact]
    public void Army_value_ratio_is_own_over_enemy_and_capped_when_enemy_is_zero()
    {
        Assert.Equal(0.5, ConditionEvaluator.ArmyValueRatio(Fx.Features(1, ownArmy: 500, enemyArmy: 1000)));
        Assert.Equal(ConditionEvaluator.RatioCap, ConditionEvaluator.ArmyValueRatio(Fx.Features(1, ownArmy: 500, enemyArmy: 0)));
        Assert.True(ConditionEvaluator.Holds(new Condition(ConditionMetric.ArmyValueRatio, Comparison.Lt, 0.6), Fx.Features(1, ownArmy: 500, enemyArmy: 1000)));
        Assert.False(ConditionEvaluator.Holds(new Condition(ConditionMetric.ArmyValueRatio, Comparison.Lt, 0.5), Fx.Features(1, ownArmy: 500, enemyArmy: 1000)));
        Assert.True(ConditionEvaluator.Holds(new Condition(ConditionMetric.ArmyValueRatio, Comparison.Le, 0.5), Fx.Features(1, ownArmy: 500, enemyArmy: 1000)));
    }

    [Fact]
    public void Base_threat_ratio_is_the_worst_base_threat_and_ignores_non_base_regions()
    {
        StrategicFeatures features = Fx.Features(1, threats:
        [
            Threat(Fx.R0, 1200, 800, isBase: true),
            Threat(Fx.R3, 5000, 100, isBase: false),
            Threat(Fx.R1, 300, 600, isBase: true),
        ]);
        Assert.Equal(1.5, ConditionEvaluator.BaseThreatRatio(features), 9);
        Assert.Equal(0, ConditionEvaluator.BaseThreatRatio(Fx.Features(1)));
        Assert.Equal(ConditionEvaluator.RatioCap, ConditionEvaluator.BaseThreatRatio(Fx.Features(1, threats: [Threat(Fx.R0, 100, 0, isBase: true)])));
    }

    [Fact]
    public void Local_force_ratio_without_region_is_false_with_an_issue()
    {
        ConditionResult result = ConditionEvaluator.Evaluate(new Condition(ConditionMetric.LocalForceRatio, Comparison.Ge, 0), Fx.Features(1));
        Assert.False(result.Holds);
        Assert.Equal(ConditionEvaluator.RegionRequiredCode, result.Issue?.Code);
    }

    [Fact]
    public void Local_force_ratio_reads_the_region_threat_and_is_capped_without_one()
    {
        StrategicFeatures features = Fx.Features(1, threats: [Threat(Fx.R1, 1000, 400, isBase: false, localForceRatio: 0.4)]);
        Assert.Equal(0.4, ConditionEvaluator.Evaluate(new Condition(ConditionMetric.LocalForceRatio, Comparison.Lt, 0.6, Fx.R1), features).Value);
        Assert.Equal(ConditionEvaluator.RatioCap, ConditionEvaluator.Evaluate(new Condition(ConditionMetric.LocalForceRatio, Comparison.Lt, 0.6, Fx.R2), features).Value);
    }

    [Fact]
    public void Scouting_age_is_per_region_and_infinite_for_never_seen_regions()
    {
        StrategicFeatures features = Fx.Features(1);
        Assert.Equal(30, ConditionEvaluator.Evaluate(new Condition(ConditionMetric.ScoutingAgeSeconds, Comparison.Gt, 0, Fx.R1), features).Value);
        Assert.True(ConditionEvaluator.Holds(new Condition(ConditionMetric.ScoutingAgeSeconds, Comparison.Gt, 1e9, Fx.R3), features));
        Assert.False(ConditionEvaluator.Holds(new Condition(ConditionMetric.ScoutingAgeSeconds, Comparison.Gt, 10, Fx.R0), features));
    }

    [Fact]
    public void Losses_metric_is_the_fifteen_second_delta()
    {
        StrategicFeatures features = Fx.Features(1) with
        {
            Army = Fx.Features(1).Army with { LossesValue = new Trend(9000, 100, 1500, 4000) },
        };
        Assert.Equal(1500, ConditionEvaluator.Evaluate(new Condition(ConditionMetric.LossesValue15s, Comparison.Gt, 0), features).Value);
    }

    [Fact]
    public void Scalar_metrics_read_the_documented_features()
    {
        StrategicFeatures f = Fx.Features(30, ownArmy: 700, enemyArmy: 900, credits: 1234, income: 800);
        double Value(ConditionMetric m) => ConditionEvaluator.Evaluate(new Condition(m, Comparison.Ge, 0), f).Value;
        Assert.Equal(30, Value(ConditionMetric.GameSeconds));
        Assert.Equal(1234, Value(ConditionMetric.Credits));
        Assert.Equal(800, Value(ConditionMetric.IncomePerMinute));
        Assert.Equal(700, Value(ConditionMetric.OwnArmyValue));
        Assert.Equal(900, Value(ConditionMetric.EnemyArmyValueEstimate));
        Assert.Equal(2, Value(ConditionMetric.HarvesterCount));
    }

    [Fact]
    public void Nan_threshold_never_holds()
    {
        Assert.False(ConditionEvaluator.Holds(new Condition(ConditionMetric.Credits, Comparison.Gt, double.NaN), Fx.Features(1)));
        Assert.False(ConditionEvaluator.Holds(new Condition(ConditionMetric.Credits, Comparison.Lt, double.NaN), Fx.Features(1)));
    }

    [Fact]
    public void All_of_empty_holds_and_any_of_empty_does_not()
    {
        StrategicFeatures f = Fx.Features(1);
        Assert.True(ConditionEvaluator.AllOf([], f));
        Assert.False(ConditionEvaluator.AnyOf([], f));
        Condition yes = new(ConditionMetric.Credits, Comparison.Gt, 0);
        Condition no = new(ConditionMetric.Credits, Comparison.Lt, 0);
        Assert.False(ConditionEvaluator.AllOf([yes, no], f));
        Assert.True(ConditionEvaluator.AnyOf([no, yes], f));
        Assert.Equal(yes, ConditionEvaluator.FirstHolding([no, yes], f));
    }
}
