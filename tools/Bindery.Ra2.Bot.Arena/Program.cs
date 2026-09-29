// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Bindery.Ra2.Bot.Analysis;
using Bindery.Ra2.Bot.Claude;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Runtime;
using Bindery.Ra2.Bot.Sim;
using Bindery.Ra2.Bot.Strategy;

namespace Bindery.Ra2.Bot.Arena;

/// <summary>An arm that did not run, and why.</summary>
public sealed record SkippedArm(string Arm, string Reason);

/// <summary>
/// `arena run` entry point: arms × maps (for the requested split) × opponents ×
/// seeds, every side a full <c>BotRuntime</c> on the bindery region sim (not
/// retail RA2). Writes <c>results.json</c>, <c>probes.json</c>, <c>report.md</c>
/// and one <c>dataset-&lt;arm&gt;.ndjson</c> of training-map decision examples per arm into <c>--out</c>.
/// </summary>
/// <remarks>
/// Order: the distilled arm needs a dataset, so when <c>--dataset</c> is absent it trains on the
/// <c>llm</c> arm's primary decisions on the training maps (the spec's "a dataset from <c>llm</c> runs"): the
/// run's own <c>llm</c> arm when it played them, else that arm's training-map matches run first, unreported,
/// whatever <c>--maps</c> says, and its label says which; it is never trained on the selector. With fewer than
/// <see cref="MinDistillExamples"/> examples the arm is skipped with the reason recorded. The bandit's
/// matches run sequentially in a fixed order because it learns across them; other arms run in
/// parallel. A live LLM arm runs its first match alone and is skipped with the recorded reason if no
/// credential resolved or if none of that match's Primary model proposals succeeded (see
/// <see cref="AllPrimaryProposalsFailed"/>); the first match is then not counted. With <c>--interleave</c> the arms that need nothing from another arm (not distilled, not a live
/// LLM arm) play their matches cell by cell (opponent, map, seed) across arms first, so paired comparisons fill in as the
/// run goes; a bandit arm keeps its order. With <c>--resume</c> matches already recorded under <c>&lt;out&gt;/decisions/</c>
/// (each match's record is written the moment it finishes) are loaded instead of replayed.
/// </remarks>
public static class Program
{
    /// <summary>Game times at which each arm's leakage probe perturbs hidden state.</summary>
    internal static readonly double[] LeakageProbeTimes = [90, 240];

