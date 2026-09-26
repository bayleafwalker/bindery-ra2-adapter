// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using Bindery.Ra2.Bot.Analysis;

namespace Bindery.Ra2.Bot.Arena;

/// <summary>One per-match metric the paired tables compare, read from the arm side of a match record.</summary>
/// <param name="HigherIsBetter">Direction for the better/worse counts; null for metrics with no better direction (duration, churn).</param>
public sealed record MatchMetric(string Name, Func<MatchRecord, double?> Value, bool? HigherIsBetter);

/// <summary>
/// Paired comparisons between arms: two matches pair when they share opponent, map and seed (and so faction and
/// benchmark settings), which removes the between-match variance a pooled comparison would carry. Each metric gets
/// the mean difference with a 95% bootstrap interval, the better/worse/tied pair counts, an exact sign test, and a
/// Holm-adjusted p over the arm's metrics.
/// </summary>
public static class PairedReport
{
    public static IReadOnlyList<MatchMetric> Metrics { get; } =
    [
        new("score (win 1, draw ½)", static m => m.Winner switch { 0 => 1.0, null => 0.5, _ => 0.0 }, true),
        new("final asset margin", static m => m.Players["arm"].FinalAssetValue - m.Players["opponent"].FinalAssetValue, true),
        new("value destroyed", static m => m.Players["arm"].AssetValueDestroyedByOpponent, true),
        new("value lost", static m => m.Players["arm"].AssetValueLostByPlayer, false),
        new("trade share", static m => TradeShare(m.Players["arm"]), true),
        new("peak army value", static m => m.Players["arm"].PeakArmyValue, true),
        new("units built", static m => m.Players["arm"].UnitsBuilt, true),
        new("production idle fraction", static m => m.Players["arm"].ProductionIdleFraction, false),
        new("average credits (float)", static m => m.Players["arm"].AverageCreditsOnHand, false),
        new("first attack s", static m => m.Players["arm"].FirstAttackSeconds, null),
        new("duration s", static m => m.DurationSeconds, null),
        new("activations /10 min", static m => m.DurationSeconds <= 0 ? null : m.Players["arm"].Activations / m.DurationSeconds * 600, null),
        new("invalid plan rate", static m => m.Players["arm"].Proposals == 0 ? null : m.Players["arm"].Rejected / (double)m.Players["arm"].Proposals, false),
        new("USD per match", static m => m.Players["arm"].Usd, false),
    ];

    /// <summary>Destroyed / (destroyed + lost), in [0, 1]; ½ when nothing was traded.</summary>
    public static double TradeShare(PlayerMatchMetrics p)
    {
        double total = p.AssetValueDestroyedByOpponent + p.AssetValueLostByPlayer;
        return total <= 0 ? 0.5 : p.AssetValueDestroyedByOpponent / total;
    }

    /// <summary>Pairs every match of <paramref name="arm"/> with the <paramref name="baseline"/> match of the same opponent, map and seed.</summary>
    public static IReadOnlyList<(MatchRecord Baseline, MatchRecord Arm)> Pairs(IReadOnlyList<MatchRecord> matches, string baseline, string arm, string? split = null)
    {
        Dictionary<(string, string, int), MatchRecord> baseByKey = matches
            .Where(m => m.Arm == baseline && (split is null || m.Split == split))
            .ToDictionary(static m => (m.Opponent, m.Map, m.Seed));
        return [.. matches
            .Where(m => m.Arm == arm && (split is null || m.Split == split))
            .OrderBy(static m => m.Opponent, StringComparer.Ordinal).ThenBy(static m => m.Map, StringComparer.Ordinal).ThenBy(static m => m.Seed)
            .Where(m => baseByKey.ContainsKey((m.Opponent, m.Map, m.Seed)))
            .Select(m => (baseByKey[(m.Opponent, m.Map, m.Seed)], m))];
    }

