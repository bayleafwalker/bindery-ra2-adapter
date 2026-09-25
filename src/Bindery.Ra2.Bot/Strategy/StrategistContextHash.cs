// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bindery.Ra2.Bot.Runtime;

namespace Bindery.Ra2.Bot.Strategy;

/// <summary>
/// A stable digest of everything a strategist is given that varies during a match:
/// the compiled features, the active intent, the intent history and the personality.
/// (The rules database and playbook catalogue are fixed per match and identified by
/// <see cref="IRulesDatabase.RulesetId"/> and the playbook ids.) The fog invariant is
/// tested by perturbing hidden simulator state and requiring this digest to stay the same.
/// </summary>
public static class StrategistContextHash
{
    /// <summary>Canonical JSON of the context (<see cref="BotJson.Options"/>, intent parameters sorted).</summary>
    public static string ToJson(StrategistContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var view = new
        {
            rulesetId = context.Rules.RulesetId,
            playbooks = context.Playbooks.All.Select(static p => p.Id).OrderBy(static id => id, StringComparer.Ordinal).ToList(),
            features = context.Features,
            activeIntent = context.ActiveIntent is null ? (JsonElement?)null : IntentJson.ToElement(context.ActiveIntent),
            history = context.History,
            personality = context.Personality,
        };
        return JsonSerializer.Serialize(view, BotJson.Options);
    }

    /// <summary>Lowercase hex SHA-256 of <see cref="ToJson"/>.</summary>
    public static string Compute(StrategistContext context) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ToJson(context)))).ToLowerInvariant();
}
