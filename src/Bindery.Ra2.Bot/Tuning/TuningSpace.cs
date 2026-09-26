// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Features;
using Bindery.Ra2.Bot.Operations;

namespace Bindery.Ra2.Bot.Tuning;

/// <summary>One searched coordinate: a playbook parameter or an option knob, with its declared range.</summary>
/// <param name="Scope"><c>playbook</c>, <c>operational</c> or <c>features</c>.</param>
/// <param name="PlaybookId">The owning playbook for <c>playbook</c> scope, else null.</param>
public sealed record TuningDimension(string Scope, string? PlaybookId, string Name, double Min, double Max, bool Integer, double Default)
{
    public string Key => PlaybookId is null ? $"{Scope}.{Name}" : $"{Scope}.{PlaybookId}.{Name}";

    /// <summary>Maps a unit-interval coordinate to a value in [Min, Max] (rounded for integer knobs).</summary>
    public double Decode(double unit)
    {
        double v = Min + Math.Clamp(unit, 0, 1) * (Max - Min);
        return Integer ? Math.Round(v, MidpointRounding.AwayFromZero) : v;
    }

    public double Encode(double value) => Max > Min ? Math.Clamp((value - Min) / (Max - Min), 0, 1) : 0;
}

/// <summary>
/// The search space: every consumed playbook parameter (<see cref="TuningKnobs.ConsumedPlaybookParameters"/>) of
/// every playbook, then the operational and feature knobs, each normalised to [0, 1] so a single step size
/// is meaningful across a 600–3000 army value and a 0.2–0.9 fraction alike. Order is fixed (playbook id,
/// parameter name, then knob list order) so a seeded search visits the same points on every machine.
/// </summary>
public sealed class TuningSpace
{
    public TuningSpace(IReadOnlyList<Playbook> playbooks, OperationalOptions operational, FeatureOptions features)
    {
        ArgumentNullException.ThrowIfNull(playbooks);
        ArgumentNullException.ThrowIfNull(operational);
        ArgumentNullException.ThrowIfNull(features);
        List<TuningDimension> dims = [];
        List<string> inert = [];
        foreach (Playbook playbook in playbooks.OrderBy(static p => p.Id, StringComparer.Ordinal))
        {
            foreach (PlaybookParameter parameter in playbook.Parameters.OrderBy(static p => p.Name, StringComparer.Ordinal))
            {
                if (TuningKnobs.ConsumedPlaybookParameters.Contains(parameter.Name))
                {
                    dims.Add(new TuningDimension("playbook", playbook.Id, parameter.Name, parameter.Min, parameter.Max, false, parameter.Default));
                }
                else
                {
                    inert.Add($"{playbook.Id}.{parameter.Name}");
                }
            }
        }
        foreach (OptionKnob knob in TuningKnobs.Operational)
        {
            dims.Add(new TuningDimension("operational", null, knob.Name, knob.Min, knob.Max, knob.Integer, TuningKnobs.Get(operational, knob.Name)));
        }
        foreach (OptionKnob knob in TuningKnobs.Features)
        {
            dims.Add(new TuningDimension("features", null, knob.Name, knob.Min, knob.Max, knob.Integer, TuningKnobs.Get(features, knob.Name)));
        }
        Dimensions = dims;
        InertPlaybookParameters = inert;
    }

    public IReadOnlyList<TuningDimension> Dimensions { get; }

    /// <summary>Declared playbook parameters left out of the search because nothing reads them.</summary>
    public IReadOnlyList<string> InertPlaybookParameters { get; }

    /// <summary>The authored defaults as a unit-interval point: the search's starting mean.</summary>
    public double[] DefaultPoint() => [.. Dimensions.Select(static d => d.Encode(d.Default))];

    /// <summary>Decodes a unit-interval point into a (not adopted) override set holding every searched value.</summary>
    public TunedParameterSet Decode(IReadOnlyList<double> point)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (point.Count != Dimensions.Count) throw new ArgumentException($"Point has {point.Count} coordinates; the space has {Dimensions.Count}.", nameof(point));
        SortedDictionary<string, SortedDictionary<string, double>> playbooks = new(StringComparer.Ordinal);
        SortedDictionary<string, double> operational = new(StringComparer.Ordinal);
        SortedDictionary<string, double> features = new(StringComparer.Ordinal);
        for (int i = 0; i < Dimensions.Count; i++)
        {
            TuningDimension d = Dimensions[i];
            double value = Math.Round(d.Decode(point[i]), 6);
            switch (d.Scope)
            {
                case "playbook":
                    if (!playbooks.TryGetValue(d.PlaybookId!, out SortedDictionary<string, double>? values)) playbooks[d.PlaybookId!] = values = new(StringComparer.Ordinal);
                    values[d.Name] = value;
                    break;
                case "operational": operational[d.Name] = value; break;
                default: features[d.Name] = value; break;
            }
        }
        return new TunedParameterSet(
            1, false,
            playbooks.ToDictionary(static p => p.Key, static p => (IReadOnlyDictionary<string, double>)p.Value, StringComparer.Ordinal),
            operational, features);
    }
}
