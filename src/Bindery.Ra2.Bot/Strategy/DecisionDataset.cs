// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace Bindery.Ra2.Bot.Strategy;

/// <summary>
/// One supervised example: the encoded features a strategist saw and the playbook
/// whose intent the runtime activated.
/// </summary>
/// <param name="Mode">
/// Whether the decision was made on belief (fog) or oracle (full-map) frames. Oracle decisions saw what a
/// fog-respecting bot cannot, so consumers keep them apart; a line written before the field existed reads as
/// <see cref="ObservationMode.Belief"/>.
/// </param>
public sealed record DecisionExample(
    string FeatureVersion,
    IReadOnlyList<double> Features,
    Faction Faction,
    string PlaybookId,
    StrategicPosture Posture,
    IntentSource Source,
    string Role,
    bool Renewal,
    long Frame,
    long SnapshotVersion,
    string? MatchId = null,
    ObservationMode Mode = ObservationMode.Belief);

/// <summary>Which activation records become examples.</summary>
/// <param name="Roles">Arbiter roles to keep (<c>Primary</c>, <c>Fallback</c>, <c>Emergency</c>); null keeps all.</param>
/// <param name="Sources">Intent sources to keep; null keeps all.</param>
/// <param name="IncludeRenewals">Whether renewals (the same playbook re-affirmed on a later snapshot) are examples.</param>
/// <param name="IncludeShadow">
/// Whether a shadow strategist's validated answers (<see cref="DecisionRecordKinds.ShadowProposal"/> records, role
/// <c>Shadow</c>) are examples too. They never took effect, but they are what the shadow strategist chose for the
/// features it was asked with, which is what distilling it needs; an answer the validator rejected is not an
/// example. Subject to <paramref name="Roles"/> like every other record.
/// </param>
public sealed record DatasetFilter(
    IReadOnlySet<string>? Roles = null,
    IReadOnlySet<IntentSource>? Sources = null,
    bool IncludeRenewals = true,
    bool IncludeShadow = false)
{
    /// <summary>Primary-slot decisions only: what the configured strategist chose, not placeholders.</summary>
    public static DatasetFilter PrimaryOnly { get; } = new(new HashSet<string>(StringComparer.Ordinal) { "Primary" });

    /// <summary>Shadow decisions only: what a shadow strategist (an LLM run alongside the selector) would have chosen.</summary>
    public static DatasetFilter ShadowOnly { get; } = new(new HashSet<string>(StringComparer.Ordinal) { "Shadow" }, IncludeShadow: true);
}

/// <summary>
/// Decision examples exported from decision logs, for distillation. The source is the
/// <see cref="DecisionRecordKinds.IntentActivated"/> record, whose payload the runtime
/// writes with (at least) these fields — the contract this type reads:
/// <list type="bullet">
/// <item><c>featureVersion</c>: <see cref="FeatureVector.Version"/> of the vector below.</item>
/// <item><c>features</c>: <see cref="FeatureVector.Encode"/> of the features the strategist saw (was asked with),
/// whose snapshot version and frame are <c>featuresSnapshotVersion</c> and <c>featuresFrame</c> when present (the
/// example's <see cref="DecisionExample.SnapshotVersion"/> and <see cref="DecisionExample.Frame"/>; older logs fall
/// back to the record's own, the activation's).</item>
/// <item><c>faction</c>: the player's faction.</item>
/// <item><c>intent</c>: the activated intent in canonical <c>IntentJson</c> form (playbook, posture, source, ...).</item>
/// <item><c>role</c>, <c>renewal</c>: arbiter slot and whether the activation renewed the incumbent.</item>
/// <item><c>mode</c> (optional): the <see cref="ObservationMode"/> of the features; when absent, the mode the
/// caller passes to <see cref="FromDecisionLog"/> (the mode the run was played in).</item>
/// </list>
/// With <see cref="DatasetFilter.IncludeShadow"/>, <see cref="DecisionRecordKinds.ShadowProposal"/> records carry
/// the same fields (and <c>accepted</c>, <c>wouldBe</c>); a shadow example is a renewal when the arbiter would have
/// renewed it. Records without a vector of the current version are skipped (and counted), never guessed.
/// </summary>
public sealed class DecisionDataset
{
    public const string FeatureVersionField = "featureVersion";
    public const string FeaturesField = "features";
    public const string FactionField = "faction";
    public const string IntentField = "intent";
    public const string RoleField = "role";
    public const string RenewalField = "renewal";
    public const string ModeField = "mode";
    public const string FeaturesSnapshotVersionField = "featuresSnapshotVersion";
    public const string FeaturesFrameField = "featuresFrame";

    public DecisionDataset(IReadOnlyList<DecisionExample> examples, int skipped = 0)
    {
        ArgumentNullException.ThrowIfNull(examples);
        Examples = examples;
        Skipped = skipped;
    }

    public IReadOnlyList<DecisionExample> Examples { get; }

    /// <summary>Activation records that matched the filter but could not be read as examples.</summary>
    public int Skipped { get; }

    public int Count => Examples.Count;

    /// <summary>Whether any example was decided on oracle frames (a diagnostic run's data, not a belief arm's).</summary>
    public bool HasOracleExamples => Examples.Any(static e => e.Mode == ObservationMode.Oracle);

    public static DecisionDataset Empty { get; } = new([]);