    /// <summary>Every metric compared for one arm against the baseline, with Holm-adjusted p values in the same order.</summary>
    public static IReadOnlyList<(PairedDifference Difference, double HolmP, bool? HigherIsBetter)> Compare(IReadOnlyList<(MatchRecord Baseline, MatchRecord Arm)> pairs)
    {
        List<(PairedDifference Difference, bool? HigherIsBetter)> rows = [];
        foreach (MatchMetric metric in Metrics)
        {
            List<(double, double)> values = [];
            foreach ((MatchRecord b, MatchRecord a) in pairs)
            {
                if (metric.Value(b) is { } vb && metric.Value(a) is { } va && double.IsFinite(vb) && double.IsFinite(va)) values.Add((vb, va));
            }
            rows.Add((PairedStatistics.Compare(metric.Name, values, metric.HigherIsBetter ?? true), metric.HigherIsBetter));
        }
        IReadOnlyList<double> holm = PairedStatistics.HolmAdjust([.. rows.Select(static r => r.Difference.SignTestP)]);
        return [.. rows.Select((r, i) => (r.Difference, holm[i], r.HigherIsBetter))];
    }

    /// <summary>Appends one table per arm (every arm but the baseline) of paired differences against the baseline.</summary>
    public static void Append(StringBuilder sb, IReadOnlyList<MatchRecord> matches, string baseline, string heading, string? split = null, Func<string, string?>? baselineFor = null)
    {
        List<string> arms = [.. matches.Select(static m => m.Arm).Distinct(StringComparer.Ordinal).OrderBy(static a => a, StringComparer.Ordinal)];
        bool any = false;
        foreach (string arm in arms)
        {
            string? against = baselineFor is null ? (arm == baseline ? null : baseline) : baselineFor(arm);
            if (against is null) continue;
            IReadOnlyList<(MatchRecord Baseline, MatchRecord Arm)> pairs = Pairs(matches, against, arm, split);
            if (pairs.Count == 0) continue;
            if (!any)
            {
                sb.AppendLine(heading);
                sb.AppendLine();
                sb.AppendLine("Pairs share opponent, map and seed. Difference = arm − baseline, mean with a 95% seeded bootstrap interval; better/worse/tied count pairs by the metric's direction (n/d: no better direction, counted as higher/lower); p is the exact two-sided sign test over untied pairs, Holm-adjusted over the arm's metrics. `*` marks Holm p < 0.05.");
                sb.AppendLine();
                any = true;
            }
            sb.AppendLine($"### {arm} vs {against} ({pairs.Count} pairs)");
            sb.AppendLine();
            sb.AppendLine("| Metric | Pairs | Baseline mean | Arm mean | Difference [95% CI] | Better / worse / tied | Sign p | Holm p |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach ((PairedDifference d, double holm, bool? direction) in Compare(pairs))
            {
                if (d.Pairs == 0)
                {
                    sb.AppendLine($"| {d.Metric} | 0 | n/a | n/a | n/a | n/a | n/a | n/a |");
                    continue;
                }
                string counts = direction is null ? $"{d.Better} / {d.Worse} / {d.Ties} (n/d)" : $"{d.Better} / {d.Worse} / {d.Ties}";
                string mark = holm < 0.05 ? " *" : string.Empty;
                sb.AppendLine($"| {d.Metric} | {d.Pairs} | {F(d.BaselineMean)} | {F(d.ArmMean)} | {F(d.MeanDifference)} [{F(d.CiLow)}, {F(d.CiHigh)}] | {counts} | {P(d.SignTestP)} | {P(holm)}{mark} |");
            }
            sb.AppendLine();
        }
    }

    private static string F(double value) => double.IsFinite(value) ? value.ToString(Math.Abs(value) >= 100 ? "0" : "0.###", CultureInfo.InvariantCulture) : "n/a";

    private static string P(double value) => value < 0.0001 ? "<0.0001" : value.ToString("0.0000", CultureInfo.InvariantCulture);
}
