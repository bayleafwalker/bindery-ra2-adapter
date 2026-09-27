// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Bindery.Ra2.Bot.Strategy;

namespace Bindery.Ra2.Bot.Claude;

/// <summary>
/// What the LLM may decide, as named tiers of <see cref="StrategicIntent"/> fields (build step 6: widen the
/// vocabulary only where a controlled test shows benefit). Each tier adds fields to the one below; fields above a
/// strategist's tier are replaced by the chosen playbook's defaults before the proposal is validated.
/// </summary>
public enum VocabularyTier
{
    /// <summary>Choose a playbook (plus confidence, expiry, assumptions, rationale); everything else is the playbook's.</summary>
    PlaybookOnly = 0,

    /// <summary>Also set the playbook's parameters (build step 5's "select and parameterise"): the base tier.</summary>
    Parameters = 1,

    /// <summary>Also set objectives and regions of interest.</summary>
    ObjectivesAndRegions = 2,

    /// <summary>Also set posture, budget, composition and attack, abort and replan conditions: every field.</summary>
    Full = 3,
}

/// <summary>Applies a <see cref="VocabularyTier"/> to a mapped proposal.</summary>
public static class IntentVocabulary
{
    /// <summary>
    /// The proposal as its tier allows: fields above the tier come from <see cref="IntentComposer.Compose"/> (the
    /// playbook's defaults and posture-derived objectives, as every deterministic strategist produces them); the
    /// proposal's identity, timing, confidence, assumptions and rationale are kept. <c>Dropped</c> names the fields
    /// that changed, in ordinal order. An unknown playbook is returned unchanged for the validator to reject.
    /// </summary>
    public static (StrategicIntent Intent, IReadOnlyList<string> Dropped) Restrict(StrategicIntent proposed, VocabularyTier tier, IPlaybookLibrary playbooks, StrategicFeatures features)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentNullException.ThrowIfNull(playbooks);
        ArgumentNullException.ThrowIfNull(features);
        if (tier == VocabularyTier.Full || !playbooks.TryGet(proposed.PlaybookId, out Playbook playbook)) return (proposed, []);

        StrategicIntent defaults = IntentComposer.Compose(
            playbook, features, proposed.IntentId, proposed.Source, proposed.Confidence, proposed.Rationale ?? string.Empty,
            Math.Max(1, proposed.ExpiresAt.SecondsSince(proposed.IssuedAt)));
        StrategicIntent restricted = defaults with
        {
            IntentId = proposed.IntentId,
            Source = proposed.Source,
            BasedOnSnapshotVersion = proposed.BasedOnSnapshotVersion,
            IssuedAt = proposed.IssuedAt,
            ExpiresAt = proposed.ExpiresAt,
            Confidence = proposed.Confidence,
            Assumptions = proposed.Assumptions,
            Rationale = proposed.Rationale,
        };
        if (tier >= VocabularyTier.Parameters)
        {
            // The playbook's conditions stay, but one that carries the attack threshold follows the chosen parameter.
            restricted = restricted with
            {
                PlaybookParameters = proposed.PlaybookParameters,
                AttackConditions = AttackArmyThreshold.Retarget(
                    restricted.AttackConditions, AttackArmyThreshold.Of(defaults.PlaybookParameters), AttackArmyThreshold.Of(proposed.PlaybookParameters)),
            };
        }
        if (tier >= VocabularyTier.ObjectivesAndRegions) restricted = restricted with { Objectives = proposed.Objectives, RegionsOfInterest = proposed.RegionsOfInterest };

        List<string> dropped = [];
        if (!SameMap(proposed.PlaybookParameters, restricted.PlaybookParameters)) dropped.Add("parameters");
        if (!proposed.Objectives.SequenceEqual(restricted.Objectives)) dropped.Add("objectives");
        if (!proposed.RegionsOfInterest.SequenceEqual(restricted.RegionsOfInterest)) dropped.Add("regionsOfInterest");
        if (proposed.Posture != restricted.Posture) dropped.Add("posture");
        if (proposed.Budget != restricted.Budget) dropped.Add("budget");
        if (!proposed.Composition.SequenceEqual(restricted.Composition)) dropped.Add("composition");
        if (!proposed.AttackConditions.SequenceEqual(restricted.AttackConditions)) dropped.Add("attackConditions");
        if (!proposed.AbortTriggers.SequenceEqual(restricted.AbortTriggers)) dropped.Add("abortTriggers");
        if (!proposed.ReplanTriggers.SequenceEqual(restricted.ReplanTriggers)) dropped.Add("replanTriggers");
        dropped.Sort(StringComparer.Ordinal);
        return (restricted, dropped);
    }

    private static bool SameMap(IReadOnlyDictionary<string, double> a, IReadOnlyDictionary<string, double> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out double v) && v.Equals(p.Value));
}

