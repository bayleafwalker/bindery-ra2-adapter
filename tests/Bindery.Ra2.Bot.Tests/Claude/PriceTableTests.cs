// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Claude;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Claude;

public sealed class PriceTableTests
{
    [Theory]
    [InlineData("claude-opus-5", 5, 25)]
    [InlineData("claude-sonnet-5", 2, 10)]
    [InlineData("claude-haiku-4-5", 1, 5)]
    [InlineData("claude-opus-5-5", 4, 20)]
    [InlineData("claude-fable-5-1", 10, 50)]
    public void Published_rates(string model, double input, double output)
    {
        Assert.Equal(input, PriceTable.CostUsd(model, 1_000_000, 0));
        Assert.Equal(output, PriceTable.CostUsd(model, 0, 1_000_000));
    }

    [Fact]
    public void Dated_snapshot_ids_resolve_to_their_alias_by_longest_prefix()
    {
        Assert.True(PriceTable.TryGet("claude-haiku-4-5-20251001", out ModelPrice haiku));
        Assert.Equal(new ModelPrice(1, 5), haiku);
        Assert.True(PriceTable.TryGet("claude-opus-5-5-20260901", out ModelPrice opus55));
        Assert.Equal(new ModelPrice(4, 20), opus55);
    }

    [Fact]
    public void Unknown_models_have_no_price()
    {
        Assert.Null(PriceTable.CostUsd("gpt-9", 1000, 1000));
        Assert.Null(PriceTable.CostUsd(null, 1000, 1000));
        Assert.False(PriceTable.TryGet("claude-opus-5-9", out _));
    }

    [Fact]
    public void Proposal_cost_counts_cache_reads_as_input()
    {
        ProposalCost cost = new(1.2, 400_000, 100_000, 600_000, "claude-opus-5");
        Assert.Equal(5.0 + 2.5, PriceTable.CostUsd(cost)!.Value, 9);
    }
}
