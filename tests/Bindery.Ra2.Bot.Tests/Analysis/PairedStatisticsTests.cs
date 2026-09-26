// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Analysis;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Analysis;

public sealed class PairedStatisticsTests
{
    [Theory]
    [InlineData(9, 1, 0.021484375)]
    [InlineData(5, 5, 1.0)]
    [InlineData(0, 0, 1.0)]
    [InlineData(10, 0, 0.001953125)]
    public void Sign_test_is_the_exact_two_sided_binomial(int better, int worse, double expected)
    {
        Assert.Equal(expected, PairedStatistics.SignTestTwoSided(better, worse), 9);
    }

    [Fact]
    public void Sign_test_stays_finite_for_large_samples()
    {
        double p = PairedStatistics.SignTestTwoSided(600, 400);
        Assert.InRange(p, 1e-12, 1e-8);
    }

    [Fact]
    public void Holm_adjustment_is_monotone_and_capped()
    {
        Assert.Equal([0.03, 0.06, 0.06], PairedStatistics.HolmAdjust([0.01, 0.04, 0.03]));
        Assert.Equal([1.0, 1.0], PairedStatistics.HolmAdjust([0.9, 0.7]));
    }

    [Fact]
    public void Compare_counts_direction_and_brackets_the_mean_difference()
    {
        List<(double Baseline, double Arm)> pairs = [.. Enumerable.Range(0, 40).Select(static i => ((double)i, i + 1.0 + (i % 3) * 0.5))];

        PairedDifference d = PairedStatistics.Compare("m", pairs, higherIsBetter: true);

        Assert.Equal(40, d.Pairs);
        Assert.Equal(40, d.Better);
        Assert.Equal(0, d.Worse);
        Assert.InRange(d.MeanDifference, d.CiLow, d.CiHigh);
        Assert.True(d.CiLow > 0);
        Assert.True(d.SignTestP < 1e-6);
        // Deterministic: the bootstrap is seeded.
        Assert.Equal(d, PairedStatistics.Compare("m", pairs, higherIsBetter: true));
    }

    [Fact]
    public void Lower_is_better_metrics_count_a_decrease_as_better()
    {
        PairedDifference d = PairedStatistics.Compare("idle", [(0.5, 0.2), (0.4, 0.1), (0.3, 0.3)], higherIsBetter: false);
        Assert.Equal(2, d.Better);
        Assert.Equal(0, d.Worse);
        Assert.Equal(1, d.Ties);
    }

    [Fact]
    public void No_pairs_gives_an_empty_result_not_an_exception()
    {
        PairedDifference d = PairedStatistics.Compare("m", [], higherIsBetter: true);
        Assert.Equal(0, d.Pairs);
        Assert.Equal(1.0, d.SignTestP);
    }
}