    /// <summary>
    /// Whether a bandit match credits the learner: training maps against training opponents only, the same rule as
    /// the exported datasets, so the bandit's held-out-map and held-out-opponent results are out of sample.
    /// </summary>
    public static bool BanditLearnsFrom(string opponent, string split) =>
        split == "training" && !OpponentSets.IsHeldOut(opponent);

    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        try
        {
            if (args.Length > 0 && args[0] == "replay")
            {
                if (args.Length is not (2 or 4) || (args.Length == 4 && args[2] != "--out")) throw new ArgumentException("Usage: arena replay <out>/decisions/<match>.ndjson [--out <replayed.ndjson>]");
                ReplayResult result = ReplayMatch(args[1], args.Length == 4 ? args[3] : null);
                Console.WriteLine(result.Equal
                    ? $"replay: hash EQUAL ({result.ReplayedHash}), {result.ReplayedRecords} records"
                    : $"replay: hash DIFFERENT (recorded {result.RecordedHash}, replayed {result.ReplayedHash}); first differing record {result.FirstDifference}; {result.RecordedRecords} recorded, {result.ReplayedRecords} replayed records");
                Console.WriteLine($"requests without a recording: {result.Misses}; recorded answers never asked for: {result.Unused}");
                if (result.ManifestHash is not null && result.ManifestHash != result.RecordedHash)
                {
                    Console.WriteLine($"warning: the log file's hash {result.RecordedHash} differs from the hash recorded when the match was played ({result.ManifestHash}): the file was edited");
                }
                return result.Equal ? 0 : 2;
            }
            if (args.Length > 0 && args[0] == "playbooks")
            {
                if (args.Length is not (2 or 4) || args[1] != "export" || (args.Length == 4 && args[2] != "--out")) throw new ArgumentException("Usage: arena playbooks export [--out <playbooks.json>]");
                string json = ExportPlaybooksJson();
                if (args.Length == 4) File.WriteAllText(args[3], json);
                else Console.WriteLine(json);
                return 0;
            }
            if (args.Length > 0 && args[0] == "analyze")
            {
                if (args.Length < 2) throw new ArgumentException("Usage: arena analyze <out>/decisions/<match>.ndjson [--out <report.md>] [--narrate] [--llm-fake]");
                string? outPath = null;
                bool narrate = false, fake = false;
                for (int i = 2; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
                        case "--narrate": narrate = true; break;
                        case "--llm-fake": fake = true; break;
                        default: throw new ArgumentException($"Unknown argument '{args[i]}'.");
                    }
                }
                string analysis = Analyze(args[1], narrate, fake);
                if (outPath is null) Console.Write(analysis);
                else File.WriteAllText(outPath, analysis);
                return 0;
            }
            CliOptions options = CliOptions.Parse(args);
            Run(options);
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (AggregateException ex) when (ex.Flatten().InnerExceptions is [ArgumentException inner, ..])
        {
            Console.Error.WriteLine(inner.Message);
            return 1;
        }
    }

    private static void Run(CliOptions options)
    {
        Stopwatch wall = Stopwatch.StartNew();
        (IRulesDatabase rules, IPlaybookLibrary playbooks, IReadOnlyList<RosterChange> rosterChanges) = LoadRules(options.RulesPath, options.PlaybookFiles);
        ArenaRunContext context = new(options.LlmFake, options.LlmLatencySeconds, options.LlmEndpoint, options.LlmModel) { ArmKnobs = options.ArmKnobs, ExtendedMetrics = options.ExtendedMetrics };
        BotAgentFactory factory = new(rules, playbooks, context);
        foreach (ArmSpec arm in options.ArmSpecs())
        {
            if (!BotAgentFactory.IsArm(arm.Name)) throw new ArgumentException($"Unknown arm '{arm.Name}'. Arms: {string.Join(", ", BotAgentFactory.Arms.Concat(BotAgentFactory.TierArms.Keys).Concat(BotAgentFactory.ShadowTierArms.Keys))} (any with a -oracle suffix).");
        }
        foreach (string opponent in options.Opponents)
        {
            if (!BotAgentFactory.IsOpponent(opponent)) throw new ArgumentException($"Unknown opponent '{opponent}'.");
        }

        List<(SimMap Map, string Split)> maps = MapsForSplit(options.MapSplit);
        List<ArmSpec> arms = [.. options.ArmSpecs()];
        if (options.Dataset is not null)
        {
            using StreamReader reader = new(options.Dataset);
            context.DistillDataset = DecisionDataset.ReadNdjson(reader);
            context.DistillSource = Path.GetFileName(options.Dataset);
            // Oracle decisions saw the whole map: a fog-respecting distilled arm must not be trained on them (it
            // would drop them silently and report a dataset size it never used), so the mix is refused up front.
            if (context.DistillDataset.HasOracleExamples && arms.Any(static a => a.Name == "distilled" && !a.Oracle))
            {
                throw new ArgumentException(
                    $"Dataset '{options.Dataset}' holds oracle (full-map) decisions; the belief-mode distilled arm cannot use them. " +
                    "Use a dataset exported from a belief-mode arm, or run the distilled-oracle arm.");
            }
        }

        // Distilled last, after its teacher.
        arms = [.. arms.OrderBy(static a => a.Name == "distilled" ? 1 : 0)];
        (ArmSpec Teacher, bool Reported)? teacher = context.DistillDataset is null && arms.Any(static a => a.Name == "distilled")
            ? DistillTeacher(arms, options.LlmFake)
            : null;
        List<MatchRecord> results = [];
        List<SkippedArm> skipped = [];
        List<LeakageProbeResult> probes = [];
        Directory.CreateDirectory(options.OutDir);
        if (options.Resume) PreflightResume(arms, options, maps);
        if (options.Interleave)
        {
            // Arms that run in the per-arm loop below without needing anything from another arm; a live LLM arm (first
            // match alone, skip reason) and the distilled arm (needs its teacher's dataset) stay in the per-arm loop.
            List<ArmSpec> interleaved = [.. arms.Where(a => a.Name != "distilled" && !(a.UsesLlm && !options.LlmFake))];
            ArenaScheduling.RunInterleaved(interleaved, a => [.. Jobs(a, options, maps).Select(static j => new ArenaJob(j.Arm, j.Opponent, j.Map, j.Split, j.Seed))], context,
                (j, log) => RunJob((j.Arm, j.Opponent, j.Map, j.Split, j.Seed), options, rules, factory, log));
        }

        foreach (ArmSpec arm in arms)
        {
            if (arm.Name == "distilled" && context.DistillDataset is null && teacher is { } t)
            {
                if (context.LlmSkipReason is { } noTeacher)
                {
                    skipped.Add(new SkippedArm(arm.ToString(), $"no teacher: the llm arm could not run ({noTeacher})"));
                    continue;
                }
                // The teacher always plays SimMaps.Training, whatever --maps says: with --maps heldout the run has no
                // training-map matches to learn from, and an empty dataset would escalate every decision to the LLM.
                context.DistillDataset = TeacherDataset(t.Teacher, options, rules, factory);
                context.DistillSource = "llm arm run for this purpose (training maps, unreported)";
                if (context.LlmSkipReason is { } failed)
                {
                    skipped.Add(new SkippedArm(arm.ToString(), $"no teacher: the llm arm could not run ({failed})"));
                    continue;
                }
            }
            if (arm.Name == "distilled" && DistillSkipReason(context.DistillDataset ?? DecisionDataset.Empty, context.DistillSource ?? "none") is { } tooSmall)
            {
                skipped.Add(new SkippedArm(arm.ToString(), tooSmall));
                Console.WriteLine($"{arm}: {tooSmall}");
                continue;
            }

            List<(ArmSpec Arm, string Opponent, SimMap Map, string Split, int Seed)> jobs = Jobs(arm, options, maps);
            ConcurrentDictionary<int, IReadOnlyList<DecisionRecord>> logs = new();
            MatchRecord[] armResults = new MatchRecord[jobs.Count];
            int start = 0;
            if (arm.UsesLlm && !options.LlmFake)
            {
                armResults[0] = RunJob(jobs[0], options, rules, factory, log => logs[0] = log);
                start = 1;
                // An all-failed first match skips this arm only; a missing credential (context-wide) skips every LLM arm.
                string? armReason = context.LlmSkipReason;
                if (armReason is null && logs.TryGetValue(0, out IReadOnlyList<DecisionRecord>? firstLog))
                {
                    armReason = AllPrimaryProposalsFailed(firstLog, arm.ToString());
                }
                if (armReason is { } reason)
                {
                    skipped.Add(new SkippedArm(arm.ToString(), reason));
                    Console.WriteLine($"{arm}: {reason}");
                    continue;
                }
            }

            if (arm.Name == "bandit" || (arm.UsesLlm && !options.LlmFake))
            {
                for (int i = start; i < jobs.Count; i++)
                {
                    int index = i;
                    context.BanditLearning = BanditLearnsFrom(jobs[i].Opponent, jobs[i].Split);
                    armResults[i] = RunJob(jobs[i], options, rules, factory, log => logs[index] = log);
                }
            }
            else
            {
                Parallel.For(start, jobs.Count, i => armResults[i] = RunJob(jobs[i], options, rules, factory, log => logs[i] = log));
            }
            context.BanditLearning = true;
            results.AddRange(armResults);

            // Exported datasets hold training-map decisions against training opponents only, so a distilled arm
            // trained on them is never evaluated on maps or opponents its teacher's data came from.
            DecisionDataset dataset = DecisionDataset.Merge(Enumerable.Range(0, jobs.Count)
                .Where(i => logs.ContainsKey(i) && BanditLearnsFrom(jobs[i].Opponent, jobs[i].Split))
                .Select(i => DecisionDataset.FromDecisionLog(logs[i], DatasetFilter.PrimaryOnly, MatchId(jobs[i]), ModeOf(jobs[i].Arm))));
            using (StreamWriter writer = new(Path.Combine(options.OutDir, $"dataset-{Slug(arm.ToString())}.ndjson")))
            {
                dataset.WriteNdjson(writer);
            }
            // Only a run that played the training maps leaves the teacher's own dataset; otherwise the distilled arm
            // runs the teacher on the training maps itself (above).
            if (teacher is { Reported: true } reportedTeacher && arm == reportedTeacher.Teacher && context.DistillDataset is null && dataset.Count > 0)
            {
                context.DistillDataset = dataset;
                context.DistillSource = "llm arm in this run (training maps)";
            }

            // Two perturbation times: early, and after the armies have usually met, when leaks through fire and kill
            // events can show.
            LeakageProbeResult[] armProbes = [.. LeakageProbeTimes.Select(t => LeakageProbe.Run(arm, maps[0].Map, 1, rules, factory, perturbAtSeconds: t))];
            if (arm.Name == "bandit") context.BanditFor(arm).AbandonEpisode();
            probes.AddRange(armProbes);
            Console.WriteLine($"{arm}: {armResults.Length} matches, {armResults.Count(static r => r.Winner == 0)} wins; leakage probe {armProbes.Sum(static p => p.Differences)}/{armProbes.Sum(static p => p.FramesCompared)} differing frames, {armProbes.Sum(static p => p.FogViolations)} fog-violation frames.");
        }

        List<MatchRecord> ordered = [.. results.OrderBy(r => r.Arm, StringComparer.Ordinal)
                                                 .ThenBy(r => r.Opponent, StringComparer.Ordinal)
                                                 .ThenBy(r => r.Map, StringComparer.Ordinal)
                                                 .ThenBy(r => r.Seed)];

        JsonSerializerOptions indented = new(BotJson.Options) { WriteIndented = true };
        File.WriteAllText(Path.Combine(options.OutDir, "results.json"), JsonSerializer.Serialize(ordered, indented));
        File.WriteAllText(Path.Combine(options.OutDir, "probes.json"), JsonSerializer.Serialize(new { probes, skipped }, indented));
        File.WriteAllText(Path.Combine(options.OutDir, "report.md"), ReportBuilder.Build(ordered, probes, skipped, options, rules.RulesetId, rosterChanges));
        if (ordered.Count(static m => BotAgentFactory.TierArms.ContainsKey(m.Arm)) > 0)
        {
            string adoption = TierAdoption(ordered, live: !options.LlmFake, DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), options.MaxLlmFailureRate).ToJson() + "\n";
            File.WriteAllText(Path.Combine(options.OutDir, "vocabulary-adoption.json"), adoption);
            if (options.WriteAdoption is { } target) File.WriteAllText(target, adoption);
        }

        foreach (((string unreliableArm, string unreliableSplit), LlmCallTally tally) in LlmCallTally.Unreliable(ordered, options.MaxLlmFailureRate).OrderBy(static p => p.Key.Arm, StringComparer.Ordinal).ThenBy(static p => p.Key.Split, StringComparer.Ordinal))
        {
            Console.WriteLine(tally.Warning(unreliableArm, unreliableSplit, options.MaxLlmFailureRate));
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Wrote {ordered.Count} match results, {probes.Count} leakage probes and report.md to {options.OutDir} in {wall.Elapsed.TotalSeconds:0}s."));
    }

    /// <summary>Fails before anything runs when a recorded match under <c>--out</c> disagrees with its job, or a bandit arm is partly recorded.</summary>
    private static void PreflightResume(IReadOnlyList<ArmSpec> arms, CliOptions options, List<(SimMap Map, string Split)> maps)
    {
        foreach (ArmSpec arm in arms)
        {
            List<ArenaJob> jobs = [.. Jobs(arm, options, maps).Select(static j => new ArenaJob(j.Arm, j.Opponent, j.Map, j.Split, j.Seed))];
            foreach (ArenaJob job in jobs) ArenaScheduling.FindManifest(job, options);
            ArenaScheduling.CheckBanditResume(arm, jobs, options);
        }
    }

    /// <summary>
    /// The skip reason when a live LLM arm's first match logged Primary <c>strategy.proposal_failed</c> records and no
    /// Primary <c>strategy.proposal</c> (every decision then fell back to the selector, so the arm's results would not
    /// measure the model); null when at least one Primary proposal succeeded or none was attempted.
    /// </summary>
    public static string? AllPrimaryProposalsFailed(IReadOnlyList<DecisionRecord> log, string arm = "llm")
    {
        int failed = 0;
        string? firstError = null;
        string? claudeError = null;
        foreach (DecisionRecord record in log)
        {
            if (record.Data.ValueKind != JsonValueKind.Object) continue;
            // The strategist's own failure record (no role) carries the real error; the scheduler's Primary record
            // only says no_opinion.
            if (record.Kind == DecisionRecordKinds.ProposalFailed && !record.Data.TryGetProperty("role", out _))
            {
                claudeError ??= ReadText(record.Data, "detail") ?? ReadText(record.Data, "code");
                continue;
            }
            if (!record.Data.TryGetProperty("role", out JsonElement role) || role.ValueKind != JsonValueKind.String || role.GetString() != "Primary")
            {
                continue;
            }
            if (record.Kind == DecisionRecordKinds.Proposal) return null;
            if (record.Kind != DecisionRecordKinds.ProposalFailed) continue;
            failed++;
            if (firstError is null)
            {
                firstError = ReadText(record.Data, "message") ?? ReadText(record.Data, "reason") ?? "no message";
            }
        }
        if (failed == 0) return null;
        string best = claudeError ?? firstError!;
        string error = best.Length > 200 ? best[..200] + "..." : best;
        return string.Create(CultureInfo.InvariantCulture, $"skipped: {arm}: 0 of {failed} model proposals succeeded in the first match ({error})");
    }

    private static string? ReadText(JsonElement data, string name) =>
        data.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    /// <summary>
    /// The rules every side plays on and the playbooks fitted to their roster: the embedded approximate fixture and
    /// the default playbooks when <paramref name="rulesPath"/> is null, else that rules JSON and the default playbooks
    /// adapted to it (<see cref="PlaybookRosterAdapter"/>; the playbooks were authored against the fixture).
    /// </summary>
    public static (IRulesDatabase Rules, IPlaybookLibrary Playbooks, IReadOnlyList<RosterChange> Changes) LoadRules(string? rulesPath, IReadOnlyList<string>? playbookFiles = null)
    {
        PlaybookLibrary authored = MergePlaybookFiles(PlaybookLibrary.LoadDefault(), playbookFiles ?? []);
        if (rulesPath is null) return (RulesDatabase.LoadEmbeddedFixture(), authored, []);
        if (!File.Exists(rulesPath)) throw new ArgumentException($"No rules file at {rulesPath}.");
        RulesDatabase rules = RulesDatabase.LoadJson(File.ReadAllText(rulesPath));
        RosterAdaptation adapted = PlaybookRosterAdapter.Adapt(authored.All, rules, RulesDatabase.LoadEmbeddedFixture());
        return (rules, adapted.Library, adapted.Changes);
    }

    /// <summary>
    /// <paramref name="baseLibrary"/> plus every playbook of each <c>--playbooks</c> file, in order, as one library.
    /// A file that is missing or malformed, or an id already taken (by the base or an earlier file), is an
    /// <see cref="ArgumentException"/> naming the id and file.
    /// </summary>
    public static PlaybookLibrary MergePlaybookFiles(PlaybookLibrary baseLibrary, IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(baseLibrary);
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0) return baseLibrary;
        List<Playbook> merged = [.. baseLibrary.All];
        HashSet<string> taken = new(merged.Select(static p => p.Id), StringComparer.Ordinal);
        foreach (string file in files)
        {
            if (!File.Exists(file)) throw new ArgumentException($"No playbook file at {file}.");
            PlaybookLibrary loaded;
            try
            {
                loaded = PlaybookLibrary.LoadJson(File.ReadAllText(file));
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
            {
                throw new ArgumentException($"Playbook file {file} is not a valid playbook document: {ex.Message}");
            }
            foreach (Playbook playbook in loaded.All)
            {
                if (!taken.Add(playbook.Id)) throw new ArgumentException($"Duplicate playbook id '{playbook.Id}' in {file}: already defined by the default library or an earlier --playbooks file.");
                merged.Add(playbook);
            }
        }
        return new PlaybookLibrary(merged);
    }

    /// <summary>The default library (tuned parameters applied) as <see cref="PlaybookDocument"/> JSON, the format <c>--playbooks</c> loads.</summary>
    public static string ExportPlaybooksJson() =>
        System.Text.Json.JsonSerializer.Serialize(new PlaybookDocument(PlaybookLibrary.LoadDefault().All), new System.Text.Json.JsonSerializerOptions(BotJson.Options) { WriteIndented = true });

    /// <summary>
    /// The adoption rule (<see cref="VocabularyAdoption.Decide"/>) applied to a run's tier arms: for each tier arm and
    /// the tier arm below it, the paired match-score comparison on each split (only held-out counts for adoption).
    /// <paramref name="live"/> is false for <c>--llm-fake</c> runs, whose evidence never adopts anything; nor does the
    /// evidence of a tier arm (or the arm it is compared with) whose pooled LLM call failure rate on that split exceeds
    /// <paramref name="maxLlmFailureRate"/>, since the selector fallback played that arm's gaps.
    /// </summary>
    public static VocabularyAdoption TierAdoption(IReadOnlyList<MatchRecord> matches, bool live, string? date, double maxLlmFailureRate = CliOptions.DefaultMaxLlmFailureRate)
    {
        ArgumentNullException.ThrowIfNull(matches);
        IReadOnlyDictionary<(string Arm, string Split), LlmCallTally> unreliable = LlmCallTally.Unreliable(matches, maxLlmFailureRate);
        List<TierEvidence> evidence = [];
        foreach ((string arm, VocabularyTier tier) in BotAgentFactory.TierArms)
        {
            if (tier == VocabularyTier.PlaybookOnly) continue;
            string below = BotAgentFactory.TierArms.Single(p => p.Value == tier - 1).Key;
            foreach (string split in new[] { "heldout", "training" })
            {
                IReadOnlyList<(MatchRecord Baseline, MatchRecord Arm)> pairs = PairedReport.Pairs(matches, below, arm, split);
                if (pairs.Count == 0) continue;
                PairedDifference d = PairedStatistics.Compare("score", [.. pairs.Select(static p => (Score(p.Baseline), Score(p.Arm)))], higherIsBetter: true);
                string? refusal = null;
                foreach (string suspect in new[] { arm, below })
                {
                    if (unreliable.TryGetValue((suspect, split), out LlmCallTally? tally))
                    {
                        refusal = string.Create(CultureInfo.InvariantCulture, $"{suspect} is not a model result on {split} maps ({tally.Failed} of {tally.Answered + tally.Failed} model calls failed, {tally.FailureRate:P1}, above {maxLlmFailureRate:0.##}); its evidence is refused");
                        break;
                    }
                }
                evidence.Add(new TierEvidence(tier, tier - 1, split, d.Pairs, d.BaselineMean, d.ArmMean, d.MeanDifference, d.CiLow, d.CiHigh, d.Better, d.Worse, d.Ties, d.SignTestP, live && refusal is null, refusal));
            }
        }
        return VocabularyAdoption.Decide(evidence, date);
    }

    private static double Score(MatchRecord m) => m.Winner switch { 0 => 1.0, null => 0.5, _ => 0.0 };

    /// <summary>
    /// The arm whose decisions teach the distilled arm: the run's own belief-frame <c>llm</c> arm when there is one
    /// (<c>Reported</c> true: its dataset is taken after it runs), else a belief-frame <c>llm</c> arm to run on the
    /// training maps first, unreported. Never the selector: distilling one rule set into another says nothing
    /// about step 7.
    /// </summary>
    public static (ArmSpec Teacher, bool Reported) DistillTeacher(IReadOnlyList<ArmSpec> arms, bool llmFake)
    {
        ArgumentNullException.ThrowIfNull(arms);
        ArmSpec? inRun = arms.FirstOrDefault(static a => a.Name == "llm" && !a.Oracle);
        return inRun is not null ? (inRun, true) : (new ArmSpec("llm", false, llmFake), false);
    }

    /// <summary>
    /// Fewest examples the distilled arm runs on. Below this nearly every state is out of distribution, so the arm
    /// would escalate almost every decision to the LLM and report an LLM arm's play under the distilled label.
    /// </summary>
    public const int MinDistillExamples = 20;

    /// <summary>Why the distilled arm cannot run on <paramref name="dataset"/>, or null when it has enough examples.</summary>
    public static string? DistillSkipReason(DecisionDataset dataset, string source)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        return dataset.Count >= MinDistillExamples
            ? null
            : $"skipped: the dataset from {source} has {dataset.Count} examples, fewer than the {MinDistillExamples} needed; every decision would escalate to the LLM";
    }

    /// <summary>
    /// Runs the teacher on <see cref="SimMaps.Training"/> (always, whatever the run's <c>--maps</c>; unreported) and
    /// returns its primary decisions.
    /// </summary>
    private static DecisionDataset TeacherDataset(ArmSpec teacher, CliOptions options, IRulesDatabase rules, BotAgentFactory factory)
    {
        List<(ArmSpec Arm, string Opponent, SimMap Map, string Split, int Seed)> jobs =
            Jobs(teacher, OpponentSets.TeacherOpponents(options.Opponents), options.Seeds, [.. SimMaps.Training.Select(static m => (m, "training"))], options.SeedList);
        ConcurrentDictionary<int, IReadOnlyList<DecisionRecord>> logs = new();
        if (options.LlmFake)
        {
            Parallel.For(0, jobs.Count, i => RunJob(jobs[i], options, rules, factory, log => logs[i] = log));
        }
        else
        {
            for (int i = 0; i < jobs.Count; i++)
            {
                int index = i;
                RunJob(jobs[i], options, rules, factory, log => logs[index] = log);
            }
        }
        return DecisionDataset.Merge(Enumerable.Range(0, jobs.Count)
            .Where(logs.ContainsKey)
            .Select(i => DecisionDataset.FromDecisionLog(logs[i], DatasetFilter.PrimaryOnly, MatchId(jobs[i]), ModeOf(jobs[i].Arm))));
    }

    /// <summary>The observation mode an arm plays in, for dataset records that do not carry it.</summary>
    private static ObservationMode ModeOf(ArmSpec arm) => arm.Oracle ? ObservationMode.Oracle : ObservationMode.Belief;

    private static List<(ArmSpec Arm, string Opponent, SimMap Map, string Split, int Seed)> Jobs(ArmSpec arm, CliOptions options, List<(SimMap Map, string Split)> maps) =>
        Jobs(arm, options.Opponents, options.Seeds, maps, options.SeedList);

    private static List<(ArmSpec Arm, string Opponent, SimMap Map, string Split, int Seed)> Jobs(ArmSpec arm, IReadOnlyList<string> opponents, int seeds, List<(SimMap Map, string Split)> maps, IReadOnlyList<int>? seedList = null)
    {
        IReadOnlyList<int> played = seedList ?? [.. Enumerable.Range(1, seeds)];
        List<(ArmSpec, string, SimMap, string, int)> jobs = [];
        foreach (string opponent in opponents)
        {
            foreach ((SimMap map, string split) in maps)
            {
                foreach (int seed in played) jobs.Add((arm, opponent, map, split, seed));
            }
        }
        return jobs;
    }

    private static MatchRecord RunJob((ArmSpec Arm, string Opponent, SimMap Map, string Split, int Seed) job, CliOptions options, IRulesDatabase rules, IArenaAgentFactory factory, Action<IReadOnlyList<DecisionRecord>> armLog)
    {
        if (factory is BotAgentFactory { Context: var done } && done.Completed.TryGetValue(MatchId(job), out CompletedMatch? finished))
        {
            if (finished.Log is not null) armLog(finished.Log);
            return finished.Record;
        }
        if (options.Resume && ArenaScheduling.TryLoad(new ArenaJob(job.Arm, job.Opponent, job.Map, job.Split, job.Seed), options) is { } loaded)
        {
            armLog(loaded.Log!);
            return loaded.Record;
        }
        IReadOnlyList<DecisionRecord>? captured = null;
        MatchRecord played = RunTraced(job, options, rules, factory, log =>
        {
            captured = log;
            armLog(log);
        });
        if (options.WriteDecisions && captured is not null && factory is BotAgentFactory { Context: var context })
        {
            MatchManifest manifest = new(
                MatchManifest.CurrentSchema, job.Arm, job.Opponent, job.Map.Map.MapId, job.Split, job.Seed, options.MaxSeconds,
                options.Benchmark, options.LlmLatencySeconds, job.Arm.Name == "distilled" ? context.DistillSource : null,
                played.Players["arm"].DecisionLogHash, played.Winner, played.Reason, played.DurationSeconds,
                options.RulesPath is null ? null : Path.GetFullPath(options.RulesPath), played, ArenaScheduling.Fingerprint(options),
                options.PlaybookFiles.Count == 0 ? null : [.. options.PlaybookFiles.Select(static f => Path.GetFullPath(f))]);
            MatchManifest.Write(Path.Combine(options.OutDir, "decisions"), manifest, captured);
        }
        return played;
    }

    private static MatchRecord RunTraced((ArmSpec Arm, string Opponent, SimMap Map, string Split, int Seed) job, CliOptions options, IRulesDatabase rules, IArenaAgentFactory factory, Action<IReadOnlyList<DecisionRecord>> armLog)
    {
        if (options.TraceDir is null) return MatchRunner.Run(job.Arm, job.Opponent, job.Map, job.Split, job.Seed, options.MaxSeconds, rules, factory, armLog, benchmark: options.Benchmark);
        Directory.CreateDirectory(options.TraceDir);
        using StreamWriter trace = new(Path.Combine(options.TraceDir, $"{Slug(job.Arm.ToString())}_{job.Opponent.Replace(':', '-')}_{job.Map.Map.MapId}_{job.Seed}.txt"));
        MatchRecord record = MatchRunner.Run(job.Arm, job.Opponent, job.Map, job.Split, job.Seed, options.MaxSeconds, rules, factory, log =>
        {
            armLog(log);
            foreach (DecisionRecord r in log) trace.WriteLine(string.Create(CultureInfo.InvariantCulture, $"log {r.Time.Seconds:0} {r.Kind} {r.Data}"));
        }, trace, options.Benchmark);
        trace.WriteLine(string.Create(CultureInfo.InvariantCulture, $"result winner={record.Winner} reason={record.Reason} at {record.DurationSeconds:0}"));
        return record;
    }

    /// <summary>
    /// Re-runs one recorded match: the same arm, opponent, map, seed and benchmark, with the arm's primary and shadow
    /// strategists replaced by <see cref="ReplayStrategist"/>s over the recorded log (LLM answers come from the log,
    /// never from a model), and compares the new decision log with the recorded one.
    /// </summary>
    /// <param name="writeReplayed">Optional path for the replayed decision log, to diff against the recording.</param>
    public static ReplayResult ReplayMatch(string ndjsonPath, string? writeReplayed = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ndjsonPath);
        string manifestPath = MatchManifest.PathFor(ndjsonPath);
        if (!File.Exists(ndjsonPath)) throw new ArgumentException($"No decision log at {ndjsonPath}.");
        if (!File.Exists(manifestPath)) throw new ArgumentException($"No match manifest at {manifestPath}; replay needs the settings the arena writes next to each log.");
        MatchManifest manifest = MatchManifest.Load(manifestPath);
        IReadOnlyList<DecisionRecord> recorded;
        using (StreamReader reader = new(ndjsonPath)) recorded = DecisionLogCodec.ReadAll(reader);

        // The files are loaded again from their recorded paths: refuse if their contents are no longer what the match was played on.
        if (manifest.Fingerprint is { } fingerprint)
        {
            if (fingerprint.TryGetValue("playbooks", out string? recordedPlaybooks) && manifest.PlaybookFiles is { Count: > 0 } files)
            {
                foreach (string file in files) if (!File.Exists(file)) throw new ArgumentException($"No playbook file at {file}.");
                string now = ArenaScheduling.PlaybooksHash(files);
                if (now != recordedPlaybooks) throw new ArgumentException($"playbook files changed since the match: recorded {recordedPlaybooks}, now {now}.");
            }
            if (fingerprint.TryGetValue("rules", out string? recordedRules) && manifest.RulesFile is { } rulesFile && File.Exists(rulesFile))
            {
                string now = ArenaScheduling.RulesHash(rulesFile);
                if (now != recordedRules) throw new ArgumentException($"rules file changed since the match: recorded {recordedRules}, now {now}.");
            }
        }
        (IRulesDatabase rules, IPlaybookLibrary playbooks, _) = LoadRules(manifest.RulesFile, manifest.PlaybookFiles ?? []);
        // No model is ever asked: a fake context keeps credential resolution out of a replay entirely.
        ArenaRunContext context = new(llmFake: true, manifest.LlmLatencySeconds);
        BotAgentFactory factory = new(rules, playbooks, context);
        ReplayAgentFactory replayFactory = new(factory, manifest.Arm, recorded);
        SimMap map = SimMaps.All.SingleOrDefault(m => m.Map.MapId == manifest.Map) ?? throw new ArgumentException($"Unknown map '{manifest.Map}'.");
        IReadOnlyList<DecisionRecord> replayed = [];
        MatchRunner.Run(manifest.Arm, manifest.Opponent, map, manifest.Split, manifest.Seed, manifest.MaxSeconds, rules, replayFactory,
            log => replayed = log, benchmark: manifest.Benchmark);

        if (writeReplayed is not null)
        {
            using NdjsonDecisionLogWriter writer = new(File.Create(writeReplayed));
            foreach (DecisionRecord record in replayed) writer.Write(record);
        }
        string recordedHash = DecisionLogCodec.Hash(recorded);
        string replayedHash = DecisionLogCodec.Hash(replayed);
        int? first = null;
        if (recordedHash != replayedHash)
        {
            int n = Math.Min(recorded.Count, replayed.Count);
            for (int i = 0; i < n && first is null; i++)
            {
                if (DecisionLogCodec.ToLine(recorded[i]) != DecisionLogCodec.ToLine(replayed[i])) first = i;
            }
            first ??= n;
        }
        int misses = replayFactory.Primary!.Misses + (replayFactory.Shadow?.Misses ?? 0);
        int unused = replayFactory.Primary.Unused + (replayFactory.Shadow?.Unused ?? 0);
        return new ReplayResult(ndjsonPath, recordedHash == replayedHash, recordedHash, replayedHash, manifest.RecordedHash, misses, unused, recorded.Count, replayed.Count, first);
    }

    /// <summary>
    /// The post-game analysis of one match log (<see cref="PostGameReport"/>), titled from its manifest when there is
    /// one, with an optional narrative from <see cref="PostGameNarrator"/> (the fake client with
    /// <paramref name="llmFake"/>, else the Anthropic API; without a credential the section says so).
    /// </summary>
    public static string Analyze(string ndjsonPath, bool narrate, bool llmFake)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ndjsonPath);
        if (!File.Exists(ndjsonPath)) throw new ArgumentException($"No decision log at {ndjsonPath}.");
        IReadOnlyList<DecisionRecord> records;
        using (StreamReader reader = new(ndjsonPath)) records = DecisionLogCodec.ReadAll(reader);
        string manifestPath = MatchManifest.PathFor(ndjsonPath);
        MatchManifest? manifest = File.Exists(manifestPath) ? MatchManifest.Load(manifestPath) : null;
        string header = manifest is null
            ? Path.GetFileName(ndjsonPath)
            : $"{manifest.Arm} vs {manifest.Opponent} on {manifest.Map}, seed {manifest.Seed} ({manifest.Split}), benchmark {manifest.Benchmark.Name}";
        PostGameReport report = PostGameReport.Build(records);
        StringBuilder md = new(report.ToMarkdown(manifest is null ? "Post-game report" : $"Post-game report: {header}"));
        if (!narrate) return md.ToString();

        md.AppendLine();
        md.AppendLine("## Narrative");
        md.AppendLine();
        IMessageClient? client = null;
        if (llmFake)
        {
            client = new FakeMessageClient();
            md.AppendLine("_Written by the `--llm-fake` client: scripted from the report above, not a model._");
            md.AppendLine();
        }
        else
        {
            try
            {
                client = new AnthropicMessageClient();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                md.AppendLine($"No narrative: no credential resolved ({ex.GetType().Name}: {ex.Message}). Set ANTHROPIC_API_KEY or pass --llm-fake.");
                return md.ToString();
            }
        }
        NarrationResult narration = new PostGameNarrator(client).NarrateAsync(report, header).GetAwaiter().GetResult();
        md.AppendLine(narration.Narrative ?? $"No narrative: {narration.Failure}");
        return md.ToString();
    }

    /// <summary>Builds the recorded arm as a replay and every other side as usual.</summary>
    private sealed class ReplayAgentFactory(BotAgentFactory inner, ArmSpec arm, IReadOnlyList<DecisionRecord> recorded) : IArenaAgentFactory
    {
        public ReplayStrategist? Primary { get; private set; }

        public ReplayStrategist? Shadow { get; private set; }

        public IArenaAgent Create(ArmSpec spec, PlayerId player, Faction faction, MapInfo map, int seed)
        {
            if (spec != arm) return inner.Create(spec, player, faction, map, seed);
            BotArenaAgent agent = inner.CreateReplay(spec, player, faction, map, seed, recorded, out ReplayStrategist primary, out ReplayStrategist? shadow);
            Primary = primary;
            Shadow = shadow;
            return agent;
        }
    }

    private static string MatchId((ArmSpec Arm, string Opponent, SimMap Map, string Split, int Seed) job) =>
        $"{job.Arm}/{job.Opponent}/{job.Map.Map.MapId}/{job.Seed}";

    private static string Slug(string name) => name.Replace('+', '-');

    private static List<(SimMap Map, string Split)> MapsForSplit(string split) => split.ToLowerInvariant() switch
    {
        "training" => [.. SimMaps.Training.Select(m => (m, "training"))],
        "heldout" => [.. SimMaps.HeldOut.Select(m => (m, "heldout"))],
        "all" => [.. SimMaps.Training.Select(m => (m, "training")), .. SimMaps.HeldOut.Select(m => (m, "heldout"))],
        _ => throw new ArgumentException($"Unknown map split '{split}'; expected training, heldout or all."),
    };
}
