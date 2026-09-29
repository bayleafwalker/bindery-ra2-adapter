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
    public static string Build(IReadOnlyList<MatchRecord> matches, IReadOnlyList<LeakageProbeResult> probes, IReadOnlyList<SkippedArm> skipped, CliOptions options, string rulesetId, IReadOnlyList<Bindery.Ra2.Bot.Playbooks.RosterChange>? rosterChanges = null)
    {
        ArgumentNullException.ThrowIfNull(matches);
        StringBuilder sb = new();
        sb.AppendLine("# Bindery region sim arena report");
        sb.AppendLine();
        sb.AppendLine($"Results are from the bindery region simulator with the approximate `{rulesetId}` rules, not retail RA2; they are directional.");
        sb.AppendLine($"The arm is a full `BotRuntime`. Opponents named `ai-*` are the independent scripted AI (`Bindery.Ra2.Bot.Sim.Opponents`, no shared planner code; `:easy`/`:medium`/`:hard`, default hard); the other opponents are pinned-playbook styles running a frozen copy of the bot's stack as of commit 7f3e2c7 (`Bindery.Ra2.Bot.Baseline`), a stationary benchmark; `live-<style>` runs a pinned style on the live stack. The arm plays Allied on odd seeds and Soviet on even seeds. Match limit {F(options.MaxSeconds, "0")} s (a timeout is won on final asset value).");
        // Disclosed because only the held-out opponents are out of sample: the pinned styles chose the selector's
        // default playbook, so a win rate against them is partly in-sample.
        sb.AppendLine($"Opponents are split as well as maps. Held-out opponents ({string.Join(", ", OpponentSets.HeldOut.Select(static o => $"`{o}`"))}, independent scripted styles) are never used for the selector's default playbook, bandit learning, distillation datasets or tuning; see \"Held-out opponents\" below. They are not untouched: their difficulty and the contested benchmark's handicaps were calibrated against the selector's win rate on all five maps, held-out maps included, so the selector's own held-out win rate is near one half partly by construction. Every other opponent is a training opponent: the selector's default playbook was chosen from a style-versus-style matrix against the pinned-playbook styles (training maps only), so win rates against training opponents are partly in-sample.");
        sb.AppendLine($"Benchmark: {options.Benchmark}.");
        List<string> labels = matches.SelectMany(static m => m.Players["arm"].Labels).Distinct(StringComparer.Ordinal).OrderBy(static l => l, StringComparer.Ordinal).ToList();
        if (labels.Count > 0) sb.AppendLine($"Labels in this run: {string.Join(", ", labels.Select(static l => $"`{l}`"))}.");
        sb.AppendLine();
        sb.AppendLine($"Matches: {matches.Count}");
        sb.AppendLine();

        AppendSkipped(sb, skipped);
        AppendRoster(sb, options, rulesetId, rosterChanges ?? []);
        AppendWinRate(sb, matches, options.MaxLlmFailureRate);
        AppendSaturation(sb, matches, options.Baseline);
        AppendPerOpponent(sb, matches);
        AppendHeldOutOpponents(sb, matches, options.Baseline);
        PairedReport.Append(sb, matches, options.Baseline, $"## Paired differences vs {options.Baseline}",
            baselineFor: arm => arm == options.Baseline || IsOracle(arm) ? null : options.Baseline);
        // The perception-bottleneck diagnostic: each arm against its own oracle twin on the same jobs.
        HashSet<string> armNames = [.. matches.Select(static m => m.Arm)];
        PairedReport.Append(sb, matches, options.Baseline, "## Perception bottleneck (belief − oracle)",
            baselineFor: arm => !IsOracle(arm) && armNames.Contains(OracleTwin(arm)) ? OracleTwin(arm) : null);
        AppendGame(sb, matches);
        AppendStrategy(sb, matches);
        AppendCommands(sb, matches);
        AppendInferenceCost(sb, matches);
        AppendDistillation(sb, matches);
        AppendTiers(sb, matches, options);
        AppendStyles(sb, matches);
        AppendLeakage(sb, matches, probes);
        return sb.ToString();
    }

    /// <summary>
    /// Under <c>--rules</c>, how the authored playbooks were fitted to the roster: each replaced tech goal and each
    /// dropped playbook, with the rule facts behind it.
    /// </summary>
    private static void AppendRoster(StringBuilder sb, CliOptions options, string rulesetId, IReadOnlyList<Bindery.Ra2.Bot.Playbooks.RosterChange> changes)
    {
        if (options.RulesPath is null) return;
        sb.AppendLine("## Roster adaptation");
        sb.AppendLine();
        sb.AppendLine($"Rules: `{rulesetId}` from `{Path.GetFileName(options.RulesPath)}`. The playbooks were authored against `bindery-sim-approx`; composition targets name roles and carry over unchanged, and each tech goal the roster does not define is replaced by the same-role unit of nearest cost that every playbook faction can reach, or the playbook is dropped. The scripted `ai-*` opponents fit their build lists the same way; the frozen pinned styles run unadapted (their playbooks fail validation where a type is missing and fall back to the frozen selector).");
        sb.AppendLine();
        if (changes.Count == 0) sb.AppendLine("No playbook needed a change.");
        foreach (Bindery.Ra2.Bot.Playbooks.RosterChange c in changes)
        {
            sb.AppendLine(c.Replacement is null
                ? $"- {c.PlaybookId}: {c.Reason}"
                : $"- {c.PlaybookId}: {c.TechGoal} → {c.Replacement} ({c.Reason})");
        }
        sb.AppendLine();
    }

    private static void AppendSkipped(StringBuilder sb, IReadOnlyList<SkippedArm> skipped)
    {
        if (skipped.Count == 0) return;
        sb.AppendLine("## Skipped arms");
        sb.AppendLine();
        foreach (SkippedArm s in skipped) sb.AppendLine($"- **{s.Arm}**: {s.Reason}");
        sb.AppendLine();
    }

    /// <summary>
    /// Win rate by opponent split and map split, then the paired tables restricted to held-out opponents: the only
    /// results in the report that are out of sample in opponent as well as in map.
    /// </summary>
    private static void AppendHeldOutOpponents(StringBuilder sb, IReadOnlyList<MatchRecord> matches, string baseline)
    {
        sb.AppendLine("## Held-out opponents");
        sb.AppendLine();
        List<MatchRecord> heldOut = [.. matches.Where(static m => OpponentSets.IsHeldOut(m.Opponent))];
        if (heldOut.Count == 0)
        {
            sb.AppendLine($"No held-out opponent in this run ({string.Join(", ", OpponentSets.HeldOut.Select(static o => $"`{o}`"))}; `--opponents heldout` adds them). Every win rate above is against training opponents.");
            sb.AppendLine();
            return;
        }
        sb.AppendLine("Opponent split × map split. Held-out opponents never inform the selector's defaults, bandit learning (the arena abandons the bandit's episode instead of crediting it, as it does on held-out maps), distillation datasets or teacher runs, or tuning (the tuner refuses them); their difficulty was calibrated against the selector on all maps, so read the selector's own held-out rate as partly calibrated and other arms' rates relative to it.");
        sb.AppendLine();
        sb.AppendLine("| Arm | Opponents | Maps | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var group in matches.GroupBy(static m => (m.Arm, Opponents: OpponentSets.SplitOf(m.Opponent), m.Split))
                     .OrderBy(static g => g.Key.Arm, StringComparer.Ordinal).ThenBy(static g => g.Key.Opponents, StringComparer.Ordinal).ThenBy(static g => g.Key.Split, StringComparer.Ordinal))
        {
            int wins = group.Count(static m => m.Winner == 0);
            int total = group.Count();
            sb.AppendLine($"| {group.Key.Arm} | {group.Key.Opponents} | {group.Key.Split} | {wins} | {group.Count(static m => m.Winner == 1)} | {group.Count(static m => m.Winner is null)} | {total} | {Rate(wins, total)} | {Wilson(wins, total)} |");
        }
        foreach (var group in heldOut.GroupBy(static m => m.Arm).OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            int wins = group.Count(static m => m.Winner == 0);
            int total = group.Count();
            sb.AppendLine($"| {group.Key} | heldout | all | {wins} | {group.Count(static m => m.Winner == 1)} | {group.Count(static m => m.Winner is null)} | {total} | {Rate(wins, total)} | {Wilson(wins, total)} |");
        }
        sb.AppendLine();
        PairedReport.Append(sb, heldOut, baseline, $"### Paired differences vs {baseline}, held-out opponents only",
            baselineFor: arm => arm == baseline || IsOracle(arm) ? null : baseline);
    }

    private static string Delivery(LlmCallTally? t) =>
        t is null ? "n/a" : $"{t.Answered} answered, {t.Failed} failed ({Rate(t.Failed, t.Answered + t.Failed)})";

    private static void AppendWinRate(StringBuilder sb, IReadOnlyList<MatchRecord> matches, double maxLlmFailureRate)
    {
        sb.AppendLine("## Win rate (arm × split)");
        sb.AppendLine();
        sb.AppendLine("Distinct games drop repeats of an identical game (same arm faction, decision log and outcome on the same map): against a differently named opponent whose style had not diverged when the match ended, or on another seed that changed nothing; the distinct interval is the one to read. The arm plays Allied on odd seeds and Soviet on even seeds, and starts west on seeds 1-2, 5-6, ... and east on 3-4, 7-8, ...; the fixture is asymmetric, so the faction and side columns show the mix behind each rate.");
        sb.AppendLine();
        sb.AppendLine("| Arm | Split | Wins | Losses | Draws | Matches | Win rate | 95% interval (Wilson) | Distinct games | Distinct wins | 95% interval, distinct | As Allied | As Soviet | As west | As east | Eliminations won | Timeouts | LLM delivery |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        IReadOnlyDictionary<(string Arm, string Split), LlmCallTally> unreliable = LlmCallTally.Unreliable(matches, maxLlmFailureRate);
        List<string> unbalanced = [];
        List<string> unevenSides = [];
        foreach (var group in matches.GroupBy(m => (m.Arm, m.Split)).OrderBy(g => g.Key.Arm, StringComparer.Ordinal).ThenBy(g => g.Key.Split, StringComparer.Ordinal))
        {
            int wins = group.Count(static m => m.Winner == 0);
            int losses = group.Count(static m => m.Winner == 1);
            int draws = group.Count(static m => m.Winner is null);
            int total = group.Count();
            int elimWins = group.Count(static m => m.Winner == 0 && m.Reason == "elimination");
            int timeouts = group.Count(static m => m.Reason == "timeout");
            IReadOnlyList<MatchRecord> distinct = PairedReport.DistinctGames(group);
            int distinctWins = distinct.Count(static m => m.Winner == 0);
            List<MatchRecord> allied = [.. group.Where(static m => MatchRunner.ArmFaction(m.Seed) == Faction.Allied)];
            List<MatchRecord> soviet = [.. group.Where(static m => MatchRunner.ArmFaction(m.Seed) == Faction.Soviet)];
            if (allied.Count != soviet.Count) unbalanced.Add($"{group.Key.Arm}/{group.Key.Split} ({allied.Count} Allied, {soviet.Count} Soviet)");
            List<MatchRecord> west = [.. group.Where(static m => !MatchRunner.ArmStartsEast(m.Seed))];
            List<MatchRecord> east = [.. group.Where(static m => MatchRunner.ArmStartsEast(m.Seed))];
            if (west.Count != east.Count) unevenSides.Add($"{group.Key.Arm}/{group.Key.Split} ({west.Count} west, {east.Count} east)");
            sb.AppendLine($"| {(unreliable.ContainsKey((group.Key.Arm, group.Key.Split)) ? group.Key.Arm + ' ' + "(not a model result)" : group.Key.Arm)} | {group.Key.Split} | {wins} | {losses} | {draws} | {total} | {Rate(wins, total)} | {Wilson(wins, total)} | {distinct.Count} | {distinctWins} | {Wilson(distinctWins, distinct.Count)} | {allied.Count(static m => m.Winner == 0)}/{allied.Count} | {soviet.Count(static m => m.Winner == 0)}/{soviet.Count} | {west.Count(static m => m.Winner == 0)}/{west.Count} | {east.Count(static m => m.Winner == 0)}/{east.Count} | {elimWins} | {timeouts} | {Delivery(LlmCallTally.Pool(group))} |");
        }
        sb.AppendLine();
        foreach (((string unreliableArm, string unreliableSplit), LlmCallTally tally) in unreliable.OrderBy(static p => p.Key.Arm, StringComparer.Ordinal).ThenBy(static p => p.Key.Split, StringComparer.Ordinal))
        {
            sb.AppendLine($"**{unreliableArm} on {unreliableSplit} maps: not a model result.** {tally.Failed} of {tally.Answered + tally.Failed} model calls failed ({Rate(tally.Failed, tally.Answered + tally.Failed)}), above the {F(maxLlmFailureRate, "0.##")} limit (`--max-llm-failure-rate`); the selector fallback played the gaps, so this arm's win rate is largely the selector's. Its matches are kept for inspection, and its tier evidence is refused for adoption.");
            sb.AppendLine();
        }
        if (unbalanced.Count > 0)
        {
            sb.AppendLine($"Warning: the faction mix is unbalanced for {string.Join(", ", unbalanced)} (an odd `--seeds` gives the arm Allied more often); a win rate over it mixes faction strength into the result. Use an even `--seeds`.");
            sb.AppendLine();
        }
        if (unevenSides.Count > 0)
        {
            sb.AppendLine($"Warning: the start-side mix is unbalanced for {string.Join(", ", unevenSides)}; a map-side advantage leaks into the rate. Use a multiple of 4 for `--seeds`.");
            sb.AppendLine();
        }
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

        // Opponents that played the same game as another on a map and seed: their columns above are not
        // independent evidence (their styles had not diverged when the match ended).
        List<string> identical = [];
        foreach (var arm in matches.GroupBy(static m => m.Arm).OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            Dictionary<string, int> cellsBySet = new(StringComparer.Ordinal);
            foreach (var game in arm.Where(static m => PairedReport.GameKey(m) is not null).GroupBy(static m => PairedReport.GameKey(m)!, StringComparer.Ordinal))
            {
                List<string> names = [.. game.Select(static m => m.Opponent).Distinct(StringComparer.Ordinal).OrderBy(static o => o, StringComparer.Ordinal)];
                if (names.Count < 2) continue;
                string set = string.Join(" = ", names);
                cellsBySet[set] = cellsBySet.GetValueOrDefault(set) + 1;
            }
            identical.AddRange(cellsBySet.OrderBy(static kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{arm.Key}: {kv.Key} on {kv.Value} map × seed cell{(kv.Value == 1 ? string.Empty : "s")}"));
        }
        if (identical.Count > 0)
        {
            sb.AppendLine("Identical games (same arm decision log and outcome against differently named opponents; counted once in the distinct columns and paired tables):");
            sb.AppendLine();
            foreach (string line in identical) sb.AppendLine($"- {line}");
            sb.AppendLine();
        }
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
        sb.AppendLine("Proposals, invalid plans and lateness are the arm's primary strategist's: rejected / proposals; seconds from a proposal's snapshot to its validation; late-discard count / proposals. Fallback and emergency proposals (immediate and always valid) are counted apart so they cannot flatter the strategist under test. Shadow columns are the shadow strategist's own lateness and would-be invalid plans. Churn: activations and posture flips per 10 game minutes.");
        sb.AppendLine();
        sb.AppendLine("Shadow agreement: shadow proposals naming the same playbook as the primary proposal for the same request / shadow proposals whose request got a primary proposal.");
        sb.AppendLine();
        sb.AppendLine("| Arm | Proposals | Invalid plans | Mean lateness s | Late-discarded | Fallback proposals | Failed requests | Shadow proposals | Shadow invalid | Shadow mean lateness s | Shadow agreement | Activations /10 min | Posture flips /10 min |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var group in matches.GroupBy(static m => m.Arm).OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            List<PlayerMatchMetrics> arm = group.Select(static m => m.Players["arm"]).ToList();
            int proposals = arm.Sum(static a => a.Proposals);
            int rejected = arm.Sum(static a => a.Rejected);
            int late = arm.Sum(static a => a.LateDiscarded);
            List<double> lateness = arm.SelectMany(static a => a.LateSeconds).ToList();
            double minutes = Math.Max(1.0 / 60, group.Sum(static m => m.DurationSeconds) / 60.0);
            int shadow = arm.Sum(static a => a.ShadowProposals), shadowRejected = arm.Sum(static a => a.ShadowRejected);
            List<double> shadowLateness = arm.SelectMany(static a => a.ShadowLateSeconds).ToList();
            string shadowInvalid = shadow == 0 ? "n/a" : $"{shadowRejected}/{shadow} ({Rate(shadowRejected, shadow)})";
            sb.AppendLine($"| {group.Key} | {proposals} | {rejected}/{proposals} ({Rate(rejected, proposals)}) | {(lateness.Count == 0 ? "n/a" : F(lateness.Average(), "0.00"))} | {late}/{proposals} ({Rate(late, proposals)}) | {arm.Sum(static a => a.FallbackProposals)} | {arm.Sum(static a => a.ProposalsFailed)} | {shadow} | {shadowInvalid} | {(shadowLateness.Count == 0 ? "n/a" : F(shadowLateness.Average(), "0.00"))} | {Agreement(arm)} | {F(arm.Sum(static a => a.Activations) / minutes * 10, "0.00")} | {F(arm.Sum(static a => a.PostureFlips) / minutes * 10, "0.00")} |");
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
            double failedUsd = arm.Sum(static a => a.FailedRequestUsd);
            string? model = arm.Select(static a => a.Model).FirstOrDefault(static m => m is not null);
            bool fake = arm.Any(static a => a.Labels.Contains("llm-fake"));
            int n = Math.Max(1, arm.Count);
            string note = fake ? " (fake client: tokens estimated from prompt size, priced at the list rate; not a measurement)" : string.Empty;
            int unpriced = arm.Sum(static a => a.UnpricedRequests);
            string unpricedNote = unpriced > 0 ? $"; {unpriced} requests on unpriced models are not in the cost" : string.Empty;
            SortedDictionary<string, int> servedBy = new(StringComparer.Ordinal);
            foreach ((string servingModel, int count) in arm.SelectMany(static a => a.ServedBy)) servedBy[servingModel] = servedBy.GetValueOrDefault(servingModel) + count;
            string served = servedBy.Count > 1 || (servedBy.Count == 1 && servedBy.Keys.First() != model)
                ? $"; served by {string.Join(", ", servedBy.Select(static kv => $"{kv.Key} x{kv.Value}"))}"
                : string.Empty;
            sb.AppendLine($"- **{group.Key}** ({model ?? "no model"}): {tokensIn / n} in / {tokensOut / n} out tokens and ${F(usd / n, "0.0000")} per match, of which ${F(failedUsd / n, "0.0000")} on failed requests{served}{unpricedNote}{note}.");
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

    /// <summary>
    /// Whether personalities are recognisable: per arm and style, where its time went (playbook and posture shares of
    /// active-intent time, pooled over matches) and when it first attacked; per pair of styles of the same arm, the
    /// Jensen–Shannon divergence (base 2: 0 same, 1 disjoint) of the two playbook and posture distributions and the
    /// difference in mean time to first attack.
    /// </summary>
    private static void AppendStyles(StringBuilder sb, IReadOnlyList<MatchRecord> matches)
    {
        List<IGrouping<string, MatchRecord>> styled = [.. matches.GroupBy(static m => m.Arm).OrderBy(static g => g.Key, StringComparer.Ordinal)];
        if (!styled.Any(static g => g.Key.Contains('@', StringComparison.Ordinal))) return;
        sb.AppendLine("## Play styles");
        sb.AppendLine();
        sb.AppendLine("Shares are of active-intent seconds pooled over the arm's matches (top three). First attack: mean seconds to the first combat unit in a region holding an enemy structure (matches where it happened / matches).");
        sb.AppendLine();
        sb.AppendLine("| Arm | Matches | Playbooks | Postures | First attack s |");
        sb.AppendLine("|---|---|---|---|---|");
        Dictionary<string, (Dictionary<string, double> Playbooks, Dictionary<string, double> Postures, double? FirstAttack)> profile = [];
        foreach (IGrouping<string, MatchRecord> group in styled)
        {
            List<PlayerMatchMetrics> arm = [.. group.Select(static m => m.Players["arm"])];
            Dictionary<string, double> playbooks = Pool(arm.Select(static a => a.PlaybookSeconds));
            Dictionary<string, double> postures = Pool(arm.Select(static a => a.PostureSeconds));
            List<double> attacks = [.. arm.Where(static a => a.FirstAttackSeconds is not null).Select(static a => a.FirstAttackSeconds!.Value)];
            profile[group.Key] = (playbooks, postures, attacks.Count == 0 ? null : attacks.Average());
            sb.AppendLine($"| {group.Key} | {arm.Count} | {Top(playbooks)} | {Top(postures)} | {FirstAttack(arm)} |");
        }
        sb.AppendLine();
        sb.AppendLine("| Arm | Style | Style | Playbook JSD | Posture JSD | First attack difference s |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (IGrouping<string, string> family in profile.Keys.GroupBy(static k => k.Split('@')[0], StringComparer.Ordinal).OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            List<string> members = [.. family.OrderBy(static k => k, StringComparer.Ordinal)];
            for (int i = 0; i < members.Count; i++)
            {
                for (int j = i + 1; j < members.Count; j++)
                {
                    var a = profile[members[i]];
                    var b = profile[members[j]];
                    string attack = a.FirstAttack is { } fa && b.FirstAttack is { } fb ? F(Math.Abs(fb - fa), "0") : "n/a";
                    sb.AppendLine($"| {family.Key} | {StyleOf(members[i])} | {StyleOf(members[j])} | {F(JensenShannon(a.Playbooks, b.Playbooks), "0.000")} | {F(JensenShannon(a.Postures, b.Postures), "0.000")} | {attack} |");
                }
            }
        }
        sb.AppendLine();
    }

    /// <summary>True for an oracle arm label (<c>x-oracle</c>, <c>x-oracle@style</c>).</summary>
    private static bool IsOracle(string arm) => arm.Split('@')[0].EndsWith(CliOptions.OracleSuffix, StringComparison.Ordinal);

    /// <summary>The oracle arm label for a belief arm label (<c>x@style</c> → <c>x-oracle@style</c>).</summary>
    private static string OracleTwin(string arm)
    {
        int at = arm.IndexOf('@', StringComparison.Ordinal);
        return at < 0 ? arm + CliOptions.OracleSuffix : arm[..at] + CliOptions.OracleSuffix + arm[at..];
    }

    private static string StyleOf(string arm) => arm.Contains('@', StringComparison.Ordinal) ? arm[(arm.IndexOf('@', StringComparison.Ordinal) + 1)..] : "none";

    private static Dictionary<string, double> Pool(IEnumerable<IReadOnlyDictionary<string, double>> parts)
    {
        Dictionary<string, double> total = new(StringComparer.Ordinal);
        foreach (IReadOnlyDictionary<string, double> part in parts)
        {
            foreach ((string key, double value) in part) total[key] = total.GetValueOrDefault(key) + value;
        }
        return total;
    }

    private static string Top(Dictionary<string, double> seconds)
    {
        double sum = seconds.Values.Sum();
        if (sum <= 0) return "n/a";
        return string.Join(", ", seconds.OrderByDescending(static p => p.Value).ThenBy(static p => p.Key, StringComparer.Ordinal).Take(3)
            .Select(p => $"{p.Key} {F(100 * p.Value / sum, "0")}%"));
    }

    /// <summary>Jensen–Shannon divergence in bits between two unnormalised distributions; 0 when either is empty.</summary>
    public static double JensenShannon(IReadOnlyDictionary<string, double> a, IReadOnlyDictionary<string, double> b)
    {
        double sa = a.Values.Sum(), sb = b.Values.Sum();
        if (sa <= 0 || sb <= 0) return 0;
        double divergence = 0;
        foreach (string key in a.Keys.Union(b.Keys, StringComparer.Ordinal))
        {
            double p = a.GetValueOrDefault(key) / sa, q = b.GetValueOrDefault(key) / sb, m = (p + q) / 2;
            if (p > 0) divergence += 0.5 * p * Math.Log2(p / m);
            if (q > 0) divergence += 0.5 * q * Math.Log2(q / m);
        }
        return Math.Clamp(divergence, 0, 1);
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
        Claude.VocabularyAdoption adoption = Program.TierAdoption(matches, live: !options.LlmFake, null, options.MaxLlmFailureRate);
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
            int shadowFog = group.Sum(static m => m.Players["arm"].ShadowFogRejections);
            sb.AppendLine($"- **{group.Key}**: {group.Sum(static m => m.Players["arm"].FogRejections)} validator `fog.*` rejections{(group.Any(static m => m.Players["arm"].ShadowProposals > 0) ? $"; shadow strategist: {shadowFog}" : string.Empty)}.");
        }
        sb.AppendLine();
        sb.AppendLine("Probe: two lockstep simulations, hidden state of one perturbed (`SimLeakageProbe`: enemy credits and queue, wounded hidden enemies, a hidden unit in an unseen region, one announced there through the event path, and one just across a border inside the arm's weapon reach), strategist-context hash compared on every following frame until the window closes or the objects the arm can see first differ. A differing frame is one where the context changed while everything visible was still identical. Fog-violation frames are arm frames, over the whole run of both simulations, that carried an enemy object or event from a region the arm did not see: a per-frame check that finds leaks the perturbation does not exercise.");
        sb.AppendLine();
        sb.AppendLine("| Arm | Map | Seed | Perturbed at s | Lockstep before | Compared s | Differing frames | Fog-violation frames | Note |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (LeakageProbeResult p in probes)
        {
            sb.AppendLine($"| {p.Arm} | {p.Map} | {p.Seed} | {F(p.PerturbedAtSeconds, "0")} | {(p.StatesMatchedBeforePerturbation ? "yes" : "no")} | {F(p.ComparedSeconds, "0")} | {p.Differences}/{p.FramesCompared} | {p.FogViolations} | {p.Note ?? string.Empty} |");
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
