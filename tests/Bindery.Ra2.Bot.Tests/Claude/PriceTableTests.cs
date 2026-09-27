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
        Assert.Equal(new ModelPrice(4, 20, CacheReadMultiplier: 0.05), opus55);
    }

    [Fact]
    public void Unknown_models_have_no_price()
    {
        Assert.Null(PriceTable.CostUsd("gpt-9", 1000, 1000));
        Assert.Null(PriceTable.CostUsd(null, 1000, 1000));
        Assert.False(PriceTable.TryGet("claude-opus-5-9", out _));
    }

    [Fact]
    public void Cache_reads_bill_at_the_published_discount()
    {
        // A warm Opus 5 call: 200 uncached, 4800 cache-read and 500 output tokens.
        Assert.Equal(0.0159, PriceTable.CostUsd("claude-opus-5", 200, 500, 4800)!.Value, 9);
        Assert.Equal(0.25, PriceTable.CostUsd("claude-fable-5-1", 0, 0, 1_000_000)!.Value, 9);
        Assert.Equal(0.20, PriceTable.CostUsd("claude-opus-5-5", 0, 0, 1_000_000)!.Value, 9);
        Assert.Equal(0.10, PriceTable.CostUsd("claude-haiku-4-5", 0, 0, 1_000_000)!.Value, 9);
        ProposalCost cost = new(1.2, 400_000, 100_000, 600_000, "claude-opus-5");
        Assert.Equal(2.0 + 0.3 + 2.5, PriceTable.CostUsd(cost)!.Value, 9);
    }

    [Fact]
    public void Cache_writes_bill_at_the_five_minute_premium()
    {
        // A cold Opus 5 call that writes the 4800-token cached prefix.
        Assert.Equal(0.0435, PriceTable.CostUsd("claude-opus-5", 200, 500, 0, 4800)!.Value, 9);
    }

    [Theory]
    [InlineData("claude-opus-4-8", 5, 25)]
    [InlineData("claude-opus-4-7", 5, 25)]
    [InlineData("claude-sonnet-4-6", 3, 15)]
    [InlineData("claude-fable-5", 10, 50)]
    public void Server_side_fallback_targets_are_priced(string model, double input, double output)
    {
        Assert.Equal(input, PriceTable.CostUsd(model, 1_000_000, 0));
        Assert.Equal(output, PriceTable.CostUsd(model, 0, 1_000_000));
    }
}