    /// <summary>Builds examples from one match's decision log.</summary>
    /// <param name="mode">The mode the match was played in, for records that do not carry a <c>mode</c> field.</param>
    public static DecisionDataset FromDecisionLog(IEnumerable<DecisionRecord> records, DatasetFilter? filter = null, string? matchId = null,
        ObservationMode mode = ObservationMode.Belief)
    {
        ArgumentNullException.ThrowIfNull(records);
        filter ??= new DatasetFilter();
        List<DecisionExample> examples = [];
        int skipped = 0;
        foreach (DecisionRecord record in records)
        {
            bool shadow = string.Equals(record.Kind, DecisionRecordKinds.ShadowProposal, StringComparison.Ordinal);
            if (!(shadow && filter.IncludeShadow) && !string.Equals(record.Kind, DecisionRecordKinds.IntentActivated, StringComparison.Ordinal)) continue;
            JsonElement data = record.Data;
            if (data.ValueKind != JsonValueKind.Object) { skipped++; continue; }

            string role = data.TryGetProperty(RoleField, out JsonElement r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : string.Empty;
            bool renewal = shadow
                ? data.TryGetProperty("wouldBe", out JsonElement wb) && wb.ValueKind == JsonValueKind.String && wb.GetString() == nameof(Arbitration.ArbitrationOutcome.Renewed)
                : data.TryGetProperty(RenewalField, out JsonElement rn) && rn.ValueKind == JsonValueKind.True;
            if (shadow && !(data.TryGetProperty("accepted", out JsonElement accepted) && accepted.ValueKind == JsonValueKind.True)) continue;
            if (filter.Roles is not null && !filter.Roles.Contains(role)) continue;
            if (renewal && !filter.IncludeRenewals) continue;

            if (!TryRead(data, out string version, out double[] vector, out Faction faction, out StrategicIntent? intent)
                || !string.Equals(version, FeatureVector.Version, StringComparison.Ordinal)
                || vector.Length != FeatureVector.Dimension)
            {
                skipped++;
                continue;
            }
            if (filter.Sources is not null && !filter.Sources.Contains(intent!.Source)) continue;
            ObservationMode recordMode = mode;
            if (data.TryGetProperty(ModeField, out JsonElement m))
            {
                if (m.ValueKind != JsonValueKind.String || !Enum.TryParse(m.GetString(), ignoreCase: false, out recordMode))
                {
                    skipped++;
                    continue;
                }
            }

            long frame = data.TryGetProperty(FeaturesFrameField, out JsonElement ff) && ff.ValueKind == JsonValueKind.Number ? ff.GetInt64() : record.Time.Frame;
            long snapshotVersion = data.TryGetProperty(FeaturesSnapshotVersionField, out JsonElement fv) && fv.ValueKind == JsonValueKind.Number
                ? fv.GetInt64()
                : record.SnapshotVersion;
            examples.Add(new DecisionExample(
                version, vector, faction, intent!.PlaybookId, intent.Posture, intent.Source, role, renewal,
                frame, snapshotVersion, matchId, recordMode));
        }
        return new DecisionDataset(examples, skipped);
    }

    public static DecisionDataset Merge(IEnumerable<DecisionDataset> datasets)
    {
        ArgumentNullException.ThrowIfNull(datasets);
        List<DecisionExample> all = [];
        int skipped = 0;
        foreach (DecisionDataset d in datasets)
        {
            all.AddRange(d.Examples);
            skipped += d.Skipped;
        }
        return new DecisionDataset(all, skipped);
    }

    /// <summary>One example per line, <see cref="BotJson.Options"/>.</summary>
    public void WriteNdjson(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        foreach (DecisionExample example in Examples)
        {
            writer.Write(JsonSerializer.Serialize(example, BotJson.Options));
            writer.Write('\n');
        }
    }

    public static DecisionDataset ReadNdjson(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        List<DecisionExample> examples = [];
        string? line;
        int lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            DecisionExample example = JsonSerializer.Deserialize<DecisionExample>(line, BotJson.Options)
                ?? throw new FormatException($"Dataset line {lineNumber} is null.");
            examples.Add(example);
        }
        return new DecisionDataset(examples);
    }

    private static bool TryRead(JsonElement data, out string version, out double[] vector, out Faction faction, out StrategicIntent? intent)
    {
        version = string.Empty;
        vector = [];
        faction = default;
        intent = null;
        if (!data.TryGetProperty(FeatureVersionField, out JsonElement v) || v.ValueKind != JsonValueKind.String) return false;
        if (!data.TryGetProperty(FeaturesField, out JsonElement f) || f.ValueKind != JsonValueKind.Array) return false;
        if (!data.TryGetProperty(FactionField, out JsonElement fa) || fa.ValueKind != JsonValueKind.String
            || !Enum.TryParse(fa.GetString(), ignoreCase: false, out faction)) return false;
        if (!data.TryGetProperty(IntentField, out JsonElement i) || i.ValueKind != JsonValueKind.Object) return false;
        version = v.GetString()!;
        List<double> values = [];
        foreach (JsonElement item in f.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number) return false;
            values.Add(item.GetDouble());
        }
        vector = [.. values];
        try
        {
            intent = i.Deserialize<StrategicIntent>(BotJson.Options);
        }
        catch (JsonException)
        {
            return false;
        }
        return intent is not null;
    }
}
