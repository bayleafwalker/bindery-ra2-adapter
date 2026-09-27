// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Bindery.Ra2.Adapter.Channel;

namespace Bindery.Ra2.Adapter.Planner.Claude;

/// <summary>
/// A playbook planner backed by Claude: at each trigger it reads the seat's
/// <see cref="PlayerViewSummary"/> -- only what the house was allowed to
/// see -- and returns the next playbook as structured output.
/// </summary>
/// <remarks>
/// The directive vocabulary is fixed and matches <see cref="RulePlaybookPlanner"/>,
/// so the same routine controller plays either planner's playbooks and the
/// experiment report compares like with like. The planner is slow next to
/// the game loop; <see cref="PlaybookController"/> keeps playing the current
/// playbook while a plan is in flight. A refusal, an unparseable answer or a
/// timeout throws, and the controller keeps the current playbook.
/// </remarks>
public sealed class ClaudePlaybookPlanner(AnthropicClient client, ClaudePlannerOptions? options = null) : IPlaybookPlanner
{
    private readonly AnthropicClient client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly ClaudePlannerOptions options = options ?? new ClaudePlannerOptions();

    public string PlannerId => "claude/" + options.Model;

    public async Task<Playbook> PlanAsync(PlanningRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);

        MessageCreateParams parameters = new()
        {
            Model = options.Model,
            MaxTokens = options.MaxTokens,
            // Refusal fallbacks, routed by category on the server, so a
            // declined plan is re-served rather than lost.
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            System = PlannerPrompt.System,
            OutputConfig = new BetaOutputConfig
            {
                Effort = options.Effort,
                Format = new BetaJsonOutputFormat { Schema = PlannerPrompt.Schema() },
            },
            Messages = [new() { Role = Role.User, Content = PlannerPrompt.User(request) }],
        };
        BetaMessage response = await client.Beta.Messages.Create(parameters, timeout.Token).ConfigureAwait(false);

        if (response.StopReason == "refusal")
            throw new InvalidOperationException($"the planner declined: {response.StopDetails?.Category?.ToString() ?? "no category"}");
        string text = string.Concat(response.Content.Select(static block => block.Value).OfType<BetaTextBlock>().Select(static block => block.Text));
        return PlannerPrompt.Parse(text, request.Current);
    }
}

/// <param name="Model">Claude Opus 5 by default.</param>
/// <param name="Effort">
/// Medium by default: a plan is wanted within seconds of the trigger, and
/// the routine layer does the fine-grained work. Raise it for offline
/// replays where latency does not matter.
/// </param>
public sealed record ClaudePlannerOptions(
    string Model = "claude-opus-5",
    string Effort = "medium",
    long MaxTokens = 16000,
    TimeSpan? PlanTimeout = null)
{
    public TimeSpan Timeout => PlanTimeout ?? TimeSpan.FromSeconds(60);
}

/// <summary>The planner's frozen prompt, schema and answer parser, kept apart from the API call so they can be tested.</summary>
public static class PlannerPrompt
{
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Vocabulary { get; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        ["economy"] = ["build_refineries", "expand", "hold"],
        ["posture"] = ["scout", "defend", "pressure", "attack"],
        ["tech"] = ["hold", "advance"],
    };

    // Frozen: no timestamps or per-match values, so it stays byte-identical.
    public const string System = """
        You are the strategic planner for one player's seat in a match of Command & Conquer: Red Alert 2 - Yuri's Revenge.
        A deterministic controller carries out your playbook between your revisions; you are called only at meaningful moments (a trigger).

        You receive the trigger, the current playbook, and a summary of what this player is allowed to know: its own credits and their trend, its own units and buildings, how many enemy objects it has seen, and which houses are defeated. You never see the enemy's economy or hidden units, and you should not assume knowledge you were not given.

        Return the next playbook:
        - one value for each directive (economy, posture, tech) from the allowed values;
        - a one-sentence summary a viewer could follow;
        - a short rationale tied to the trigger and the summary.
        Change only what the situation calls for; keeping a directive is a valid choice.
        """;

    public static IReadOnlyDictionary<string, JsonElement> Schema()
    {
        Dictionary<string, object> directiveProperties = Vocabulary.ToDictionary(
            static pair => pair.Key,
            static pair => (object)new Dictionary<string, object> { ["type"] = "string", ["enum"] = pair.Value });
        return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["type"] = JsonSerializer.SerializeToElement("object"),
            ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
            ["required"] = JsonSerializer.SerializeToElement(new[] { "directives", "summary", "rationale" }),
            ["properties"] = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["directives"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = Vocabulary.Keys.ToArray(),
                    ["properties"] = directiveProperties,
                },
                ["summary"] = new Dictionary<string, object> { ["type"] = "string" },
                ["rationale"] = new Dictionary<string, object> { ["type"] = "string" },
            }),
        };
    }

    public static string User(PlanningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        PlayerViewSummary view = request.View;
        return JsonSerializer.Serialize(new
        {
            trigger = request.Trigger,
            current_playbook = new
            {
                revision = request.Current.Revision,
                directives = request.Current.Directives,
                text = request.Current.Text,
            },
            player = new
            {
                house = view.House,
                elapsed_seconds = (long)view.Elapsed.TotalSeconds,
                credits = view.Credits,
                credits_change_over_window = view.CreditsChangeOverWindow,
                own_units = view.OwnUnits,
                own_building_types = view.OwnBuildingTypes,
                enemy_sightings = view.EnemySightings,
                defeated_houses = view.DefeatedHouses,
            },
        });
    }

    /// <summary>
    /// Reads the structured answer. Structured outputs guarantee the shape;
    /// this still checks it, because a fallback model or a truncated answer
    /// must never become a playbook the routine controller cannot read.
    /// </summary>
    public static Playbook Parse(string text, Playbook current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("the planner returned no text");
        using JsonDocument document = JsonDocument.Parse(text);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("directives", out JsonElement directives) || directives.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("the planner's answer has no directives");
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach ((string key, IReadOnlyList<string> allowed) in Vocabulary)
        {
            string? value = directives.TryGetProperty(key, out JsonElement element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;
            if (value is null || !allowed.Contains(value, StringComparer.Ordinal))
                throw new InvalidOperationException($"the planner's '{key}' directive is not one of {string.Join(", ", allowed)}");
            result[key] = value;
        }
        string summary = root.TryGetProperty("summary", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : string.Empty;
        string rationale = root.TryGetProperty("rationale", out JsonElement r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : string.Empty;
        string directiveText = string.Join(", ", result.OrderBy(static d => d.Key, StringComparer.Ordinal).Select(static d => $"{d.Key}={d.Value}"));
        string playbookText = string.IsNullOrWhiteSpace(summary) ? directiveText : $"{summary} ({directiveText})";
        if (!string.IsNullOrWhiteSpace(rationale)) playbookText += $" -- {rationale}";
        return new Playbook(current.Revision + 1, playbookText, result);
    }
}
