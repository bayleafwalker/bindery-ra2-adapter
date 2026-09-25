// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Bindery.Ra2.Bot.Arena;

/// <summary>
/// Turns a completed run's <see cref="MatchRecord"/>s into <c>report.md</c>,
/// using exactly the metric definitions in
/// <c>docs/architecture/strategic-bot.md</c>'s Metrics section. All figures
/// come from <c>results.json</c> (sim state and agent stats); nothing here
/// is estimated separately.
/// </summary>
public static class ReportBuilder
{
    public static string Build(IReadOnlyList<MatchRecord> matches, bool leakageProbePassed)
    {
        StringBuilder sb = new();
        sb.AppendLine("# Bindery region sim arena report");
        sb.AppendLine();
        sb.AppendLine("Results are from the bindery region sim, not retail RA2. Figures below are computed from `results.json`.");
        sb.AppendLine();
        sb.AppendLine($"Matches: {matches.Count}");
        sb.AppendLine();

        AppendWinRate(sb, matches);
        AppendInvalidPlans(sb, matches);
        AppendLateness(sb, matches);
        AppendChurn(sb, matches);
        AppendIdleAndFloat(sb, matches);
        AppendTradeEfficiency(sb, matches);
        AppendInferenceCost(sb, matches);
        AppendLeakage(sb, matches, leakageProbePassed);

        return sb.ToString();
    }

    private static void AppendWinRate(StringBuilder sb, IReadOnlyList<MatchRecord> matches)
    {
        sb.AppendLine("## Win rate");
        sb.AppendLine();
        sb.AppendLine("| Arm | Split | Opponent | Wins | Matches | Win rate |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var group in matches.GroupBy(m => (m.Arm, m.Split, m.Opponent)).OrderBy(g => g.Key))
        {
            int wins = group.Count(m => m.Winner == 0);
            int total = group.Count();
            sb.AppendLine($"| {group.Key.Arm} | {group.Key.Split} | {group.Key.Opponent} | {wins} | {total} | {Rate(wins, total)} |");
        }
        sb.AppendLine();
    }

    private static void AppendInvalidPlans(StringBuilder sb, IReadOnlyList<MatchRecord> matches)
    {
        sb.AppendLine("## Invalid plans");
        sb.AppendLine();
        foreach (var group in matches.GroupBy(m => m.Arm).OrderBy(g => g.Key))
        {
            int proposals = group.Sum(m => m.Players["arm"].Proposals);
            int rejected = group.Sum(m => m.Players["arm"].Rejected);
            sb.AppendLine($"- **{group.Key}**: {rejected} / {proposals} rejected proposals ({Rate(rejected, proposals)}); {group.Sum(m => m.Players["arm"].InvalidCommands)} invalid sim commands.");
        }
        sb.AppendLine();
    }

    private static void AppendLateness(StringBuilder sb, IReadOnlyList<MatchRecord> matches)
    {
        sb.AppendLine("## Decision lateness");
        sb.AppendLine();
        foreach (var group in matches.GroupBy(m => m.Arm).OrderBy(g => g.Key))
        {
            List<double> late = [.. group.SelectMany(m => m.Players["arm"].LateSeconds)];
            double avg = late.Count == 0 ? 0 : late.Average();
            int proposals = group.Sum(m => m.Players["arm"].Proposals);
            sb.AppendLine($"- **{group.Key}**: avg {avg.ToString("0.00", CultureInfo.InvariantCulture)}s late-validate; late-discard {Rate(late.Count, proposals)}.");
        }
        sb.AppendLine();
    }

    private static void AppendChurn(StringBuilder sb, IReadOnlyList<MatchRecord> matches)
    {
        sb.AppendLine("## Strategic churn (per 10 game minutes)");
        sb.AppendLine();
        foreach (var group in matches.GroupBy(m => m.Arm).OrderBy(g => g.Key))
        {
            double minutes = Math.Max(1.0 / 60, group.Sum(m => m.DurationSeconds) / 60.0);
            double activationsPer10 = group.Sum(m => m.Players["arm"].Activations) / minutes * 10;
            double flipsPer10 = group.Sum(m => m.Players["arm"].PostureFlips) / minutes * 10;
            sb.AppendLine($"- **{group.Key}**: {activationsPer10.ToString("0.00", CultureInfo.InvariantCulture)} activations, {flipsPer10.ToString("0.00", CultureInfo.InvariantCulture)} posture flips.");
        }
        sb.AppendLine();
    }

    private static void AppendIdleAndFloat(StringBuilder sb, IReadOnlyList<MatchRecord> matches)
    {
        sb.AppendLine("## Production idle time and resource float");
        sb.AppendLine();
        sb.AppendLine("| Arm | Idle fraction | Avg credits on hand |");
        sb.AppendLine("|---|---|---|");
        foreach (var group in matches.GroupBy(m => m.Arm).OrderBy(g => g.Key))
        {
            double idle = group.Average(m => m.Players["arm"].ProductionIdleFraction);
            double credits = group.Average(m => m.Players["arm"].AverageCreditsOnHand);
            sb.AppendLine($"| {group.Key} | {idle.ToString("0.000", CultureInfo.InvariantCulture)} | {credits.ToString("0", CultureInfo.InvariantCulture)} |");
        }
        sb.AppendLine();
    }

    private static void AppendTradeEfficiency(StringBuilder sb, IReadOnlyList<MatchRecord> matches)
    {
        sb.AppendLine("## Trade efficiency (enemy value destroyed / own value lost)");
        sb.AppendLine();
        foreach (var group in matches.GroupBy(m => m.Arm).OrderBy(g => g.Key))
        {
            double avg = group.Average(m => m.Players["arm"].TradeEfficiency);
            sb.AppendLine($"- **{group.Key}**: {avg.ToString("0.00", CultureInfo.InvariantCulture)}");
        }
        sb.AppendLine();
    }

    private static void AppendInferenceCost(StringBuilder sb, IReadOnlyList<MatchRecord> matches)
    {
        sb.AppendLine("## Inference cost");
        sb.AppendLine();
        foreach (var group in matches.GroupBy(m => m.Arm).OrderBy(g => g.Key))
        {
            long tokensIn = group.Sum(m => m.Players["arm"].TokensIn);
            long tokensOut = group.Sum(m => m.Players["arm"].TokensOut);
            double usd = group.Sum(m => m.Players["arm"].Usd);
            string? model = group.Select(m => m.Players["arm"].Model).FirstOrDefault(m => m is not null);
            sb.AppendLine($"- **{group.Key}** ({model ?? "n/a"}): {tokensIn} in / {tokensOut} out tokens, ${usd.ToString("0.0000", CultureInfo.InvariantCulture)}.");
        }
        sb.AppendLine();
    }

    private static void AppendLeakage(StringBuilder sb, IReadOnlyList<MatchRecord> matches, bool leakageProbePassed)
    {
        sb.AppendLine("## Hidden-information leakage");
        sb.AppendLine();
        _ = matches;
        sb.AppendLine("- Validator `fog.*` rejections: 0 (the validator is not wired into the arena yet; it belongs to package C).");
        sb.AppendLine($"- Arena leakage probe (`SimLeakageProbe.PerturbHidden`, belief-mode frame diff): {(leakageProbePassed ? "PASS — 0 differences" : "FAIL — observation changed")}.");
        sb.AppendLine();
    }

    private static string Rate(int numerator, int denominator) =>
        denominator == 0 ? "n/a" : (numerator / (double)denominator).ToString("0.000", CultureInfo.InvariantCulture);
}
