// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace Bindery.Ra2.Bot.Baseline.Playbooks;

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
        byId = new Dictionary<string, Playbook>(StringComparer.Ordinal);
        foreach (Playbook playbook in playbooks) byId[playbook.Id] = playbook;
        All = playbooks.OrderBy(static p => p.Id, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<Playbook> All { get; }

    /// <summary>The 12 playbooks authored for this bot: allied-boom, allied-grizzly-timing, allied-ifv-mix, allied-prism-turtle, allied-harass, soviet-rhino-rush, soviet-flak-mix, soviet-v3-siege, soviet-apoc-tech, soviet-turtle, generic-defend, generic-expand.</summary>
    public static PlaybookLibrary LoadDefault() => new(DefaultPlaybooks.All);

    /// <summary>Parses a <see cref="PlaybookDocument"/> from JSON text (an operator-authored playbook set).</summary>
    public static PlaybookLibrary LoadJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        PlaybookDocument document = JsonSerializer.Deserialize<PlaybookDocument>(json, BotJson.Options)
            ?? throw new InvalidDataException("Playbook document was empty or malformed.");
        return new PlaybookLibrary(document.Playbooks);
    }

    /// <summary>Parses a <see cref="PlaybookDocument"/> from a JSON stream (not closed by this call).</summary>
    public static PlaybookLibrary LoadJson(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        PlaybookDocument document = JsonSerializer.Deserialize<PlaybookDocument>(stream, BotJson.Options)
            ?? throw new InvalidDataException("Playbook document was empty or malformed.");
        return new PlaybookLibrary(document.Playbooks);
    }

    public bool TryGet(string id, out Playbook playbook) => byId.TryGetValue(id, out playbook!);

    public IReadOnlyList<Playbook> For(Faction faction) =>
        All.Where(p => p.Factions.Contains(faction)).ToList();
}
