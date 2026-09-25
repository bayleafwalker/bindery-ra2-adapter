// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Claude;
using Bindery.Ra2.Bot.Runtime;
using Bindery.Ra2.Bot.Strategy;

namespace Bindery.Ra2.Bot.Arena;

/// <summary>State shared by every match of one arena run: learners, datasets and LLM availability.</summary>
public sealed class ArenaRunContext
{
    public ArenaRunContext(bool llmFake, double? llmLatencySeconds)
    {
        LlmFake = llmFake;
        LlmLatencySeconds = llmLatencySeconds;
    }

    public bool LlmFake { get; }

    /// <summary>Simulated game-time latency for LLM answers; null uses measured wall latency (fake: 4 s).</summary>
    public double? LlmLatencySeconds { get; }

    /// <summary>The bandit arm's learner, shared across the run's matches (the spec's "learns across matches within a run").</summary>
    public ContextualBanditStrategist Bandit { get; } = new();

    /// <summary>Dataset for the distilled arm, and where it came from.</summary>
    public DecisionDataset? DistillDataset { get; set; }

    public string? DistillSource { get; set; }

    /// <summary>Set when a live LLM arm cannot run; every LLM-arm match is then skipped with this reason.</summary>
    public string? LlmSkipReason { get; set; }

    private readonly object gate = new();

    public void MarkLlmSkipped(string reason)
    {
        lock (gate) LlmSkipReason ??= reason;
    }

