// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bindery.Ra2.Bot.Playbooks;

namespace Bindery.Ra2.Bot.Arena;

/// <summary>Parsed <c>arena induce</c> arguments.</summary>
public sealed record InduceOptions(
    IReadOnlyList<string> From,
    string Arm,
    string Split,
    string OutPath,
    string? ReportPath,
    int MinSupport,
    IReadOnlyList<string> PlaybookFiles)
{
    public const int DefaultMinSupport = 30;

    public const string Usage =
        "Usage: arena induce --from <dir> [--from <dir> ...] --arm <arm> [--split training|heldout|all] --out <playbooks.json> [--report <md>] [--min-support N] [--playbooks <playbooks.json> ...]\n" +
        "       (compiles the arm's decisions in won matches into one induced playbook per faction and chosen playbook; --split defaults to training)";

    public static InduceOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0 || args[0] != "induce") throw new ArgumentException(Usage);
        List<string> from = [], playbooks = [];
        string? arm = null, outPath = null, report = null;
        string split = "training";
        int minSupport = DefaultMinSupport;
        for (int i = 1; i < args.Count; i++)
        {
            string Next() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.\n{Usage}");
            switch (args[i])
            {
                case "--from": from.Add(Next()); break;
                case "--arm": arm = Next(); break;
                case "--split": split = Next(); break;
                case "--out": outPath = Next(); break;
                case "--report": report = Next(); break;
                case "--playbooks": playbooks.Add(Next()); break;
                case "--min-support":
                    if (!int.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out minSupport) || minSupport < 1) throw new ArgumentException("--min-support takes a positive integer.");
                    break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'.\n{Usage}");
            }
        }
        if (from.Count == 0 || arm is null || outPath is null) throw new ArgumentException(Usage);
        if (split is not ("training" or "heldout" or "all")) throw new ArgumentException($"--split takes training, heldout or all, not '{split}'.");
        return new InduceOptions(from, arm, split, outPath, report, minSupport, playbooks);
    }
}

/// <summary>What <see cref="PlaybookInducer.Induce"/> produced.</summary>
/// <param name="Json">The <see cref="PlaybookDocument"/> JSON, byte-identical for identical inputs.</param>
/// <param name="Warnings">Printed by the CLI before it writes anything (held-out data, unreadable logs).</param>
public sealed record InductionResult(IReadOnlyList<Playbook> Playbooks, string Json, string Report, IReadOnlyList<string> Warnings);

