// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bindery.Ra2.Bot.Claude;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Runtime;
using Bindery.Ra2.Bot.Sim;

namespace Bindery.Ra2.Bot.Arena;

/// <summary>One arena match to play: the arm, its opponent, the map and its split, and the seed.</summary>
internal readonly record struct ArenaJob(ArmSpec Arm, string Opponent, SimMap Map, string Split, int Seed)
{
    /// <summary>Identifies the match within a run (also the key of <see cref="ArenaRunContext.Completed"/>).</summary>
    public string MatchId => $"{Arm}/{Opponent}/{Map.Map.MapId}/{Seed}";
}

/// <summary>A match already played (this run's interleave phase) or loaded from disk (<c>--resume</c>).</summary>
/// <param name="Record">The match's result record.</param>
/// <param name="Log">The arm's decision log, or null when nothing downstream needs it.</param>
public sealed record CompletedMatch(MatchRecord Record, IReadOnlyList<DecisionRecord>? Log);

/// <summary>Job ordering for <c>--interleave</c> and loading of finished matches for <c>--resume</c>.</summary>
public static class ArenaScheduling
{
    /// <summary>
    /// Cell-major order: <paramref name="perArm"/> holds each arm's jobs in the same cell order (opponent, map, seed);
    /// the result lists cell 0 of every arm (in arm order), then cell 1 of every arm, and so on, as (arm, job index).
    /// </summary>
    public static List<(int Arm, int Job)> CellMajor<T>(IReadOnlyList<IReadOnlyList<T>> perArm)
    {
        ArgumentNullException.ThrowIfNull(perArm);
        int cells = perArm.Count == 0 ? 0 : perArm[0].Count;
        if (perArm.Any(a => a.Count != cells)) throw new ArgumentException("Every arm must have the same number of cells to interleave.");
        List<(int, int)> order = [];
        for (int cell = 0; cell < cells; cell++)
        {
            for (int arm = 0; arm < perArm.Count; arm++) order.Add((arm, cell));
        }
        return order;
    }

    private static readonly ConditionalWeakTable<CliOptions, IReadOnlyDictionary<string, string>> Fingerprints = new();

