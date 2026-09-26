// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Strategy;

/// <summary>
/// An authored play style. The same profile drives the LLM (as prompt guidance) and every deterministic strategist
/// (as playbook and parameter biases), so a personality is a recognisable way of playing whichever strategist is in
/// charge, not a string only the LLM reads.
/// </summary>
/// <param name="Id">Stable id (<c>aggressive</c>, <c>turtle</c>, <c>tech</c>, <c>harasser</c>), as passed in <see cref="StrategistContext.Personality"/>.</param>
/// <param name="PromptGuidance">What the LLM is told about the style (data in the match context, never instructions that override the rules).</param>
/// <param name="PreferredPlaybook">The style's playbook per faction: the selector's default, the bandit's and the distilled model's bias.</param>
/// <param name="ParameterScale">Multipliers on playbook parameter defaults (clamped to each parameter's range).</param>
/// <param name="DefendThreatRatio">Base threat ratio at which the style switches to <c>generic-defend</c>.</param>
/// <param name="BanditBonus">Added to the preferred playbook's LinUCB score.</param>
/// <param name="DistilledLogitBias">Added to the preferred playbook's logit in the distilled model.</param>
public sealed record PersonalityProfile(
    string Id,
    string Description,
    string PromptGuidance,
    IReadOnlyDictionary<Faction, string> PreferredPlaybook,
    IReadOnlyDictionary<string, double> ParameterScale,
    double DefendThreatRatio,
    double BanditBonus = 0.5,
    double DistilledLogitBias = 2.0)
{
    /// <summary>Playbook defaults scaled by <see cref="ParameterScale"/> and clamped to their declared ranges.</summary>
    public IReadOnlyDictionary<string, double> ScaledParameters(Playbook playbook)
    {
        ArgumentNullException.ThrowIfNull(playbook);
        SortedDictionary<string, double> values = new(StringComparer.Ordinal);
        foreach (PlaybookParameter p in playbook.Parameters)
        {
            double scale = ParameterScale.TryGetValue(p.Name, out double s) ? s : 1.0;
            values[p.Name] = Math.Round(Math.Clamp(p.Default * scale, p.Min, p.Max), 3);
        }
        return values;
    }
}

/// <summary>The authored personality set.</summary>
public static class Personalities
{
    public static PersonalityProfile Aggressive { get; } = new(
        "aggressive",
        "Early, sustained pressure with armour; fights on under threat.",
        "Aggressive: take the initiative early and keep it. Prefer armour timing attacks before the enemy techs, accept even trades, and keep pressing while the army holds; defend only when the base is clearly outmatched.",
        new Dictionary<Faction, string> { [Faction.Allied] = "allied-grizzly-timing", [Faction.Soviet] = "soviet-rhino-rush" },
        new Dictionary<string, double>(StringComparer.Ordinal) { ["attackArmyValue"] = 0.7, ["harvesterTarget"] = 0.8 },
        DefendThreatRatio: 1.6);

    public static PersonalityProfile Turtle { get; } = new(
        "turtle",
        "Defences first; attacks only with a clear lead.",
        "Turtle: secure the base with static defences and a strong economy first; attack only with a clear army lead; prefer late power over early trades.",
        new Dictionary<Faction, string> { [Faction.Allied] = "allied-prism-turtle", [Faction.Soviet] = "soviet-turtle" },
        new Dictionary<string, double>(StringComparer.Ordinal) { ["expandAtSeconds"] = 1.2, ["attackArmyValue"] = 1.3 },
        DefendThreatRatio: 1.0);

    public static PersonalityProfile Tech { get; } = new(
        "tech",
        "Economy and high-tier units before fighting.",
        "Tech: grow the economy and reach high-tier units before committing; avoid early trades; strike once the tech advantage is on the field.",
        new Dictionary<Faction, string> { [Faction.Allied] = "allied-boom", [Faction.Soviet] = "soviet-apoc-tech" },
        new Dictionary<string, double>(StringComparer.Ordinal) { ["expandAtSeconds"] = 0.8 },
        DefendThreatRatio: 1.3);

    public static PersonalityProfile Harasser { get; } = new(
        "harasser",
        "Raids and sieges; avoids the enemy main army.",
        "Harasser: raid harvesters and expansions with small, fast groups and siege defences from range; avoid the enemy's main army; keep the enemy reacting.",
        new Dictionary<Faction, string> { [Faction.Allied] = "allied-harass", [Faction.Soviet] = "soviet-v3-siege" },
        new Dictionary<string, double>(StringComparer.Ordinal) { ["harassIntervalSeconds"] = 0.6, ["siegeRangeBufferCells"] = 1.5 },
        DefendThreatRatio: 1.4);

    public static IReadOnlyList<PersonalityProfile> All { get; } = [Aggressive, Turtle, Tech, Harasser];

    /// <summary>The authored profile for an id (ordinal, case-insensitive); false for free-text or null personalities.</summary>
    public static bool TryGet(string? id, out PersonalityProfile profile)
    {
        profile = All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))!;
        return profile is not null;
    }
}
