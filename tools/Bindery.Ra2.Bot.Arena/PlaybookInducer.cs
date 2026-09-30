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
    public const int DefaultMinSupport = 5;

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
/// <param name="Note">Printed by the CLI when no cluster reached <c>--min-support</c>: the largest cluster's support, so the user can see what value would induce something.</param>
public sealed record InductionResult(IReadOnlyList<Playbook> Playbooks, string Json, string Report, IReadOnlyList<string> Warnings, string? Note = null);

/// <summary>
/// Tier 2 of the LLM-to-playbook pipeline: compiles an LLM arm's captured decisions into playbooks. For every
/// (faction, chosen base playbook) cluster with enough proposals from won matches it emits one two-phase playbook:
/// the base's posture, budget and composition, parameter defaults at the median over matches of each match's median
/// proposed value (every supporting match weighs the same), and an
/// <c>attack</c> phase entered when the army reaches the median first-launch army value, no earlier than the 25th
/// percentile first-launch time. Reads only <c>decisions/&lt;match&gt;.ndjson</c> and <c>.match.json</c>; no model
/// call, no clock, sorted inputs, so the same logs give the same bytes. Support is counted in matches (a match
/// counts once however often it proposed a choice); only proposals the arbiter adopted (a
/// <c>strategy.intent_activated</c> record) count, so ones rejected in validation, discarded late or refused by
/// arbitration are not. A launch is credited only to the cluster whose playbook was active when it happened. The data is conditioned on won matches: selection on outcome, not evidence of causation.
/// </summary>
public static partial class PlaybookInducer
{
    [GeneratedRegex(@"squads: attacking r\d+ with army value (\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex LaunchNote();

    private const string BuildPhase = "build";
    private const string AttackPhase = "attack";

    private static readonly JsonSerializerOptions Indented = new(BotJson.Options) { WriteIndented = true };

    private sealed record Proposal(string Playbook, IReadOnlyDictionary<string, double> Parameters, string? IntentId);

    private sealed record Launch(double Seconds, double Army);

    /// <summary><paramref name="Launches"/>: per playbook id, the match's first attack launch (rising edge) under an adopted LLM intent of that playbook.</summary>
    private sealed record MatchData(string File, string Sha, Faction Faction, bool Won, string Split, IReadOnlyList<Proposal> Proposals, IReadOnlyDictionary<string, Launch> Launches);

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
        report.AppendLine("> Selection on outcome: only proposals from matches the arm won are used, so this is conditioned on winning and shows what the model did when it won, not what made it win.").AppendLine();
        foreach (string w in warnings) report.AppendLine($"> {w}").AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"- arm `{arm}`, split `{split}`, minimum supporting won matches {minSupport}");
        report.AppendLine(CultureInfo.InvariantCulture, $"- match manifests read {manifestCount}; of the arm {armCount}; in the split {matches.Count + duplicates} ({duplicates} duplicate log(s) dropped); won {won}");
        report.AppendLine();

        List<Playbook> induced = [];
        StringBuilder skipped = new();
        foreach (Cluster cluster in clusters.Values)
        {
            string label = $"{cluster.Faction} / {cluster.Base}";
            int supportMatches = cluster.Won.Select(static w => w.Match.Sha).Distinct(StringComparer.Ordinal).Count();
            if (supportMatches < minSupport)
            {
                if (supportMatches > 0) skipped.AppendLine(CultureInfo.InvariantCulture, $"- {label}: {supportMatches} supporting won matches ({cluster.Won.Count} proposals), below {minSupport}");
                continue;
            }
            if (!library.TryGet(cluster.Base, out Playbook basis))
            {
                skipped.AppendLine($"- {label}: the base playbook is not in the library (pass the file that defines it with --playbooks)");
                continue;
            }
            List<MatchData> supporting = [.. cluster.Won.Select(static w => w.Match).DistinctBy(static m => m.Sha).OrderBy(static m => m.Sha, StringComparer.Ordinal)];
            List<Launch> launches = [.. supporting.Select(m => m.Launches.GetValueOrDefault(cluster.Base)).Where(static l => l is not null).Select(static l => l!)];
            double[] launchTimes = [.. launches.Select(static l => l.Seconds).Order()];
            double[] launchArmy = [.. launches.Select(static l => l.Army).Order()];
            if (launchArmy.Length == 0)
            {
                skipped.AppendLine($"- {label}: {supportMatches} supporting won matches, but none of them launched an attack, so there is no attack phase to induce");
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
            report.AppendLine(CultureInfo.InvariantCulture, $"- first launch under this playbook, time (s), {launchTimes.Length}/{supportMatches} matches launched: {Distribution(launchTimes)}");
            report.AppendLine(CultureInfo.InvariantCulture, $"- first launch under this playbook, army value, {launchArmy.Length}/{supportMatches} matches launched: {Distribution(launchArmy)}");
            report.AppendLine(CultureInfo.InvariantCulture, $"- attack phase enters at OwnArmyValue >= {Fmt(armyMedian)} and GameSeconds >= {Fmt(timeP25)}; the playbook's top-level attack conditions (in force in every phase) carry the same army and time bounds");
            report.Append(parameterReport);
            report.AppendLine("- source logs (SHA-256):");
            foreach (MatchData m in supporting.OrderBy(static m => m.File, StringComparer.Ordinal)) report.AppendLine($"  - `{m.File}` {m.Sha}");
            report.AppendLine();
        }
        string? note = null;
        if (induced.Count == 0) report.AppendLine("No cluster reached the threshold; no playbook induced.").AppendLine();
        if (clusters.Values.Select(c => (Cluster: c, Support: c.Won.Select(static w => w.Match.Sha).Distinct(StringComparer.Ordinal).Count())).Where(static x => x.Support > 0).OrderByDescending(static x => x.Support).FirstOrDefault() is { Cluster: { } largest, Support: var largestSupport }
            && largestSupport < minSupport)
        {
            note = $"no cluster reached --min-support {minSupport}; the largest, {largest.Faction} / {largest.Base}, has {largestSupport} supporting won matches (--min-support {largestSupport} would induce it).";
            report.AppendLine($"Largest cluster: {largest.Faction} / {largest.Base}, {largestSupport} supporting won matches.").AppendLine();
        }
        if (skipped.Length > 0) report.AppendLine("## Clusters not induced").AppendLine().Append(skipped);

        induced = [.. induced.OrderBy(static p => p.Id, StringComparer.Ordinal)];
        string json = JsonSerializer.Serialize(new PlaybookDocument(induced), Indented) + "\n";
        return new InductionResult(induced, json, report.ToString(), warnings, note);
    }

    /// <summary>Writes <paramref name="result"/>'s playbooks and (when a path is given) report, after printing its warnings.</summary>
    public static int Run(InduceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        PlaybookLibrary library = Program.MergePlaybookFiles(PlaybookLibrary.LoadDefault(), options.PlaybookFiles);
        InductionResult result = Induce(options.From, options.Arm, options.Split, options.MinSupport, library);
        foreach (string warning in result.Warnings) Console.Error.WriteLine(warning);
        if (result.Note is not null) Console.Error.WriteLine(result.Note);
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
            // Each supporting match weighs the same: its median proposed value first, then the median over matches.
            double[] proposed = [.. cluster.Won.GroupBy(static w => w.Match.Sha, StringComparer.Ordinal).OrderBy(static g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Select(w => w.Proposal.Parameters.TryGetValue(parameter.Name, out double v) ? (double?)v : null).Where(static v => v is not null).Select(static v => v!.Value).Order().ToArray())
                .Where(static values => values.Length > 0).Select(static values => Quantile(values, 0.5)).Order()];
            int proposals = cluster.Won.Count(w => w.Proposal.Parameters.ContainsKey(parameter.Name));
            if (proposed.Length == 0)
            {
                parameters.Add(parameter);
                report.AppendLine(CultureInfo.InvariantCulture, $"- parameter `{parameter.Name}`: never proposed, base default {Fmt(parameter.Default)} kept");
                continue;
            }
            double median = Quantile(proposed, 0.5);
            double clamped = Round(Math.Clamp(median, parameter.Min, parameter.Max), 4);
            parameters.Add(parameter with { Default = clamped });
            report.AppendLine(CultureInfo.InvariantCulture, $"- parameter `{parameter.Name}` [{Fmt(parameter.Min)}, {Fmt(parameter.Max)}], {proposed.Length} matches ({proposals} proposals), per-match medians: {Distribution(proposed)}; default {Fmt(clamped)}{(clamped != Round(median, 4) ? " (median clamped to the base's range)" : string.Empty)}");
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
        // The first-quartile launch time is a floor on attacking too. Emitted at the top level, so it holds in the
        // build phase (which overrides nothing) as well as after the attack phase is entered.
        attack.RemoveAll(static c => c.Metric == ConditionMetric.GameSeconds && c.Op is Comparison.Ge or Comparison.Gt);
        attack.Add(new Condition(ConditionMetric.GameSeconds, Comparison.Ge, timeP25));

        Playbook playbook = basis with
        {
            Id = id,
            Description = string.Create(CultureInfo.InvariantCulture,
                $"Induced from {basis.Id}: {cluster.Won.Count} proposals in {matches} won matches of {arm} ({split}); attacks (in every phase) at army value {Fmt(armyMedian)} from {Fmt(timeP25)} s."),
            Factions = [cluster.Faction],
            Parameters = parameters,
            AttackConditions = attack,
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
            if (!InSplit(manifest, split)) continue;
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
            (List<Proposal> proposals, Dictionary<string, Launch> launches) = Read(bytes);
            bySha[sha] = new MatchData(Path.GetFileName(log), sha, faction, manifest.Winner == 0, manifest.Split, proposals, launches);
        }
        return [.. bySha.Values.OrderBy(static m => m.Sha, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The split as the dataset export and the report define it: training is a training map against a training
    /// opponent; heldout is a held-out map or a held-out opponent (either leaves the training set).
    /// </summary>
    private static bool InSplit(MatchManifest manifest, string split)
    {
        bool heldOutOpponent = OpponentSets.IsHeldOut(manifest.Opponent);
        return split switch
        {
            "all" => true,
            "training" => string.Equals(manifest.Split, "training", StringComparison.Ordinal) && !heldOutOpponent,
            _ => !string.Equals(manifest.Split, "training", StringComparison.Ordinal) || heldOutOpponent,
        };
    }

    /// <summary>
    /// The Primary LLM intents the arbiter adopted (<c>strategy.intent_activated</c>, renewals included) and, per
    /// playbook, the first attack launch (game seconds, army value) under that playbook. A launch is the rising
    /// edge of attacking: an <c>operations.plan</c> with a <c>squads: attacking</c> note whose previous plan had
    /// none (the note repeats on every plan tick of an ongoing attack, so a playbook adopted mid-attack launches
    /// nothing). It is credited through the plan's own <c>intentId</c> to the playbook of that intent, provided the
    /// intent was adopted as a Primary LLM one. A proposal the validator rejected, the scheduler discarded or the
    /// arbiter refused has no activation record and never counts.
    /// </summary>
    private static (List<Proposal> Proposals, Dictionary<string, Launch> Launches) Read(byte[] bytes)
    {
        List<Proposal> proposals = [];
        HashSet<string> dropped = new(StringComparer.Ordinal);
        Dictionary<string, Launch> launches = new(StringComparer.Ordinal);
        Dictionary<string, string> adopted = new(StringComparer.Ordinal);   // intentId -> playbook, adopted Primary LLM intents only
        bool wasAttacking = false;
        foreach (string line in Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            bool isActivation = line.Contains("\"strategy.intent_activated\"", StringComparison.Ordinal);
            bool isPlan = line.Contains("\"operations.plan\"", StringComparison.Ordinal);
            bool isRejection = line.Contains("\"strategy.validation\"", StringComparison.Ordinal) && line.Contains("\"accepted\":false", StringComparison.Ordinal);
            bool isLate = line.Contains("\"strategy.late_discarded\"", StringComparison.Ordinal);
            if (!isActivation && !isPlan && !isRejection && !isLate) continue;
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            JsonElement data = root.GetProperty("data");
            if (isRejection || isLate)
            {
                // Rejected in validation or discarded late by the scheduler: never took effect, so no support.
                if (data.TryGetProperty("intentId", out JsonElement dropId) && dropId.GetString() is { } droppedId) dropped.Add(droppedId);
            }
            else if (isActivation)
            {
                // A Selector or Fallback activation (a renewal of the same playbook included, IntentArbiter.Renew) is not
                // recorded here, so its plans are credited to nobody: the selector's parameters apply then, not the model's.
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
                string? intentId = data.TryGetProperty("intentId", out JsonElement activated) ? activated.GetString() : null;
                proposals.Add(new Proposal(playbook, parameters, intentId));
                if (intentId is not null) adopted[intentId] = playbook;
            }
            else
            {
                Match? attacking = null;
                if (data.TryGetProperty("notes", out JsonElement notes) && notes.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement note in notes.EnumerateArray())
                    {
                        Match match = LaunchNote().Match(note.GetString() ?? string.Empty);
                        if (!match.Success) continue;
                        attacking = match;
                        break;
                    }
                }
                bool rising = attacking is not null && !wasAttacking;
                wasAttacking = attacking is not null;
                if (!rising || !data.TryGetProperty("intentId", out JsonElement planIntent) || planIntent.GetString() is not { } planIntentId) continue;
                if (!adopted.TryGetValue(planIntentId, out string? launchPlaybook) || launches.ContainsKey(launchPlaybook)) continue;
                launches[launchPlaybook] = new Launch(root.GetProperty("frame").GetInt64() / (double)GameTime.FramesPerSecond, double.Parse(attacking!.Groups[1].Value, CultureInfo.InvariantCulture));
            }
        }
        proposals.RemoveAll(p => p.IntentId is not null && dropped.Contains(p.IntentId));
        return (proposals, launches);
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
