// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Claude;

/// <summary>Published per-million-token list prices in USD.</summary>
public sealed record ModelPrice(double InputPerMTok, double OutputPerMTok);

/// <summary>
/// List prices for the arena's inference-cost metric. The metric is defined at
/// the published rate for the configured model, so this is a fixed table, not
/// a billing reconciliation: cache-read discounts, batch pricing and fallback
/// re-pricing are deliberately ignored and cache reads are charged as input.
/// </summary>
public static class PriceTable
{
    private static readonly IReadOnlyDictionary<string, ModelPrice> Prices = new Dictionary<string, ModelPrice>(StringComparer.Ordinal)
    {
        ["claude-opus-5"] = new(5, 25),
        ["claude-sonnet-5"] = new(2, 10),
        ["claude-haiku-4-5"] = new(1, 5),
        ["claude-opus-5-5"] = new(4, 20),
        ["claude-fable-5-1"] = new(10, 50),
    };

    /// <summary>Known model ids, sorted.</summary>
    public static IReadOnlyList<string> Models { get; } = Prices.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Looks up a price by model id. Dated snapshot ids (<c>claude-haiku-4-5-20251001</c>)
    /// resolve to their alias by longest known prefix, so the id a reply reports
    /// prices the same as the id that was requested.
    /// </summary>
    public static bool TryGet(string model, out ModelPrice price)
    {
        if (Prices.TryGetValue(model, out ModelPrice? exact))
        {
            price = exact;
            return true;
        }
        string? best = null;
        foreach (string known in Models)
        {
            if (model.StartsWith(known + "-", StringComparison.Ordinal)
                && char.IsDigit(model[known.Length + 1])
                && model.Length - known.Length - 1 >= 8
                && (best is null || known.Length > best.Length))
            {
                best = known;
            }
        }
        if (best is not null)
        {
            price = Prices[best];
            return true;
        }
        price = new ModelPrice(0, 0);
        return false;
    }

    /// <summary>USD for a token count at list price; null when the model is not in the table.</summary>
    public static double? CostUsd(string? model, long inputTokens, long outputTokens, long cacheReadTokens = 0)
    {
        if (model is null || !TryGet(model, out ModelPrice price))
        {
            return null;
        }
        return ((inputTokens + cacheReadTokens) * price.InputPerMTok + outputTokens * price.OutputPerMTok) / 1_000_000.0;
    }

    /// <summary>USD for one proposal's <see cref="ProposalCost"/>; null when its model is unknown.</summary>
    public static double? CostUsd(ProposalCost cost) =>
        CostUsd(cost.Model, cost.InputTokens, cost.OutputTokens, cost.CacheReadTokens);
}