/// <summary>One paired comparison of a tier against the tier below it.</summary>
/// <param name="Split">Map split the pairs come from; only <c>heldout</c> counts for adoption.</param>
/// <param name="BaselineScore">Mean match score (win 1, draw ½) of the tier below.</param>
/// <param name="Live">True when a live model played (never for <c>--llm-fake</c>).</param>
public sealed record TierEvidence(
    VocabularyTier Tier,
    VocabularyTier AgainstTier,
    string Split,
    int Pairs,
    double BaselineScore,
    double TierScore,
    double MeanDifference,
    double CiLow,
    double CiHigh,
    int Better,
    int Worse,
    int Ties,
    double SignTestP,
    bool Live);

/// <summary>
/// The recorded decision about which tier the LLM strategist uses, and the rule that made it (like the tuner's
/// adoption rule for parameters). <see cref="Embedded"/> is the record shipped with the bot; the default
/// <see cref="ClaudeStrategistOptions.Vocabulary"/> is its <see cref="AdoptedTier"/>.
/// </summary>
public sealed record VocabularyAdoption(
    string Schema,
    VocabularyTier AdoptedTier,
    string Rule,
    IReadOnlyList<TierEvidence> Evidence,
    IReadOnlyList<string> Reasons,
    string? Date)
{
    public const string CurrentSchema = "bindery.vocabulary-adoption/v1";

    /// <summary>Build step 5's authority, granted by the operator directive; wider tiers need evidence.</summary>
    public const VocabularyTier BaseTier = VocabularyTier.Parameters;

    public const double SignificanceLevel = 0.05;

    public const string AdoptionRule =
        "Parameters (select and parameterise validated playbooks, build step 5) is the base tier. A wider tier is adopted only if the tier below it is adopted and, on held-out maps, the paired comparison of the wider tier against it (same opponent, map and seed) was played with a live model and shows more pairs won than lost on match score, an exact two-sided sign-test p below 0.05, and a 95% bootstrap interval of the mean score difference entirely above 0.";

    private static readonly Lazy<VocabularyAdoption> EmbeddedRecord = new(LoadEmbedded);

    /// <summary>The adoption record embedded in this assembly (<c>Data/vocabulary-adoption.json</c>).</summary>
    public static VocabularyAdoption Embedded => EmbeddedRecord.Value;

    /// <summary>Applies <see cref="AdoptionRule"/> to the evidence: walks up from <see cref="BaseTier"/> while each next tier wins.</summary>
    public static VocabularyAdoption Decide(IReadOnlyList<TierEvidence> evidence, string? date = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        VocabularyTier adopted = BaseTier;
        List<string> reasons = [];
        for (VocabularyTier next = BaseTier + 1; next <= VocabularyTier.Full; next++)
        {
            TierEvidence? e = evidence.FirstOrDefault(x => x.Tier == next && x.AgainstTier == next - 1 && x.Split == "heldout");
            string? failure = e is null ? $"no held-out comparison of {next} against {next - 1}"
                : !e.Live ? $"{next} vs {next - 1}: fake-client evidence is not evidence"
                : e.Better <= e.Worse ? $"{next} vs {next - 1}: {e.Better} pairs better, {e.Worse} worse"
                : !(e.SignTestP < SignificanceLevel) ? string.Create(CultureInfo.InvariantCulture, $"{next} vs {next - 1}: sign-test p {e.SignTestP:0.0000} is not below {SignificanceLevel}")
                : !(e.CiLow > 0) ? string.Create(CultureInfo.InvariantCulture, $"{next} vs {next - 1}: score interval [{e.CiLow:0.000}, {e.CiHigh:0.000}] does not exclude 0")
                : null;
            if (failure is not null)
            {
                reasons.Add($"stays at {adopted}: {failure}");
                break;
            }
            reasons.Add(string.Create(CultureInfo.InvariantCulture, $"{next} adopted over {next - 1}: {e!.Better}/{e.Worse} pairs better/worse, p {e.SignTestP:0.0000}, difference {e.MeanDifference:0.000} [{e.CiLow:0.000}, {e.CiHigh:0.000}]"));
            adopted = next;
        }
        return new VocabularyAdoption(CurrentSchema, adopted, AdoptionRule, evidence, reasons, date);
    }

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions(BotJson.Options) { WriteIndented = true });

    public static VocabularyAdoption Parse(string json)
    {
        VocabularyAdoption record = JsonSerializer.Deserialize<VocabularyAdoption>(json, BotJson.Options)
            ?? throw new InvalidDataException("Empty vocabulary adoption record.");
        if (record.Schema != CurrentSchema) throw new InvalidDataException($"Vocabulary adoption schema '{record.Schema}', expected '{CurrentSchema}'.");
        return record;
    }

    private static VocabularyAdoption LoadEmbedded()
    {
        Assembly assembly = typeof(VocabularyAdoption).Assembly;
        string name = assembly.GetManifestResourceNames().Single(static n => n.EndsWith("vocabulary-adoption.json", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(name)!;
        using StreamReader reader = new(stream);
        return Parse(reader.ReadToEnd());
    }
}
