// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace Bindery.Ra2.Bot.Playbooks;

/// <summary>
/// In-memory playbook catalogue. <see cref="LoadDefault"/> returns the 12
/// authored playbooks named in the strategic-bot spec; an operator can instead
/// (or additionally, by concatenating <see cref="All"/> with their own list and
/// passing the combined list to the public constructor) load playbooks from
/// JSON with <see cref="LoadJson(string)"/>, using the same <see cref="PlaybookDocument"/>
/// schema.
/// </summary>
public sealed class PlaybookLibrary : IPlaybookLibrary
{
    private readonly Dictionary<string, Playbook> byId;

    /// <summary>Builds a library from an explicit playbook list, e.g. the default set plus operator-authored additions.</summary>
    public PlaybookLibrary(IReadOnlyList<Playbook> playbooks)
    {
        ArgumentNullException.ThrowIfNull(playbooks);
        string? problem = Problem(playbooks);
        if (problem is not null) throw new ArgumentException(problem, nameof(playbooks));
        byId = new Dictionary<string, Playbook>(StringComparer.Ordinal);
        foreach (Playbook playbook in playbooks) byId.Add(playbook.Id, playbook);
        All = playbooks.OrderBy(static p => p.Id, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<Playbook> All { get; }

    /// <summary>
    /// The 12 playbooks authored for this bot (allied-boom, allied-grizzly-timing, allied-ifv-mix, allied-prism-turtle,
    /// allied-harass, soviet-rhino-rush, soviet-flak-mix, soviet-v3-siege, soviet-apoc-tech, soviet-turtle,
    /// generic-defend, generic-expand), with parameter defaults from the embedded tuned set
    /// (<c>Data/tuned-parameters.json</c>) when that set was adopted after held-out validation.
    /// </summary>
    public static PlaybookLibrary LoadDefault() => new(Tuning.TunedParameterSet.Active.ApplyTo(DefaultPlaybooks.All));

    /// <summary>The 12 playbooks exactly as authored, ignoring any tuned set: the tuner's untuned baseline.</summary>
    public static PlaybookLibrary LoadAuthored() => new(DefaultPlaybooks.All);

    /// <summary>Parses a <see cref="PlaybookDocument"/> from JSON text (an operator-authored playbook set).</summary>
    public static PlaybookLibrary LoadJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        PlaybookDocument document = JsonSerializer.Deserialize<PlaybookDocument>(json, BotJson.Options)
            ?? throw new InvalidDataException("Playbook document was empty or malformed.");
        return FromDocument(document);
    }

    /// <summary>Parses a <see cref="PlaybookDocument"/> from a JSON stream (not closed by this call).</summary>
    public static PlaybookLibrary LoadJson(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        PlaybookDocument document = JsonSerializer.Deserialize<PlaybookDocument>(stream, BotJson.Options)
            ?? throw new InvalidDataException("Playbook document was empty or malformed.");
        return FromDocument(document);
    }

    private static PlaybookLibrary FromDocument(PlaybookDocument document)
    {
        if (document.Playbooks is null) throw new InvalidDataException("Playbook document has no playbooks array.");
        string? problem = Problem(document.Playbooks);
        if (problem is not null) throw new InvalidDataException(problem);
        return new PlaybookLibrary(document.Playbooks);
    }

    /// <summary>
    /// The first reason <paramref name="playbooks"/> cannot form a library, or null. System.Text.Json leaves a
    /// missing collection null instead of failing, so without this check an operator file with a missing array
    /// loads and then throws inside the strategist mid-match; a duplicated id would be offered to the LLM twice
    /// while <see cref="TryGet"/> resolved only one of them.
    /// </summary>
    private static string? Problem(IReadOnlyList<Playbook> playbooks)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int i = 0; i < playbooks.Count; i++)
        {
            Playbook? p = playbooks[i];
            if (p is null || string.IsNullOrWhiteSpace(p.Id)) return $"Playbook #{i} is null or has no id.";
            string where = $"Playbook '{p.Id}'";
            if (!seen.Add(p.Id)) return $"{where} appears more than once.";
            if (p.Description is null) return $"{where} has no description.";
            if (p.Factions is null || p.Factions.Count == 0) return $"{where} has no factions.";
            if (p.Budget is null) return $"{where} has no budget.";
            double[] shares = [p.Budget.Economy, p.Budget.Army, p.Budget.Tech, p.Budget.Defense];
            if (shares.Any(static v => !double.IsFinite(v) || v < 0) || Math.Abs(shares.Sum() - 1) > 0.01)
            {
                return $"{where} budget shares must be non-negative and sum to 1.";
            }
            if (p.Composition is null || p.Composition.Any(static c => c is null)) return $"{where} has a missing composition entry.";
            if (p.TechGoals is null || p.TechGoals.Any(string.IsNullOrWhiteSpace)) return $"{where} has a missing tech goal.";
            if (p.AttackConditions is null || p.AttackConditions.Any(static c => c is null)) return $"{where} has a missing attack condition.";
            if (p.AbortTriggers is null || p.AbortTriggers.Any(static c => c is null)) return $"{where} has a missing abort trigger.";
            if (p.Parameters is null) return $"{where} has no parameters array.";
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (PlaybookParameter? parameter in p.Parameters)
            {
                if (parameter is null || string.IsNullOrWhiteSpace(parameter.Name)) return $"{where} has a parameter with no name.";
                if (!names.Add(parameter.Name)) return $"{where} declares parameter '{parameter.Name}' twice.";
                if (!(parameter.Min <= parameter.Default && parameter.Default <= parameter.Max))
                {
                    return $"{where} parameter '{parameter.Name}' needs min <= default <= max.";
                }
            }
        }
        return null;
    }

    public bool TryGet(string id, out Playbook playbook) => byId.TryGetValue(id, out playbook!);

    public IReadOnlyList<Playbook> For(Faction faction) =>
        All.Where(p => p.Factions.Contains(faction)).ToList();
}