/// <summary>
/// Tier 2 of the LLM-to-playbook pipeline: compiles an LLM arm's captured decisions into playbooks. For every
/// (faction, chosen base playbook) cluster with enough proposals from won matches it emits one two-phase playbook:
/// the base's posture, budget and composition, parameter defaults at the medians the model proposed, and an
/// <c>attack</c> phase entered when the army reaches the median first-launch army value, no earlier than the 25th
/// percentile first-launch time. Reads only <c>decisions/&lt;match&gt;.ndjson</c> and <c>.match.json</c>; no model
/// call, no clock, sorted inputs, so the same logs give the same bytes.
/// </summary>
public static partial class PlaybookInducer
{
    [GeneratedRegex(@"squads: attacking r\d+ with army value (\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex LaunchNote();

    private const string BuildPhase = "build";
    private const string AttackPhase = "attack";

    private static readonly JsonSerializerOptions Indented = new(BotJson.Options) { WriteIndented = true };

    private sealed record Proposal(string Playbook, IReadOnlyDictionary<string, double> Parameters);

    private sealed record MatchData(string File, string Sha, Faction Faction, bool Won, string Split, IReadOnlyList<Proposal> Proposals, double? LaunchSeconds, double? LaunchArmy);

    private sealed class Cluster(Faction faction, string basePlaybook)
    {
        public Faction Faction { get; } = faction;
        public string Base { get; } = basePlaybook;
        public List<(MatchData Match, Proposal Proposal)> Won { get; } = [];
        public int AllMatches { get; set; }
        public int Wins { get; set; }
    }

    public static InductionResult Induce(IReadOnlyList<string> dirs, string arm, string split, int minSupport, IPlaybookLibrary library)
    {
        ArgumentNullException.ThrowIfNull(dirs);
        ArgumentNullException.ThrowIfNull(library);
        List<string> warnings = [];
        if (split is "heldout" or "all")
        {
            warnings.Add($"warning: --split {split} reads held-out matches; an induced playbook must not be tested on the data it was induced from.");
        }

        List<MatchData> matches = Load(dirs, arm, split, warnings, out int manifestCount, out int armCount, out int duplicates);
        int won = matches.Count(static m => m.Won);

        // Clusters over every match (for the win denominator); only won matches' proposals support one.
        SortedDictionary<(Faction, string), Cluster> clusters = [];
        foreach (MatchData match in matches)
        {
            foreach (string playbook in match.Proposals.Select(static p => p.Playbook).Distinct(StringComparer.Ordinal))
            {
                if (!clusters.TryGetValue((match.Faction, playbook), out Cluster? cluster)) clusters[(match.Faction, playbook)] = cluster = new Cluster(match.Faction, playbook);
                cluster.AllMatches++;
                if (match.Won) cluster.Wins++;
            }
            if (!match.Won) continue;
            foreach (Proposal proposal in match.Proposals) clusters[(match.Faction, proposal.Playbook)].Won.Add((match, proposal));
        }

        StringBuilder report = new();
        report.AppendLine("# Playbook induction");
        report.AppendLine();
        foreach (string w in warnings) report.AppendLine($"> {w}").AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"- arm `{arm}`, split `{split}`, minimum supporting proposals {minSupport}");
        report.AppendLine(CultureInfo.InvariantCulture, $"- match manifests read {manifestCount}; of the arm {armCount}; in the split {matches.Count + duplicates} ({duplicates} duplicate log(s) dropped); won {won}");
        report.AppendLine();

        List<Playbook> induced = [];
        StringBuilder skipped = new();
        foreach (Cluster cluster in clusters.Values)
        {
            string label = $"{cluster.Faction} / {cluster.Base}";
            int supportMatches = cluster.Won.Select(static w => w.Match.Sha).Distinct(StringComparer.Ordinal).Count();
            if (cluster.Won.Count < minSupport)
            {
                if (cluster.Won.Count > 0) skipped.AppendLine(CultureInfo.InvariantCulture, $"- {label}: {cluster.Won.Count} supporting proposals in {supportMatches} won matches, below {minSupport}");
                continue;
            }
            if (!library.TryGet(cluster.Base, out Playbook basis))
            {
                skipped.AppendLine($"- {label}: the base playbook is not in the library (pass the file that defines it with --playbooks)");
                continue;
            }
            List<MatchData> supporting = [.. cluster.Won.Select(static w => w.Match).DistinctBy(static m => m.Sha).OrderBy(static m => m.Sha, StringComparer.Ordinal)];
            double[] launchTimes = [.. supporting.Where(static m => m.LaunchSeconds is not null).Select(static m => m.LaunchSeconds!.Value).Order()];
            double[] launchArmy = [.. supporting.Where(static m => m.LaunchArmy is not null).Select(static m => m.LaunchArmy!.Value).Order()];
            if (launchArmy.Length == 0)
            {
                skipped.AppendLine($"- {label}: {cluster.Won.Count} supporting proposals, but none of its {supportMatches} won matches launched an attack, so there is no attack phase to induce");
                continue;
            }

            double armyMedian = Round(Quantile(launchArmy, 0.5), 2);
            double timeP25 = Round(Quantile(launchTimes, 0.25), 2);
            string id = $"induced-{cluster.Base}-{cluster.Faction.ToString().ToLowerInvariant()}-{ShortHash(arm, split, minSupport, cluster, supporting)}";
            (Playbook playbook, string parameterReport) = Build(basis, id, cluster, supporting.Count, arm, split, armyMedian, timeP25);
            induced.Add(playbook);

            report.AppendLine(CultureInfo.InvariantCulture, $"## `{id}`");
            report.AppendLine();
            report.AppendLine(CultureInfo.InvariantCulture, $"- cluster: {cluster.Faction}, base `{cluster.Base}`");
            report.AppendLine(CultureInfo.InvariantCulture, $"- supporting: {cluster.Won.Count} proposals in {supportMatches} won matches; the arm chose this base in {cluster.AllMatches} matches of the split, won {cluster.Wins} of them");
            report.AppendLine(CultureInfo.InvariantCulture, $"- first launch time (s), {launchTimes.Length}/{supportMatches} matches launched: {Distribution(launchTimes)}");
            report.AppendLine(CultureInfo.InvariantCulture, $"- first launch army value, {launchArmy.Length}/{supportMatches} matches launched: {Distribution(launchArmy)}");
            report.AppendLine(CultureInfo.InvariantCulture, $"- attack phase enters at OwnArmyValue >= {Fmt(armyMedian)} and GameSeconds >= {Fmt(timeP25)}");
            report.Append(parameterReport);
            report.AppendLine("- source logs (SHA-256):");
            foreach (MatchData m in supporting.OrderBy(static m => m.File, StringComparer.Ordinal)) report.AppendLine($"  - `{m.File}` {m.Sha}");
            report.AppendLine();
        }
        if (induced.Count == 0) report.AppendLine("No cluster reached the threshold; no playbook induced.").AppendLine();
        if (skipped.Length > 0) report.AppendLine("## Clusters not induced").AppendLine().Append(skipped);

        induced = [.. induced.OrderBy(static p => p.Id, StringComparer.Ordinal)];
        string json = JsonSerializer.Serialize(new PlaybookDocument(induced), Indented) + "\n";
        return new InductionResult(induced, json, report.ToString(), warnings);
    }

    /// <summary>Writes <paramref name="result"/>'s playbooks and (when a path is given) report, after printing its warnings.</summary>
    public static int Run(InduceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        PlaybookLibrary library = Program.MergePlaybookFiles(PlaybookLibrary.LoadDefault(), options.PlaybookFiles);
        InductionResult result = Induce(options.From, options.Arm, options.Split, options.MinSupport, library);
        foreach (string warning in result.Warnings) Console.Error.WriteLine(warning);
        File.WriteAllText(options.OutPath, result.Json);
        if (options.ReportPath is not null) File.WriteAllText(options.ReportPath, result.Report);
        Console.WriteLine($"induced {result.Playbooks.Count} playbook(s): {string.Join(", ", result.Playbooks.Select(static p => p.Id))}");
        return 0;
    }

    private static (Playbook Playbook, string ParameterReport) Build(Playbook basis, string id, Cluster cluster, int matches, string arm, string split, double armyMedian, double timeP25)
    {
        StringBuilder report = new();
        List<PlaybookParameter> parameters = [];
        foreach (PlaybookParameter parameter in basis.Parameters)
        {
            double[] proposed = [.. cluster.Won.Select(w => w.Proposal.Parameters.TryGetValue(parameter.Name, out double v) ? (double?)v : null).Where(static v => v is not null).Select(static v => v!.Value).Order()];
            if (proposed.Length == 0)
            {
                parameters.Add(parameter);
                report.AppendLine(CultureInfo.InvariantCulture, $"- parameter `{parameter.Name}`: never proposed, base default {Fmt(parameter.Default)} kept");
                continue;
            }
            double median = Quantile(proposed, 0.5);
            double clamped = Round(Math.Clamp(median, parameter.Min, parameter.Max), 4);
            parameters.Add(parameter with { Default = clamped });
            report.AppendLine(CultureInfo.InvariantCulture, $"- parameter `{parameter.Name}` [{Fmt(parameter.Min)}, {Fmt(parameter.Max)}], {proposed.Length} proposals: {Distribution(proposed)}; default {Fmt(clamped)}{(clamped != Round(median, 4) ? " (median clamped to the base's range)" : string.Empty)}");
        }
        HashSet<string> declared = [.. basis.Parameters.Select(static p => p.Name)];
        string[] extra = [.. cluster.Won.SelectMany(static w => w.Proposal.Parameters.Keys).Where(n => !declared.Contains(n)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        if (extra.Length > 0) report.AppendLine($"- proposed but not declared by the base, dropped: {string.Join(", ", extra.Select(static n => $"`{n}`"))}");

        // The base's attack conditions with the OwnArmyValue lower bound at the observed launch army; a base
        // without one gets it added, since the phase exists to gate the attack on the army the model attacked with.
        List<Condition> attack = [.. basis.AttackConditions];
        bool bounded = false;
        for (int i = 0; i < attack.Count; i++)
        {
            if (attack[i].Metric == ConditionMetric.OwnArmyValue && attack[i].Op is Comparison.Ge or Comparison.Gt)
            {
                attack[i] = attack[i] with { Threshold = armyMedian };
                bounded = true;
            }
        }
        if (!bounded) attack.Add(new Condition(ConditionMetric.OwnArmyValue, Comparison.Ge, armyMedian));

        Playbook playbook = basis with
        {
            Id = id,
            Description = string.Create(CultureInfo.InvariantCulture,
                $"Induced from {basis.Id}: {cluster.Won.Count} proposals in {matches} won matches of {arm} ({split}); attacks at army value {Fmt(armyMedian)} from {Fmt(timeP25)} s."),
            Factions = [cluster.Faction],
            Parameters = parameters,
            Phases =
            [
                new PlaybookPhase(BuildPhase, []),
                new PlaybookPhase(AttackPhase,
                    [new Condition(ConditionMetric.OwnArmyValue, Comparison.Ge, armyMedian), new Condition(ConditionMetric.GameSeconds, Comparison.Ge, timeP25)],
                    AttackConditions: attack),
            ],
        };
        return (playbook, report.ToString());
    }

    private static List<MatchData> Load(IReadOnlyList<string> dirs, string arm, string split, List<string> warnings, out int manifestCount, out int armCount, out int duplicates)
    {
        List<string> manifests = [];
        foreach (string dir in dirs)
        {
            if (!Directory.Exists(dir)) throw new ArgumentException($"No directory at {dir}.");
            manifests.AddRange(Directory.EnumerateFiles(dir, "*.match.json", SearchOption.AllDirectories));
        }
        manifests.Sort(StringComparer.Ordinal);
        manifestCount = manifests.Count;
        armCount = 0;
        duplicates = 0;
        Dictionary<string, MatchData> bySha = new(StringComparer.Ordinal);
        foreach (string path in manifests)
        {
            MatchManifest manifest;
            try
            {
                manifest = MatchManifest.Load(path);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException)
            {
                warnings.Add($"warning: {path} skipped: {ex.Message}");
                continue;
            }
            if (!string.Equals(manifest.Arm.ToString(), arm, StringComparison.Ordinal)) continue;
            armCount++;
            if (split != "all" && !string.Equals(manifest.Split, split, StringComparison.Ordinal)) continue;
            string log = path[..^".match.json".Length] + ".ndjson";
            if (!File.Exists(log))
            {
                warnings.Add($"warning: {path} skipped: no decision log {log}.");
                continue;
            }
            byte[] bytes = File.ReadAllBytes(log);
            string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (bySha.ContainsKey(sha))
            {
                duplicates++;
                continue;
            }
            Faction faction = manifest.Record?.Players.TryGetValue("arm", out PlayerMatchMetrics? metrics) == true ? metrics.Faction : MatchRunner.ArmFaction(manifest.Seed);
            (List<Proposal> proposals, double? seconds, double? army) = Read(bytes);
            bySha[sha] = new MatchData(Path.GetFileName(log), sha, faction, manifest.Winner == 0, manifest.Split, proposals, seconds, army);
        }
        return [.. bySha.Values.OrderBy(static m => m.Sha, StringComparer.Ordinal)];
    }

    /// <summary>The Primary LLM proposals and the first attack launch (game seconds, army value) of one decision log.</summary>
    private static (List<Proposal> Proposals, double? LaunchSeconds, double? LaunchArmy) Read(byte[] bytes)
    {
        List<Proposal> proposals = [];
        double? seconds = null, army = null;
        foreach (string line in Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            bool isProposal = line.Contains("\"strategy.proposal\"", StringComparison.Ordinal);
            bool isPlan = seconds is null && line.Contains("\"operations.plan\"", StringComparison.Ordinal) && line.Contains("squads: attacking", StringComparison.Ordinal);
            if (!isProposal && !isPlan) continue;
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            JsonElement data = root.GetProperty("data");
            if (isProposal)
            {
                if (data.GetProperty("role").GetString() != "Primary" || !data.TryGetProperty("intent", out JsonElement intent) || intent.ValueKind != JsonValueKind.Object) continue;
                if (intent.GetProperty("source").GetString() != "Llm") continue;
                string? playbook = intent.GetProperty("playbookId").GetString();
                if (string.IsNullOrEmpty(playbook)) continue;
                SortedDictionary<string, double> parameters = new(StringComparer.Ordinal);
                if (intent.TryGetProperty("playbookParameters", out JsonElement p) && p.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty property in p.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.Number) parameters[property.Name] = property.Value.GetDouble();
                    }
                }
                proposals.Add(new Proposal(playbook, parameters));
            }
            else if (data.TryGetProperty("notes", out JsonElement notes) && notes.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement note in notes.EnumerateArray())
                {
                    Match match = LaunchNote().Match(note.GetString() ?? string.Empty);
                    if (!match.Success) continue;
                    seconds = root.GetProperty("frame").GetInt64() / (double)GameTime.FramesPerSecond;
                    army = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    break;
                }
            }
        }
        return (proposals, seconds, army);
    }

    private static string ShortHash(string arm, string split, int minSupport, Cluster cluster, IReadOnlyList<MatchData> supporting)
    {
        string input = string.Join('\n', [$"arm={arm}", $"split={split}", $"min={minSupport}", $"cluster={cluster.Faction}/{cluster.Base}", .. supporting.Select(static m => m.Sha)]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..8].ToLowerInvariant();
    }

    /// <summary>Linear-interpolated quantile of an ascending array (the numpy default).</summary>
    internal static double Quantile(double[] sorted, double q)
    {
        if (sorted.Length == 1) return sorted[0];
        double position = q * (sorted.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = Math.Min(lower + 1, sorted.Length - 1);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static string Distribution(double[] sorted) =>
        $"median {Fmt(Quantile(sorted, 0.5))}, IQR {Fmt(Quantile(sorted, 0.25))} to {Fmt(Quantile(sorted, 0.75))}, range {Fmt(sorted[0])} to {Fmt(sorted[^1])}";

    private static double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.AwayFromZero);

    private static string Fmt(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