    /// <summary>A message client for an LLM arm: the fake with <c>--llm-fake</c>, else the SDK client (env key or ant profile).</summary>
    public IMessageClient? CreateClient()
    {
        if (LlmFake) return new FakeMessageClient();
        if (LlmSkipReason is not null) return null;
        try
        {
            return new AnthropicMessageClient();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MarkLlmSkipped($"skipped: no credential ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }
}

/// <summary>
/// Builds a <see cref="BotRuntime"/>-backed agent for every arm in the spec's arena table
/// and for every opponent style. All sides share the standard layers
/// (<see cref="StandardBot"/>) and differ only in strategists:
/// <list type="bullet">
/// <item><c>selector</c>: <see cref="PlaybookSelector"/>.</item>
/// <item><c>bandit</c>: the run's shared <see cref="ContextualBanditStrategist"/>, credited with the match outcome.</item>
/// <item><c>llm-shadow</c>: selector active, Claude strategist in the shadow slot.</item>
/// <item><c>llm</c>: Claude strategist, selector fallback.</item>
/// <item><c>llm+fast</c>: Claude strategist every 20 s plus Claude Refine (Haiku) in between, requested every 5 s.</item>
/// <item><c>distilled</c>: <see cref="DistilledStrategist"/> over the run's dataset, escalating to a selector.</item>
/// <item>Opponents <c>rush</c>, <c>turtle</c>, <c>tech</c>, <c>harass</c>, <c>balanced</c>: <see cref="PinnedPlaybookStrategist"/>
/// on the matching playbook for the side's faction.</item>
/// </list>
/// Every side's fallback is a <see cref="PlaybookSelector"/>. Claude strategists run behind a
/// <see cref="SimulatedLatencyStrategist"/> so their answers arrive after a game-time latency.
/// </summary>
public sealed class BotAgentFactory(IRulesDatabase rules, IPlaybookLibrary playbooks, ArenaRunContext context) : IArenaAgentFactory
{
    public const double FakeLatencySeconds = 4.0;

    /// <summary>Opponent style → playbook per faction.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<Faction, string>> OpponentStyles { get; } =
        new Dictionary<string, IReadOnlyDictionary<Faction, string>>(StringComparer.Ordinal)
        {
            ["rush"] = new Dictionary<Faction, string> { [Faction.Allied] = "allied-grizzly-timing", [Faction.Soviet] = "soviet-rhino-rush" },
            ["turtle"] = new Dictionary<Faction, string> { [Faction.Allied] = "allied-prism-turtle", [Faction.Soviet] = "soviet-turtle" },
            ["tech"] = new Dictionary<Faction, string> { [Faction.Allied] = "allied-boom", [Faction.Soviet] = "soviet-apoc-tech" },
            ["harass"] = new Dictionary<Faction, string> { [Faction.Allied] = "allied-harass", [Faction.Soviet] = "soviet-v3-siege" },
            ["balanced"] = new Dictionary<Faction, string> { [Faction.Allied] = "allied-ifv-mix", [Faction.Soviet] = "soviet-flak-mix" },
        };

    public static IReadOnlyList<string> Arms { get; } = ["selector", "bandit", "llm-shadow", "llm", "llm+fast", "distilled"];

    public IArenaAgent Create(ArmSpec arm, PlayerId player, Faction faction, MapInfo map, int seed)
    {
        ArgumentNullException.ThrowIfNull(arm);
        DecisionLog log = new();
        List<string> labels = [];
        if (arm.Oracle) labels.Add("oracle");
        BotOptions options = StandardBot.SimulatorOptions;
        IStrategist fallback = new PlaybookSelector(id: "selector-fallback");
        IStrategist? shadow = null;
        IStrategist primary;
        Action<bool?, double, double, BotArenaAgent>? onFinish = null;
        List<ClaudeStrategist> claude = [];

        if (OpponentStyles.TryGetValue(arm.Name, out IReadOnlyDictionary<Faction, string>? style))
        {
            primary = new PinnedPlaybookStrategist(style, $"style-{arm.Name}");
        }
        else
        {
            switch (arm.Name)
            {
                case "selector":
                    primary = new PlaybookSelector();
                    break;
                case "bandit":
                    primary = context.Bandit;
                    onFinish = (won, own, enemy, _) => context.Bandit.CompleteEpisode(Reward(won, own, enemy));
                    break;
                case "llm-shadow":
                    primary = new PlaybookSelector();
                    shadow = Llm(StrategistMode.Strategic, claude, labels);
                    break;
                case "llm":
                    primary = Llm(StrategistMode.Strategic, claude, labels);
                    break;
                case "llm+fast":
                    IStrategist slow = Unwrapped(StrategistMode.Strategic, claude);
                    IStrategist fast = Unwrapped(StrategistMode.Refine, claude);
                    primary = Latency(new TwoSpeedStrategist(slow, fast, 20, "claude-strategic+refine"));
                    options = options with { StrategicCadenceSeconds = 5 };
                    Label(labels);
                    break;
                case "distilled":
                    DecisionDataset dataset = context.DistillDataset ?? DecisionDataset.Empty;
                    primary = new DistilledStrategist(dataset, new PlaybookSelector(id: "distilled-escalation"));
                    labels.Add($"distilled-from:{context.DistillSource ?? "none"} ({dataset.Count} examples)");
                    break;
                default:
                    throw new ArgumentException($"Unknown arm or opponent '{arm.Name}'. Arms: {string.Join(", ", Arms)}; opponents: {string.Join(", ", OpponentStyles.Keys)}.");
            }
        }

        BotRuntime runtime = StandardBot.Create(rules, playbooks, primary, fallback, shadow, log, options);
        foreach (ClaudeStrategist strategist in claude)
        {
            strategist.ProposalFailed += (_, failure) =>
            {
                log.Write(failure.ToDecisionRecord(strategist.Id));
                if (!context.LlmFake && failure.Code is ClaudeFailureCodes.Unauthorized)
                {
                    context.MarkLlmSkipped($"skipped: no credential ({failure.Detail})");
                }
            };
        }
        BotArenaAgent agent = new(runtime, log, labels, onFinish);
        agent.ClaudeStrategists.AddRange(claude);
        return agent;
    }

    /// <summary>Bandit reward in [-1, 1]: ±0.5 for the result plus half the normalised final asset difference.</summary>
    public static double Reward(bool? won, double own, double enemy)
    {
        double result = won switch { true => 0.5, false => -0.5, null => 0 };
        double margin = own + enemy <= 0 ? 0 : (own - enemy) / (own + enemy);
        return Math.Clamp(result + 0.5 * margin, -1, 1);
    }

    private IStrategist Llm(StrategistMode mode, List<ClaudeStrategist> claude, List<string> labels)
    {
        Label(labels);
        return Latency(Unwrapped(mode, claude));
    }

    private IStrategist Unwrapped(StrategistMode mode, List<ClaudeStrategist> claude)
    {
        IMessageClient client = context.CreateClient() ?? new UnavailableClient(context.LlmSkipReason ?? "skipped: no credential");
        ClaudeStrategistOptions options = mode == StrategistMode.Refine ? ClaudeStrategistOptions.ForRefine() : new ClaudeStrategistOptions();
        ClaudeStrategist strategist = new(client, options);
        claude.Add(strategist);
        return strategist;
    }

    private IStrategist Latency(IStrategist inner) =>
        new SimulatedLatencyStrategist(inner, context.LlmLatencySeconds ?? (context.LlmFake ? FakeLatencySeconds : null));

    private void Label(List<string> labels)
    {
        if (context.LlmFake && !labels.Contains("llm-fake")) labels.Add("llm-fake");
    }

    /// <summary>Stands in when no credential resolved: every call fails as unauthorized.</summary>
    private sealed class UnavailableClient(string reason) : IMessageClient
    {
        public Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken cancellationToken) =>
            Task.FromException<ModelReply>(new ModelClientException(ModelFailureKind.Unauthorized, reason));
    }
}
