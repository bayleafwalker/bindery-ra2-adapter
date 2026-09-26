// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Claude;

/// <summary>Published per-million-token list prices in USD, with the prompt-cache multipliers of the base input price.</summary>
/// <param name="CacheReadMultiplier">Cache reads as a fraction of the input price (0.1 on most models).</param>
/// <param name="CacheWriteMultiplier">Five-minute cache writes as a multiple of the input price (the strategist's breakpoints are ephemeral).</param>
public sealed record ModelPrice(double InputPerMTok, double OutputPerMTok, double CacheReadMultiplier = 0.1, double CacheWriteMultiplier = 1.25);

/// <summary>
/// List prices for the arena's inference-cost metric: the published rate for each model, including the prompt
/// cache's read discount and write premium, because the strategist caches its system prompt and match context and
/// so nearly every call is a cache read or a cache write. Models that the server-side refusal fallback
/// (<c>fallbacks: "default"</c>) may serve a request on are listed too, so a fallback-served reply is priced at
/// its own rate instead of being dropped as unknown. Batch pricing does not apply to the strategist.
/// </summary>
public static class PriceTable
{
    private static readonly IReadOnlyDictionary<string, ModelPrice> Prices = new Dictionary<string, ModelPrice>(StringComparer.Ordinal)
    {
        ["claude-opus-5"] = new(5, 25),
        ["claude-sonnet-5"] = new(2, 10),
        ["claude-haiku-4-5"] = new(1, 5),
        ["claude-opus-5-5"] = new(4, 20, CacheReadMultiplier: 0.05),
        ["claude-fable-5-1"] = new(10, 50, CacheReadMultiplier: 0.025),
        // Server-side fallback targets.
        ["claude-fable-5"] = new(10, 50),
        ["claude-opus-4-8"] = new(5, 25),
        ["claude-opus-4-7"] = new(5, 25),
        ["claude-opus-4-6"] = new(5, 25),
        ["claude-sonnet-4-6"] = new(3, 15),
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

    /// <summary>
    /// USD at list price for uncached input, output, cache-read and cache-write tokens; null when the model is not
    /// in the table.
    /// </summary>
    public static double? CostUsd(string? model, long inputTokens, long outputTokens, long cacheReadTokens = 0, long cacheCreationTokens = 0)
    {
        if (model is null || !TryGet(model, out ModelPrice price))
        {
            return null;
        }
        double input = inputTokens + (cacheReadTokens * price.CacheReadMultiplier) + (cacheCreationTokens * price.CacheWriteMultiplier);
        return ((input * price.InputPerMTok) + (outputTokens * price.OutputPerMTok)) / 1_000_000.0;
    }

    /// <summary>USD for one proposal's <see cref="ProposalCost"/>; null when its model is unknown.</summary>
    public static double? CostUsd(ProposalCost cost) =>
        CostUsd(cost.Model, cost.InputTokens, cost.OutputTokens, cost.CacheReadTokens);
}