    /// <summary>
    /// What besides the job itself decides a match's result, recorded with each match and compared by <c>--resume</c> so a
    /// resumed run cannot silently mix runs: code identity (MVIDs of the arena, bot, sim and Claude assemblies), SHA-256 of the
    /// rules (file contents, else the embedded fixture), the arm knobs, LLM model, endpoint (host and path only, no credentials)
    /// and latency, whether <c>--extended-metrics</c> is on ("" for off), the distillation dataset's SHA-256, the <c>--seed-list</c>, and the <c>--playbooks</c> files' SHA-256 ("" for none).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Fingerprint(CliOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Fingerprints.GetValue(options, static o => new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["code"] = CodeIdentity(),
            ["rules"] = RulesHash(o.RulesPath),
            ["playbooks"] = PlaybooksHash(o.PlaybookFiles),
            ["knobs"] = string.Join(",", o.ArmKnobs.OrderBy(static k => k.Key, StringComparer.Ordinal).Select(static k => string.Create(CultureInfo.InvariantCulture, $"{k.Key}={k.Value:R}"))),
            ["llmModel"] = o.LlmModel,
            ["llmEndpoint"] = o.LlmEndpoint is null ? "" : Uri.TryCreate(o.LlmEndpoint, UriKind.Absolute, out Uri? u) ? $"{u.Scheme}://{u.Host}:{u.Port}{u.AbsolutePath}" : "(unparsed)",
            ["llmLatency"] = o.LlmLatencySeconds is { } l ? l.ToString("R", CultureInfo.InvariantCulture) : "",
            ["extendedMetrics"] = o.ExtendedMetrics ? "on" : "",
            ["dataset"] = o.Dataset is null ? "" : Sha(File.ReadAllBytes(o.Dataset)),
            // --seed-list changes which matches a learning arm has seen before a given one; empty for 1..--seeds, so
            // extending --seeds still resumes.
            ["seedList"] = o.SeedList is null ? "" : string.Join(",", o.SeedList),
        });
    }

    /// <summary>SHA-256 of the rules file's contents, else of the embedded fixture.</summary>
    public static string RulesHash(string? rulesPath) =>
        rulesPath is null ? Sha(Encoding.UTF8.GetBytes(RulesDatabase.EmbeddedFixtureJson(RulesDatabase.FixtureFile))) : Sha(File.ReadAllBytes(rulesPath));

    /// <summary>
    /// SHA-256 over the <c>--playbooks</c> files' contents, "" for none. The library is sorted by id, so the flag order
    /// does not matter: the files' own hashes are sorted before they are hashed together. A changed set changes what the LLM is offered.
    /// </summary>
    public static string PlaybooksHash(IReadOnlyList<string> files) =>
        files.Count == 0 ? "" : Sha(Encoding.UTF8.GetBytes(string.Join(";", files.Select(static f => Sha(File.ReadAllBytes(f))).Order(StringComparer.Ordinal))));

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>The MVIDs of the assemblies that decide a match, hashed: the same source built the same way gives the same identity.</summary>
    public static string CodeIdentity()
    {
        Type[] anchors = [typeof(Program), typeof(RulesDatabase), typeof(Bindery.Ra2.Bot.Sim.SkirmishSimulation), typeof(AnthropicMessageClient)];
        return Sha(Encoding.UTF8.GetBytes(string.Join(";", anchors.Select(static t => t.Assembly.ManifestModule.ModuleVersionId.ToString("N")))));
    }

    private static string Stem(ArenaJob job) => MatchManifest.Stem(job.Arm, job.Opponent, job.Map.Map.MapId, job.Seed);

    /// <summary>The manifest path of a job's match under <paramref name="outDir"/>.</summary>
    internal static string ManifestPath(string outDir, ArenaJob job) => Path.Combine(outDir, "decisions", Stem(job) + ".match.json");

    /// <summary>
    /// The manifest of a job already played into the run's out dir, checked against the job (arm, opponent, map, split,
    /// seed, benchmark, max seconds); null when there is none. Throws <see cref="ArgumentException"/> on a mismatch.
    /// </summary>
    internal static MatchManifest? FindManifest(ArenaJob job, CliOptions options)
    {
        string path = ManifestPath(options.OutDir, job);
        if (!File.Exists(path)) return null;
        MatchManifest m;
        try
        {
            m = MatchManifest.Load(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            throw new ArgumentException($"--resume: cannot read {path}: {ex.Message}");
        }
        List<string> diffs = [];
        void Check(string name, object? recorded, object? expected)
        {
            if (!Equals(recorded, expected)) diffs.Add($"{name} recorded {recorded ?? "null"} but this run has {expected ?? "null"}");
        }
        Check("arm", m.Arm, job.Arm);
        Check("opponent", m.Opponent, job.Opponent);
        Check("map", m.Map, job.Map.Map.MapId);
        Check("split", m.Split, job.Split);
        Check("seed", m.Seed, job.Seed);
        Check("maxSeconds", m.MaxSeconds, options.MaxSeconds);
        // BenchmarkSettings holds a list (reference equality), so compare the serialised form.
        Check("benchmark", JsonSerializer.Serialize(m.Benchmark, BotJson.Options), JsonSerializer.Serialize(options.Benchmark, BotJson.Options));
        if (m.Fingerprint is null)
        {
            diffs.Add("the manifest holds no run fingerprint (written before it existed)");
        }
        else
        {
            foreach ((string key, string expected) in Fingerprint(options))
            {
                Check($"fingerprint.{key}", m.Fingerprint.GetValueOrDefault(key, key is "seedList" or "playbooks" or "extendedMetrics" ? "" : null!), expected);
            }
        }
        if (job.Arm.Name == "distilled" && options.Dataset is not null) Check("distillSource", m.DistillSource, Path.GetFileName(options.Dataset));
        if (m.Record is null)
        {
            diffs.Add("the manifest holds no match record (written before --resume existed)");
        }
        else
        {
            Check("record.arm", m.Record.Arm, job.Arm.ToString());
            Check("record.opponent", m.Record.Opponent, job.Opponent);
            Check("record.map", m.Record.Map, job.Map.Map.MapId);
            Check("record.split", m.Record.Split, job.Split);
            Check("record.seed", m.Record.Seed, job.Seed);
        }
        if (diffs.Count > 0) throw new ArgumentException($"--resume: {path} does not match the job {job.MatchId}: {string.Join("; ", diffs)}. Use a fresh --out or the original settings.");
        return m;
    }

    /// <summary>Loads a finished match (record and decision log) from <c>--out</c>, or null when it has not been played.</summary>
    internal static CompletedMatch? TryLoad(ArenaJob job, CliOptions options)
    {
        if (FindManifest(job, options) is not { } manifest) return null;
        string ndjson = Path.Combine(options.OutDir, "decisions", Stem(job) + ".ndjson");
        if (!File.Exists(ndjson)) throw new ArgumentException($"--resume: the manifest for {job.MatchId} has no decision log {ndjson}.");
        IReadOnlyList<DecisionRecord> log;
        using (StreamReader reader = new(ndjson)) log = DecisionLogCodec.ReadAll(reader);
        if (manifest.RecordedHash is not null && DecisionLogCodec.Hash(log) != manifest.RecordedHash)
        {
            throw new ArgumentException($"--resume: {ndjson} does not hash to the recorded {manifest.RecordedHash}; the log was edited or truncated.");
        }
        return new CompletedMatch(manifest.Record!, log);
    }

    /// <summary>
    /// The bandit learns across its matches in order, and loaded matches do not replay that learning, so a partly
    /// finished bandit arm cannot be resumed faithfully: it must be all loaded or none.
    /// </summary>
    internal static void CheckBanditResume(ArmSpec arm, IReadOnlyList<ArenaJob> jobs, CliOptions options)
    {
        if (arm.Name != "bandit") return;
        int existing = jobs.Count(j => File.Exists(ManifestPath(options.OutDir, j)));
        if (existing != 0 && existing != jobs.Count)
        {
            throw new ArgumentException($"--resume: arm {arm} has {existing} of {jobs.Count} matches recorded; the bandit learns across matches in order, so it can only be resumed complete. Delete its records under {Path.Combine(options.OutDir, "decisions")} to rerun it.");
        }
    }

    /// <summary>
    /// Plays the given arms' jobs in cell-major order on up to <see cref="Environment.ProcessorCount"/> workers and
    /// stores each finished match in <see cref="ArenaRunContext.Completed"/>, from which the per-arm loop then takes them.
    /// A bandit arm keeps its per-arm order (one match at a time; all bandit arms are serialised, since the learning flag is shared).
    /// </summary>
    internal static void RunInterleaved(
        IReadOnlyList<ArmSpec> arms,
        Func<ArmSpec, List<ArenaJob>> jobsFor,
        ArenaRunContext context,
        Func<ArenaJob, Action<IReadOnlyList<DecisionRecord>>, MatchRecord> run)
    {
        List<ArenaJob>[] perArm = [.. arms.Select(jobsFor)];
        List<(int Arm, int Job)> order = CellMajor(perArm);
        int[] nextTurn = new int[arms.Count];
        object turnLock = new();
        object banditLock = new();
        int cursor = -1;
        int workers = Math.Max(1, Environment.ProcessorCount);
        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, _ =>
        {
            while (true)
            {
                int n = Interlocked.Increment(ref cursor);
                if (n >= order.Count) return;
                (int a, int j) = order[n];
                ArenaJob job = perArm[a][j];
                if (arms[a].Name != "bandit")
                {
                    Play(job);
                    continue;
                }
                lock (turnLock)
                {
                    while (nextTurn[a] != j) Monitor.Wait(turnLock);
                }
                try
                {
                    lock (banditLock)
                    {
                        context.BanditLearning = Program.BanditLearnsFrom(job.Opponent, job.Split);
                        Play(job);
                        context.BanditLearning = true;
                    }
                }
                finally
                {
                    lock (turnLock)
                    {
                        nextTurn[a]++;
                        Monitor.PulseAll(turnLock);
                    }
                }
            }
        });

        void Play(ArenaJob job)
        {
            IReadOnlyList<DecisionRecord>? log = null;
            MatchRecord record = run(job, l => log = l);
            // Only training-map, training-opponent logs feed the exported dataset; keep no others in memory.
            context.Completed[job.MatchId] = new CompletedMatch(record, Program.BanditLearnsFrom(job.Opponent, job.Split) ? log : null);
        }
    }
}
