// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using System.Text.Json;
using Bindery.Ra2.Bot.Features;
using Bindery.Ra2.Bot.Operations;

namespace Bindery.Ra2.Bot.Tuning;

/// <summary>
/// Where a tuned set came from, so a reader can reproduce it: every input that decided the search is here.
/// The date is passed on the tuner's command line, never read from the clock, so rerunning the same command
/// reproduces the file byte for byte.
/// </summary>
public sealed record TuningProvenance(
    string Tool,
    string Algorithm,
    string Mode,
    int Generations,
    int Population,
    int Parents,
    ulong RngSeed,
    IReadOnlyList<int> MatchSeeds,
    IReadOnlyList<string> Opponents,
    IReadOnlyList<string> Maps,
    double TradeWeight,
    int MatchesPlayed,
    double DefaultTrainingFitness,
    double TunedTrainingFitness,
    string Date,
    string? SourceCommit);

/// <summary>
/// Held-out validation of a tuned set against the authored defaults, and the adoption decision it produced.
/// </summary>
/// <param name="Rule">The adoption rule, stated in words, fixed before the validation ran.</param>
public sealed record TuningValidation(
    IReadOnlyList<string> Maps,
    IReadOnlyList<string> Opponents,
    int Seeds,
    int Matches,
    int DefaultWins,
    int TunedWins,
    double DefaultFitness,
    double TunedFitness,
    double PairedFitnessDifference,
    double PairedLow,
    double PairedHigh,
    int HeadToHeadWins,
    int HeadToHeadMatches,
    int ControlWins,
    int ControlMatches,
    string Rule,
    bool Adopted,
    string Date);

/// <summary>
/// A tuned override set for playbook parameter defaults and selected option knobs (<see cref="TuningKnobs"/>),
/// as written by <c>tools/Bindery.Ra2.Bot.Tune</c> and embedded from <c>Data/tuned-parameters.json</c>.
/// The bot applies it only when <see cref="Adopted"/> is true: the tuner writes the file either way so the
/// search and its validation are on record, but a set that did not beat the defaults on held-out maps
/// must not change behaviour.
/// </summary>
/// <param name="Playbooks">Playbook id → parameter name → new default. Values are clamped into the declared range.</param>
/// <param name="Operational">Knob name (see <see cref="TuningKnobs.Operational"/>) → value.</param>
/// <param name="Features">Knob name (see <see cref="TuningKnobs.Features"/>) → value.</param>
public sealed record TunedParameterSet(
    int SchemaVersion,
    bool Adopted,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> Playbooks,
    IReadOnlyDictionary<string, double> Operational,
    IReadOnlyDictionary<string, double> Features,
    TuningProvenance? Provenance = null,
    TuningValidation? Validation = null)
{
    private const string ResourceName = "Bindery.Ra2.Bot.Data.tuned-parameters.json";

    /// <summary>An empty, not-adopted set: applying it changes nothing.</summary>
    public static TunedParameterSet None { get; } = new(
        1, false,
        new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal),
        new Dictionary<string, double>(StringComparer.Ordinal),
        new Dictionary<string, double>(StringComparer.Ordinal));

    private static readonly Lazy<TunedParameterSet> EmbeddedSet = new(LoadEmbeddedCore);

    /// <summary>The set embedded in this assembly (<see cref="None"/> if the resource is absent).</summary>
    public static TunedParameterSet Embedded => EmbeddedSet.Value;

    /// <summary>The embedded set when adopted, else <see cref="None"/>: what the bot's defaults use.</summary>
    public static TunedParameterSet Active => Embedded.Adopted ? Embedded : None;

    public static TunedParameterSet LoadJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<TunedParameterSet>(json, BotJson.Options)
            ?? throw new InvalidDataException("Tuned parameter document was empty or malformed.");
    }

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions(BotJson.Options) { WriteIndented = true });

    /// <summary>
    /// Returns <paramref name="playbooks"/> with parameter defaults overridden. Throws on a playbook or parameter
    /// name that does not exist, so a renamed parameter cannot silently drop its tuned value.
    /// </summary>
    public IReadOnlyList<Playbook> ApplyTo(IReadOnlyList<Playbook> playbooks)
    {
        ArgumentNullException.ThrowIfNull(playbooks);
        foreach (string id in Playbooks.Keys)
        {
            if (!playbooks.Any(p => p.Id == id)) throw new InvalidDataException($"Tuned set names unknown playbook '{id}'.");
        }
        return [.. playbooks.Select(p =>
        {
            if (!Playbooks.TryGetValue(p.Id, out IReadOnlyDictionary<string, double>? values)) return p;
            foreach (string name in values.Keys)
            {
                if (!p.Parameters.Any(q => q.Name == name)) throw new InvalidDataException($"Tuned set names unknown parameter '{name}' of playbook '{p.Id}'.");
            }
            return p with
            {
                Parameters = [.. p.Parameters.Select(q => values.TryGetValue(q.Name, out double v)
                    ? q with { Default = Math.Clamp(v, q.Min, q.Max) }
                    : q)],
            };
        })];
    }

    /// <summary>Returns <paramref name="options"/> with each tuned operational knob set, clamped into its range.</summary>
    public OperationalOptions ApplyTo(OperationalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (KeyValuePair<string, double> pair in Operational.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            OptionKnob knob = TuningKnobs.Operational.FirstOrDefault(k => k.Name == pair.Key)
                ?? throw new InvalidDataException($"Tuned set names unknown operational knob '{pair.Key}'.");
            options = TuningKnobs.Set(options, knob.Name, Math.Clamp(pair.Value, knob.Min, knob.Max));
        }
        return options;
    }

    /// <summary>Returns <paramref name="options"/> with each tuned feature knob set, clamped into its range.</summary>
    public FeatureOptions ApplyTo(FeatureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (KeyValuePair<string, double> pair in Features.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            OptionKnob knob = TuningKnobs.Features.FirstOrDefault(k => k.Name == pair.Key)
                ?? throw new InvalidDataException($"Tuned set names unknown feature knob '{pair.Key}'.");
            options = TuningKnobs.Set(options, knob.Name, Math.Clamp(pair.Value, knob.Min, knob.Max));
        }
        return options;
    }

    private static TunedParameterSet LoadEmbeddedCore()
    {
        Assembly assembly = typeof(TunedParameterSet).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return None;
        using StreamReader reader = new(stream);
        return LoadJson(reader.ReadToEnd());
    }
}
