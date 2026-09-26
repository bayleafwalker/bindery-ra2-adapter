// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Runtime;

namespace Bindery.Ra2.Bot.Arena;

/// <summary>
/// Everything needed to re-run one arena match besides its decision log: the arm, the opponent, the map and seed,
/// the match length, the benchmark (and so the full simulator settings) and the LLM latency setting. Written as
/// <c>decisions/&lt;match&gt;.match.json</c> next to the log <c>decisions/&lt;match&gt;.ndjson</c>.
/// </summary>
/// <param name="RecordedHash">The arm's decision log hash when the match was played (<see cref="DecisionLogCodec.Hash"/>).</param>
public sealed record MatchManifest(
    string Schema,
    ArmSpec Arm,
    string Opponent,
    string Map,
    string Split,
    int Seed,
    double MaxSeconds,
    BenchmarkSettings Benchmark,
    double? LlmLatencySeconds,
    string? DistillSource,
    string? RecordedHash,
    int? Winner,
    string Reason,
    double DurationSeconds)
{
    public const string CurrentSchema = "bindery.arena.match/v1";

    private static readonly JsonSerializerOptions Indented = new(BotJson.Options) { WriteIndented = true };

    /// <summary>The file name stem shared by a match's log and manifest.</summary>
    public static string Stem(ArmSpec arm, string opponent, string map, int seed) =>
        $"{arm.ToString().Replace('+', '-').Replace('@', '_')}_{opponent.Replace(':', '-')}_{map}_{seed}";

    /// <summary>The manifest path for a decision log path (<c>x.ndjson</c> → <c>x.match.json</c>).</summary>
    public static string PathFor(string ndjsonPath) => Path.ChangeExtension(ndjsonPath, ".match.json");

    public string ToJson() => JsonSerializer.Serialize(this, Indented);

    public static MatchManifest Load(string path)
    {
        MatchManifest manifest = JsonSerializer.Deserialize<MatchManifest>(File.ReadAllText(path), BotJson.Options)
            ?? throw new InvalidDataException($"{path} holds no match manifest.");
        if (manifest.Schema != CurrentSchema) throw new InvalidDataException($"{path}: schema '{manifest.Schema}', expected '{CurrentSchema}'.");
        return manifest;
    }

    /// <summary>Writes a match's decision log (through <see cref="NdjsonDecisionLogWriter"/>) and its manifest into <paramref name="dir"/>.</summary>
    public static string Write(string dir, MatchManifest manifest, IReadOnlyList<DecisionRecord> log)
    {
        Directory.CreateDirectory(dir);
        string stem = Stem(manifest.Arm, manifest.Opponent, manifest.Map, manifest.Seed);
        string ndjson = Path.Combine(dir, stem + ".ndjson");
        using (NdjsonDecisionLogWriter writer = new(File.Create(ndjson)))
        {
            foreach (DecisionRecord record in log) writer.Write(record);
        }
        File.WriteAllText(PathFor(ndjson), manifest.ToJson() + "\n");
        return ndjson;
    }
}

/// <summary>The outcome of <c>arena replay</c>.</summary>
/// <param name="Misses">Requests the recorded log had no answer for (primary and shadow).</param>
/// <param name="Unused">Recorded answers no replayed request asked for.</param>
/// <param name="FirstDifference">Index of the first record that differs, when the logs differ.</param>
public sealed record ReplayResult(
    string LogPath,
    bool Equal,
    string RecordedHash,
    string ReplayedHash,
    string? ManifestHash,
    int Misses,
    int Unused,
    int RecordedRecords,
    int ReplayedRecords,
    int? FirstDifference);
