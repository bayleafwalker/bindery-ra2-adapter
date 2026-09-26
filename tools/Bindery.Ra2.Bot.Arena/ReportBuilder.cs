// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Bindery.Ra2.Bot.Arena;

/// <summary>
/// Turns a completed run into <c>report.md</c>, using the metric definitions in
/// <c>docs/architecture/strategic-bot.md</c>. Every figure comes from the match
/// records (simulator state and the bots' decision logs); every rate carries its
/// numerator and denominator.
/// </summary>
public static class ReportBuilder
{
    public static string Build(IReadOnlyList<MatchRecord> matches, IReadOnlyList<LeakageProbeResult> probes, IReadOnlyList<SkippedArm> skipped, CliOptions options, string rulesetId)
    {
        ArgumentNullException.ThrowIfNull(matches);
        StringBuilder sb = new();
        sb.AppendLine("# Bindery region sim arena report");
        sb.AppendLine();
        sb.AppendLine($"Results are from the bindery region simulator with the approximate `{rulesetId}` rules, not retail RA2; they are directional.");
        sb.AppendLine($"The arm is a full `BotRuntime`. Opponents named `ai-*` are the independent scripted AI (`Bindery.Ra2.Bot.Sim.Opponents`, no shared planner code; `:easy`/`:medium`/`:hard`, default hard); the other opponents are pinned-playbook styles running a frozen copy of the bot's stack as of commit 7f3e2c7 (`Bindery.Ra2.Bot.Baseline`), a stationary benchmark; `live-<style>` runs a pinned style on the live stack. The arm plays Allied on odd seeds and Soviet on even seeds. Match limit {F(options.MaxSeconds, "0")} s (a timeout is won on final asset value).");
        // Disclosed because the held-out split holds out maps only: these same opponents chose the selector's
        // default playbook, so a selector (or distilled) win rate against them is partly in-sample.
        sb.AppendLine("Opponents are not held out: the selector's default playbook was chosen from a style-versus-style matrix against these same pinned-playbook opponents (training maps only), so selector and distilled-arm win rates against them are partly in-sample. The held-out split holds out maps, not opponents.");
        sb.AppendLine($"Benchmark: {options.Benchmark}.");
        List<string> labels = matches.SelectMany(static m => m.Players["arm"].Labels).Distinct(StringComparer.Ordinal).OrderBy(static l => l, StringComparer.Ordinal).ToList();
        if (labels.Count > 0) sb.AppendLine($"Labels in this run: {string.Join(", ", labels.Select(static l => $"`{l}`"))}.");
        sb.AppendLine();
        sb.AppendLine($"Matches: {matches.Count}");
        sb.AppendLine();

        AppendSkipped(sb, skipped);
        AppendWinRate(sb, matches);
        AppendSaturation(sb, matches, options.Baseline);
        AppendPerOpponent(sb, matches);
        PairedReport.Append(sb, matches, options.Baseline, $"## Paired differences vs {options.Baseline}",
            baselineFor: arm => arm == options.Baseline || arm.EndsWith(CliOptions.OracleSuffix, StringComparison.Ordinal) ? null : options.Baseline);
        // The perception-bottleneck diagnostic: each arm against its own oracle twin on the same jobs.
        HashSet<string> armNames = [.. matches.Select(static m => m.Arm)];
        PairedReport.Append(sb, matches, options.Baseline, "## Perception bottleneck (belief − oracle)",
            baselineFor: arm => !arm.EndsWith(CliOptions.OracleSuffix, StringComparison.Ordinal) && armNames.Contains(arm + CliOptions.OracleSuffix) ? arm + CliOptions.OracleSuffix : null);
        AppendGame(sb, matches);
        AppendStrategy(sb, matches);
        AppendCommands(sb, matches);
        AppendInferenceCost(sb, matches);
        AppendDistillation(sb, matches);
        AppendTiers(sb, matches, options);
        AppendLeakage(sb, matches, probes);
        return sb.ToString();
    }

    private static void AppendSkipped(StringBuilder sb, IReadOnlyList<SkippedArm> skipped)
    {
        if (skipped.Count == 0) return;
        sb.AppendLine("## Skipped arms");
        sb.AppendLine();
        foreach (SkippedArm s in skipped) sb.AppendLine($"- **{s.Arm}**: {s.Reason}");
        sb.AppendLine();
    }

    private static void AppendWinRate(StringBuilder sb, IReadOnlyList<MatchRecord> matches)
    {
        sb.AppendLine("## Win rate (arm × split)");
        sb.AppendLine();
        sb.AppendLine("| Arm | Split | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) | Eliminations won | Timeouts |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var group in matches.GroupBy(m => (m.Arm, m.Split)).OrderBy(g => g.Key.Arm, StringComparer.Ordinal).ThenBy(g => g.Key.Split, StringComparer.Ordinal))
        {
            int wins = group.Count(static m => m.Winner == 0);
            int losses = group.Count(static m => m.Winner == 1);
            int draws = group.Count(static m => m.Winner is null);
            int total = group.Count();
            int elimWins = group.Count(static m => m.Winner == 0 && m.Reason == "elimination");
            int timeouts = group.Count(static m => m.Reason == "timeout");
            sb.AppendLine($"| {group.Key.Arm} | {group.Key.Split} | {wins} | {losses} | {draws} | {total} | {Rate(wins, total)} | {Wilson(wins, total)} | {elimWins} | {timeouts} |");
        }
        sb.AppendLine();
    }

    /// <summary>
    /// A benchmark the baseline wins (or loses) almost always cannot rank other arms: every arm then scores the same.
    /// The report says so when the baseline's win rate is outside 30–70%.
    /// </summary>
    private static void AppendSaturation(StringBuilder sb, IReadOnlyList<MatchRecord> matches, string baseline)
    {
        List<MatchRecord> own = [.. matches.Where(m => m.Arm == baseline)];
        if (own.Count == 0) return;
        double score = own.Average(static m => m.Winner switch { 0 => 1.0, null => 0.5, _ => 0.0 });
        bool informative = score is >= 0.3 and <= 0.7;
        sb.AppendLine(informative
            ? $"Benchmark check: the baseline `{baseline}` scored {F(score, "0.000")} over {own.Count} matches, inside the 30–70% band, so win-rate differences between arms can show."
            : $"Benchmark check: the baseline `{baseline}` scored {F(score, "0.000")} over {own.Count} matches, outside the 30–70% band: the benchmark is saturated and win-rate comparisons carry little information (use `--benchmark contested`).");
        sb.AppendLine();
    }

    private static void AppendPerOpponent(StringBuilder sb, IReadOnlyList<MatchRecord> matches)
    {
        sb.AppendLine("## Win rate by opponent style");
        sb.AppendLine();
        List<string> opponents = matches.Select(static m => m.Opponent).Distinct(StringComparer.Ordinal).OrderBy(static o => o, StringComparer.Ordinal).ToList();
        sb.AppendLine($"| Arm | Split | {string.Join(" | ", opponents)} |");
        sb.AppendLine($"|---|---|{string.Concat(opponents.Select(static _ => "---|"))}");
        foreach (var group in matches.GroupBy(m => (m.Arm, m.Split)).OrderBy(g => g.Key.Arm, StringComparer.Ordinal).ThenBy(g => g.Key.Split, StringComparer.Ordinal))
        {
            IEnumerable<string> cells = opponents.Select(o =>
            {
                List<MatchRecord> vs = group.Where(m => m.Opponent == o).ToList();
                return vs.Count == 0 ? "–" : $"{vs.Count(static m => m.Winner == 0)}/{vs.Count}";
            });
            sb.AppendLine($"| {group.Key.Arm} | {group.Key.Split} | {string.Join(" | ", cells)} |");
        }
        sb.AppendLine();
    }

    private static void AppendGame(StringBuilder sb, IReadOnlyList<MatchRecord> matches)
    {
        sb.AppendLine("## Economy, production and combat (arm side, per-match averages)");
        sb.AppendLine();
        sb.AppendLine("Production idle: seconds with a factory, nothing queued and credits for the cheapest item, over match seconds. Trade efficiency: enemy value destroyed in combat / own value lost in combat (pooled over matches).");
        sb.AppendLine();
        sb.AppendLine("Time to first attack: first second a combat unit stood in a region holding an enemy structure (mean over matches where it happened; count in brackets).");
        sb.AppendLine();
        sb.AppendLine("| Arm | Duration s | Units built | Buildings built | Peak army value | Idle fraction | Avg credits | Final assets (arm / opp) | Trade efficiency | First attack s |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var group in matches.GroupBy(static m => m.Arm).OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            List<PlayerMatchMetrics> arm = group.Select(static m => m.Players["arm"]).ToList();
            List<PlayerMatchMetrics> opp = group.Select(static m => m.Players["opponent"]).ToList();
            long destroyed = arm.Sum(static a => (long)a.AssetValueDestroyedByOpponent);
            long lost = arm.Sum(static a => (long)a.AssetValueLostByPlayer);
            sb.AppendLine($"| {group.Key} | {F(group.Average(static m => m.DurationSeconds), "0")} | {F(arm.Average(static a => a.UnitsBuilt), "0.0")} | {F(arm.Average(static a => a.BuildingsBuilt), "0.0")} | {F(arm.Average(static a => a.PeakArmyValue), "0")} | {F(arm.Average(static a => a.ProductionIdleFraction), "0.000")} | {F(arm.Average(static a => a.AverageCreditsOnHand), "0")} | {F(arm.Average(static a => a.FinalAssetValue), "0")} / {F(opp.Average(static a => a.FinalAssetValue), "0")} | {(lost == 0 ? "n/a" : F(destroyed / (double)lost, "0.00"))} ({destroyed}/{lost}) | {FirstAttack(arm)} |");
        }
        sb.AppendLine();
    }

    private static void AppendStrategy(StringBuilder sb, IReadOnlyList<MatchRecord> matches)
    {
        sb.AppendLine("## Strategy layer (arm side)");
        sb.AppendLine();
        sb.AppendLine("Invalid plans: rejected / proposals. Lateness: seconds from a proposal's snapshot to its validation; late-discard count / proposals. Churn: activations and posture flips per 10 game minutes.");
        sb.AppendLine();
        sb.AppendLine("Shadow agreement: shadow proposals naming the same playbook as the primary proposal for the same request / shadow proposals whose request got a primary proposal.");
        sb.AppendLine();
        sb.AppendLine("| Arm | Proposals | Invalid plans | Mean lateness s | Late-discarded | Failed requests | Shadow proposals | Shadow agreement | Activations /10 min | Posture flips /10 min |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var group in matches.GroupBy(static m => m.Arm).OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            List<PlayerMatchMetrics> arm = group.Select(static m => m.Players["arm"]).ToList();
            int proposals = arm.Sum(static a => a.Proposals);
            int rejected = arm.Sum(static a => a.Rejected);
            int late = arm.Sum(static a => a.LateDiscarded);
            List<double> lateness = arm.SelectMany(static a => a.LateSeconds).ToList();
            double minutes = Math.Max(1.0 / 60, group.Sum(static m => m.DurationSeconds) / 60.0);
            sb.AppendLine($"| {group.Key} | {proposals} | {rejected}/{proposals} ({Rate(rejected, proposals)}) | {(lateness.Count == 0 ? "n/a" : F(lateness.Average(), "0.00"))} | {late}/{proposals} ({Rate(late, proposals)}) | {arm.Sum(static a => a.ProposalsFailed)} | {arm.Sum(static a => a.ShadowProposals)} | {Agreement(arm)} | {F(arm.Sum(static a => a.Activations) / minutes * 10, "0.00")} | {F(arm.Sum(static a => a.PostureFlips) / minutes * 10, "0.00")} |");
        }
        sb.AppendLine();
    }

    private static void AppendCommands(StringBuilder sb, IReadOnlyList<MatchRecord> matches)
    {
        sb.AppendLine("## Command gate and simulator rejections (arm side, totals)");
        sb.AppendLine();
        foreach (var group in matches.GroupBy(static m => m.Arm).OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            List<PlayerMatchMetrics> arm = group.Select(static m => m.Players["arm"]).ToList();
            Dictionary<string, int> reasons = new(StringComparer.Ordinal);
            foreach (PlayerMatchMetrics a in arm)
            {
                foreach ((string reason, int count) in a.DroppedByReason) reasons[reason] = reasons.GetValueOrDefault(reason) + count;
            }
            string breakdown = reasons.Count == 0 ? "none" : string.Join(", ", reasons.OrderBy(static r => r.Key, StringComparer.Ordinal).Select(static r => $"{r.Key}: {r.Value}"));
            sb.AppendLine($"- **{group.Key}**: {arm.Sum(static a => a.CommandsDropped)} dropped by the gate ({breakdown}); {arm.Sum(static a => a.InvalidCommands)} commands rejected by the simulator.");
        }
        sb.AppendLine();
    }

    private static void AppendInferenceCost(StringBuilder sb, IReadOnlyList<MatchRecord> matches)
    {
        sb.AppendLine("## Inference cost (arm side)");
        sb.AppendLine();
        foreach (var group in matches.GroupBy(static m => m.Arm).OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            List<PlayerMatchMetrics> arm = group.Select(static m => m.Players["arm"]).ToList();
            long tokensIn = arm.Sum(static a => a.TokensIn);
            long tokensOut = arm.Sum(static a => a.TokensOut);
            double usd = arm.Sum(static a => a.Usd);
            string? model = arm.Select(static a => a.Model).FirstOrDefault(static m => m is not null);
            bool fake = arm.Any(static a => a.Labels.Contains("llm-fake"));
            int n = Math.Max(1, arm.Count);
            string note = fake ? " (fake client: tokens estimated from prompt size, priced at the list rate; not a measurement)" : string.Empty;
            sb.AppendLine($"- **{group.Key}** ({model ?? "no model"}): {tokensIn / n} in / {tokensOut / n} out tokens and ${F(usd / n, "0.0000")} per match{note}.");
        }
        sb.AppendLine();
    }

    /// <summary>
    /// Step 7's trade-off: how often the distilled model had to ask the LLM, next to what a match cost, and what a
    /// match of its teacher (the <c>llm</c> arm with the same qualifiers) cost in the same run.
    /// </summary>
    private static void AppendDistillation(StringBuilder sb, IReadOnlyList<MatchRecord> matches)
    {
        List<IGrouping<string, MatchRecord>> distilled = [.. matches
            .Where(static m => m.Arm.StartsWith("distilled", StringComparison.Ordinal))
            .GroupBy(static m => m.Arm)
            .OrderBy(static g => g.Key, StringComparer.Ordinal)];
        if (distilled.Count == 0) return;
        sb.AppendLine("## Distillation");
        sb.AppendLine();
        sb.AppendLine("Decisions: primary requests the distilled strategist answered. Escalations: those it handed to the LLM (out of distribution, low confidence, or no trained playbook for the faction). Cost per match includes the escalations' tokens; the teacher column is the matching `llm` arm in this run.");
        sb.AppendLine();
        sb.AppendLine("| Arm | Decisions | Escalations (rate) | USD per match | Teacher USD per match |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (IGrouping<string, MatchRecord> group in distilled)
        {
            List<PlayerMatchMetrics> arm = [.. group.Select(static m => m.Players["arm"])];
            int decisions = arm.Sum(static a => a.DistilledDecisions);
            int escalations = arm.Sum(static a => a.DistilledEscalations);
            string teacherArm = "llm" + group.Key["distilled".Length..];
            List<PlayerMatchMetrics> teacher = [.. matches.Where(m => m.Arm == teacherArm).Select(static m => m.Players["arm"])];
            string teacherCost = teacher.Count == 0 ? "n/a (not in this run)" : $"${F(teacher.Average(static t => t.Usd), "0.0000")}";
            sb.AppendLine($"| {group.Key} | {decisions} | {escalations}/{decisions} ({Rate(escalations, decisions)}) | ${F(arm.Average(static a => a.Usd), "0.0000")} | {teacherCost} |");
        }
        sb.AppendLine();
    }

    /// <summary>Build step 6: each tier arm against the tier below it, pair by pair, and the adoption rule's verdict.</summary>
    private static void AppendTiers(StringBuilder sb, IReadOnlyList<MatchRecord> matches, CliOptions options)
    {
        HashSet<string> present = [.. matches.Select(static m => m.Arm)];
        if (present.Count(BotAgentFactory.TierArms.ContainsKey) < 2) return;
        Dictionary<string, string> below = BotAgentFactory.TierArms
            .Where(p => p.Value != Claude.VocabularyTier.PlaybookOnly)
            .ToDictionary(p => p.Key, p => BotAgentFactory.TierArms.Single(q => q.Value == p.Value - 1).Key);
        PairedReport.Append(sb, matches, options.Baseline, "## Vocabulary tiers (build step 6)",
            baselineFor: arm => below.TryGetValue(arm, out string? lower) && present.Contains(lower) ? lower : null);
        Claude.VocabularyAdoption adoption = Program.TierAdoption(matches, live: !options.LlmFake, null);
        sb.AppendLine($"Adoption rule: {adoption.Rule}");
        sb.AppendLine();
        sb.AppendLine($"Adopted tier: {adoption.AdoptedTier}{(options.LlmFake ? " (fake client: these results measure the pipeline, never evidence for adoption)" : string.Empty)}.");
        foreach (string reason in adoption.Reasons) sb.AppendLine($"- {reason}");
        sb.AppendLine();
    }

    private static void AppendLeakage(StringBuilder sb, IReadOnlyList<MatchRecord> matches, IReadOnlyList<LeakageProbeResult> probes)
    {
        sb.AppendLine("## Hidden-information leakage");
        sb.AppendLine();
        foreach (var group in matches.GroupBy(static m => m.Arm).OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"- **{group.Key}**: {group.Sum(static m => m.Players["arm"].FogRejections)} validator `fog.*` rejections.");
        }
        sb.AppendLine();
        sb.AppendLine("Probe: two lockstep simulations, hidden state of one perturbed (`SimLeakageProbe`), strategist-context hash compared on every following frame.");
        sb.AppendLine();
        sb.AppendLine("| Arm | Map | Seed | Perturbed at s | Lockstep before | Differing frames | Note |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (LeakageProbeResult p in probes)
        {
            sb.AppendLine($"| {p.Arm} | {p.Map} | {p.Seed} | {F(p.PerturbedAtSeconds, "0")} | {(p.StatesMatchedBeforePerturbation ? "yes" : "no")} | {p.Differences}/{p.FramesCompared} | {p.Note ?? string.Empty} |");
        }
        sb.AppendLine();
    }

    private static string Agreement(List<PlayerMatchMetrics> arm)
    {
        int compared = arm.Sum(static a => a.ShadowCompared);
        return compared == 0 ? "n/a" : $"{arm.Sum(static a => a.ShadowAgreed)}/{compared} ({Rate(arm.Sum(static a => a.ShadowAgreed), compared)})";
    }

    private static string FirstAttack(List<PlayerMatchMetrics> arm)
    {
        List<double> times = [.. arm.Where(static a => a.FirstAttackSeconds is not null).Select(static a => a.FirstAttackSeconds!.Value)];
        return times.Count == 0 ? "never" : $"{F(times.Average(), "0")} ({times.Count}/{arm.Count})";
    }

    /// <summary>Wilson score interval at 95% for a binomial proportion, printed as [low, high].</summary>
    public static string Wilson(int successes, int total)
    {
        if (total == 0) return "n/a";
        const double z = 1.959964;
        double p = successes / (double)total;
        double denominator = 1 + z * z / total;
        double centre = (p + z * z / (2 * total)) / denominator;
        double half = z * Math.Sqrt(p * (1 - p) / total + z * z / (4.0 * total * total)) / denominator;
        return $"[{F(Math.Max(0, centre - half), "0.000")}, {F(Math.Min(1, centre + half), "0.000")}]";
    }

    private static string F(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    private static string Rate(int numerator, int denominator) =>
        denominator == 0 ? "n/a" : (numerator / (double)denominator).ToString("0.000", CultureInfo.InvariantCulture);
}
