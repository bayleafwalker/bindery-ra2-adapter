// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bindery.Ra2.Bot.Arbitration;
using Bindery.Ra2.Bot.Features;
using Bindery.Ra2.Bot.Operations;
using Bindery.Ra2.Bot.Claude;
using Bindery.Ra2.Bot.Runtime;
using Bindery.Ra2.Bot.Sim.Opponents;
using Baseline = Bindery.Ra2.Bot.Baseline;
using Bindery.Ra2.Bot.Strategy;

namespace Bindery.Ra2.Bot.Arena;

/// <summary>State shared by every match of one arena run: learners, datasets and LLM availability.</summary>
public sealed class ArenaRunContext
{
    public ArenaRunContext(bool llmFake, double? llmLatencySeconds, string? llmEndpoint = null, string llmModel = "worker-fast")
    {
        LlmFake = llmFake;
        LlmLatencySeconds = llmLatencySeconds;
        LlmEndpoint = llmEndpoint;
        LlmModel = llmModel;
    }

    /// <summary>An OpenAI-compatible base URL the LLM arms call instead of Anthropic; null uses the Anthropic SDK.</summary>
    public string? LlmEndpoint { get; }

    /// <summary>The model id sent to <see cref="LlmEndpoint"/>.</summary>
    public string LlmModel { get; }

    /// <summary>Tuning-knob overrides for arms (never pinned or live-* opponents), from <c>--knob</c>.</summary>
    public IReadOnlyDictionary<string, double> ArmKnobs { get; init; } = new Dictionary<string, double>(StringComparer.Ordinal);

    public bool LlmFake { get; }

    /// <summary>Offer the LLM arms the extended condition metrics (<c>--extended-metrics</c>).</summary>
    public bool ExtendedMetrics { get; init; }

    /// <summary>Sees each request the <c>--llm-fake</c> client receives (tests read the prompt the model would be given).</summary>
    public Action<ModelRequest>? LlmRequestObserver { get; init; }

    /// <summary>Simulated game-time latency for LLM answers; null uses measured wall latency (fake: 4 s).</summary>
    public double? LlmLatencySeconds { get; }

    private readonly Dictionary<string, ContextualBanditStrategist> bandits = new(StringComparer.Ordinal);

    /// <summary>
    /// A bandit arm's learner, shared across that arm's matches in the run (the spec's "learns across matches
    /// within a run"). Every arm has its own, keyed by its full name (observation mode and personality included):
    /// one shared learner would let an oracle arm's omniscient episodes shape a belief arm's policy, and would make
    /// a belief-versus-oracle delta measure extra training as well as information.
    /// </summary>
    public ContextualBanditStrategist BanditFor(ArmSpec arm)
    {
        ArgumentNullException.ThrowIfNull(arm);
        lock (gate)
        {
            string key = arm.ToString();
            return bandits.TryGetValue(key, out ContextualBanditStrategist? bandit) ? bandit : bandits[key] = new ContextualBanditStrategist();
        }
    }

    /// <summary>Dataset for the distilled arm, and where it came from.</summary>
    public DecisionDataset? DistillDataset { get; set; }

    public string? DistillSource { get; set; }

    /// <summary>
    /// False while the arena plays a held-out opponent or a held-out map: the bandit's decisions in that match are
    /// then abandoned, never credited, so held-out opponents and maps cannot shape what it learns
    /// (<see cref="OpponentSets"/>).
    /// </summary>
    public bool BanditLearning { get; set; } = true;

    /// <summary>Matches already finished by <c>--interleave</c>'s scheduling phase, keyed by <c>ArenaJob.MatchId</c>; the per-arm loop takes them from here.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, CompletedMatch> Completed { get; } = new(StringComparer.Ordinal);

    /// <summary>Set when a live LLM arm cannot run; every LLM-arm match is then skipped with this reason.</summary>
    public string? LlmSkipReason { get; set; }

    private readonly object gate = new();

    public void MarkLlmSkipped(string reason)
    {
        lock (gate) LlmSkipReason ??= reason;
    }

    /// <summary>
    /// A message client for an LLM arm: the fake with <c>--llm-fake</c>, an OpenAI-compatible client with
    /// <c>--llm-endpoint</c>, else the SDK client (env key or ant profile).
    /// </summary>
    public IMessageClient? CreateClient()
    {
        if (LlmFake) return new FakeMessageClient { RequestObserver = LlmRequestObserver };
        if (LlmSkipReason is not null) return null;
        if (LlmEndpoint is not null)
            return new OpenAiCompatibleMessageClient(new Uri(LlmEndpoint), LlmModel, Environment.GetEnvironmentVariable("BINDERY_BOT_LLM_API_KEY"));
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
/// <item><c>distilled</c>: <see cref="DistilledStrategist"/> over the run's dataset (the <c>llm</c> arm's decisions),
/// escalating out-of-distribution states to the Claude strategist (fake or live, behind the same latency).</item>
/// <item>Opponents <c>rush</c>, <c>turtle</c>, <c>tech</c>, <c>harass</c>, <c>balanced</c>: <see cref="PinnedPlaybookStrategist"/>
/// on the matching playbook for the side's faction.</item>
/// </list>
/// Every side's fallback is a <see cref="PlaybookSelector"/>. Claude strategists run behind a
/// <see cref="SimulatedLatencyStrategist"/> so their answers arrive after a game-time latency.
/// </summary>
public sealed class BotAgentFactory(IRulesDatabase rules, IPlaybookLibrary playbooks, ArenaRunContext context) : IArenaAgentFactory
{
    public const double FakeLatencySeconds = 4.0;

    /// <summary>The run's shared state (datasets, learners, LLM availability).</summary>
    public ArenaRunContext Context => context;

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

    /// <summary>
    /// One <c>llm</c> arm per vocabulary tier (build step 6), for the paired comparisons the adoption rule reads;
    /// <c>--arms tiers</c> runs all four. The plain <c>llm</c> arm uses the adopted tier.
    /// </summary>
    public static IReadOnlyDictionary<string, VocabularyTier> TierArms { get; } = new SortedDictionary<string, VocabularyTier>(StringComparer.Ordinal)
    {
        ["llm-t0"] = VocabularyTier.PlaybookOnly,
        ["llm-t1"] = VocabularyTier.Parameters,
        ["llm-t2"] = VocabularyTier.ObjectivesAndRegions,
        ["llm-t3"] = VocabularyTier.Full,
    };

    /// <summary>
    /// One <c>llm-shadow</c> arm per vocabulary tier: the selector plays and the LLM proposes at the tier in shadow, so a
    /// wider vocabulary can be measured (validity, latency, agreement) without it deciding a match.
    /// </summary>
    public static IReadOnlyDictionary<string, VocabularyTier> ShadowTierArms { get; } = new SortedDictionary<string, VocabularyTier>(StringComparer.Ordinal)
    {
        ["llm-shadow-t0"] = VocabularyTier.PlaybookOnly,
        ["llm-shadow-t1"] = VocabularyTier.Parameters,
        ["llm-shadow-t2"] = VocabularyTier.ObjectivesAndRegions,
        ["llm-shadow-t3"] = VocabularyTier.Full,
    };

    /// <summary>True for an arm name the arena can run (a <see cref="Arms"/>, <see cref="TierArms"/> or <see cref="ShadowTierArms"/> entry).</summary>
    public static bool IsArm(string name) => Arms.Contains(name) || TierArms.ContainsKey(name) || ShadowTierArms.ContainsKey(name) || PinnedPlaybookId(name) is not null;

    /// <summary>Prefix of the deterministic pinned arm, <c>pinned:&lt;playbookId&gt;</c>: tier 3 of the LLM-to-playbook pipeline.</summary>
    public const string PinnedPrefix = "pinned:";

    /// <summary>The playbook id of a <c>pinned:&lt;id&gt;</c> arm name, or null for any other name.</summary>
    public static string? PinnedPlaybookId(string name) =>
        name.StartsWith(PinnedPrefix, StringComparison.Ordinal) && name.Length > PinnedPrefix.Length ? name[PinnedPrefix.Length..] : null;

    /// <summary>
    /// Every opponent name <c>--opponents all</c> expands to: the independent scripted AI styles at hard difficulty
    /// first, then the pinned-playbook styles (which run the bot's own planner and are kept for comparison).
    /// </summary>
    public static IReadOnlyList<string> AllOpponents { get; } = [.. OpponentProfiles.Styles, .. OpponentStyles.Keys];

    /// <summary>Prefix that runs a pinned style on the live stack instead of the frozen baseline (<c>live-rush</c>).</summary>
    public const string LivePrefix = "live-";

    /// <summary>
    /// True for a pinned-playbook style (frozen baseline stack), a <c>live-</c> pinned style (live stack), or an
    /// independent <c>ai-*</c> style (with optional <c>:difficulty</c>).
    /// </summary>
    public static bool IsOpponent(string name) =>
        OpponentStyles.ContainsKey(name)
        || (name.StartsWith(LivePrefix, StringComparison.Ordinal) && OpponentStyles.ContainsKey(name[LivePrefix.Length..]))
        || OpponentProfiles.TryParse(name, out _, out _);

    private static readonly Baseline.Playbooks.PlaybookLibrary BaselinePlaybooks = Baseline.Playbooks.PlaybookLibrary.LoadDefault();

    public IArenaAgent Create(ArmSpec arm, PlayerId player, Faction faction, MapInfo map, int seed) => Build(arm, player, faction, map, seed, null);

    /// <summary>
    /// The arm exactly as <see cref="Create"/> builds it (options, fallback, controllers), with its primary and shadow
    /// strategists replaced by <see cref="ReplayStrategist"/>s over a recorded decision log, which also echo the
    /// strategist-authored records back into the new log. The replay strategists are returned for their counters.
    /// </summary>
    public BotArenaAgent CreateReplay(ArmSpec arm, PlayerId player, Faction faction, MapInfo map, int seed, IReadOnlyList<DecisionRecord> recorded, out ReplayStrategist primary, out ReplayStrategist? shadow)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ReplayStrategist? p = null, s = null;
        BotArenaAgent agent = (BotArenaAgent)Build(arm, player, faction, map, seed, (log, hasShadow) =>
        {
            p = new ReplayStrategist(recorded, ProposalRole.Primary, echoLog: log);
            s = hasShadow ? new ReplayStrategist(recorded, ProposalRole.Shadow, echoLog: log) : null;
            return (p, s);
        });
        primary = p!;
        shadow = s;
        return agent;
    }

    private IArenaAgent Build(ArmSpec arm, PlayerId player, Faction faction, MapInfo map, int seed, Func<DecisionLog, bool, (IStrategist Primary, IStrategist? Shadow)>? replay)
    {
        ArgumentNullException.ThrowIfNull(arm);
        if (OpponentProfiles.TryParse(arm.Name, out string aiStyle, out OpponentDifficulty difficulty))
        {
            return new ScriptedArenaAgent(new ScriptedSkirmishAi(rules, player, faction, map, aiStyle, difficulty, seed), $"scripted:{aiStyle}:{difficulty.ToString().ToLowerInvariant()}");
        }
        if (OpponentStyles.TryGetValue(arm.Name, out IReadOnlyDictionary<Faction, string>? frozenStyle))
        {
            // Pinned styles are the stationary benchmark: the frozen baseline stack, not the live one.
            Baseline.Runtime.DecisionLog baselineLog = new();
            Baseline.Runtime.BotRuntime baseline = Baseline.Runtime.StandardBot.Create(
                rules, BaselinePlaybooks, new Baseline.Strategy.PinnedPlaybookStrategist(frozenStyle, $"style-{arm.Name}"),
                new Baseline.Strategy.PlaybookSelector(id: "selector-fallback"), null, baselineLog, Baseline.Runtime.StandardBot.SimulatorOptions);
            return new BaselineArenaAgent(baseline, baselineLog, $"pinned:{arm.Name}:baseline-7f3e2c7");
        }
        string name = arm.Name.StartsWith(LivePrefix, StringComparison.Ordinal) && OpponentStyles.ContainsKey(arm.Name[LivePrefix.Length..])
            ? arm.Name[LivePrefix.Length..]
            : arm.Name;
        DecisionLog log = new();
        List<string> labels = [];
        if (arm.Oracle) labels.Add("oracle");
        BotOptions options = StandardBot.SimulatorOptions;
        if (arm.Personality is { } personality)
        {
            // The style reaches every strategist through the context (deterministic biases and LLM guidance alike).
            options = options with { Personality = personality };
            labels.Add($"personality:{personality}");
        }
        IStrategist fallback = new PlaybookSelector(id: "selector-fallback");
        IStrategist? shadow = null;
        IStrategist primary;
        Action<bool?, double, double, BotArenaAgent>? onFinish = null;
        List<ClaudeStrategist> claude = [];

        if (OpponentStyles.TryGetValue(name, out IReadOnlyDictionary<Faction, string>? style))
        {
            primary = new PinnedPlaybookStrategist(style, $"style-{name}");
            labels.Add($"pinned:{name}:live");
        }
        else
        {
            switch (arm.Name)
            {
                case "selector":
                    primary = new PlaybookSelector();
                    break;
                case "bandit":
                    ContextualBanditStrategist bandit = context.BanditFor(arm);
                    primary = bandit;
                    bool learn = context.BanditLearning;
                    onFinish = (won, own, enemy, agent) =>
                    {
                        // The final active intent settles the match's last proposal: credited when it took effect.
                        if (learn) bandit.CompleteEpisode(Reward(won, own, enemy), agent.Runtime.ActiveIntent);
                        else bandit.AbandonEpisode();
                    };
                    break;
                case "llm-shadow":
                    primary = new PlaybookSelector();
                    shadow = Llm(StrategistMode.Strategic, claude, labels);
                    break;
                case "llm":
                    primary = Llm(StrategistMode.Strategic, claude, labels);
                    break;
                case var shadowArm when ShadowTierArms.TryGetValue(shadowArm, out VocabularyTier shadowTier):
                    primary = new PlaybookSelector();
                    shadow = Llm(StrategistMode.Strategic, claude, labels, shadowTier);
                    labels.Add($"shadow-vocabulary:{shadowTier}");
                    break;
                case var tierArm when TierArms.TryGetValue(tierArm, out VocabularyTier tier):
                    primary = Llm(StrategistMode.Strategic, claude, labels, tier);
                    labels.Add($"vocabulary:{tier}");
                    break;
                case var pinnedArm when PinnedPlaybookId(pinnedArm) is { } pinnedId:
                    // Always this playbook at its default parameters, renewed at the normal cadence; never the defend
                    // override of the opponent styles. A faction the playbook does not list gets no proposal, so the
                    // selector fallback plays that side.
                    if (!playbooks.TryGet(pinnedId, out Playbook pinnedPlaybook)) throw new ArgumentException($"Unknown playbook '{pinnedId}' in arm '{arm.Name}'.");
                    primary = new PinnedPlaybookStrategist(pinnedPlaybook.Factions.ToDictionary(static f => f, _ => pinnedId), $"pinned-{pinnedId}", double.PositiveInfinity);
                    labels.Add($"pinned:{pinnedId}");
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
                    // The proposal reserves the LLM for unusual states: escalations go to Claude, not to a rule set.
                    primary = new DistilledStrategist(dataset, Llm(StrategistMode.Strategic, claude, labels),
                        new DistilledOptions(Mode: arm.Oracle ? ObservationMode.Oracle : ObservationMode.Belief), id: BotArenaAgent.DistilledId);
                    labels.Add($"distilled-from:{context.DistillSource ?? "none"} ({dataset.Count} examples)");
                    break;
                default:
                    throw new ArgumentException($"Unknown arm or opponent '{arm.Name}'. Arms: {string.Join(", ", Arms)}; opponents: {string.Join(", ", AllOpponents)}.");
            }
        }

        if (replay is not null)
        {
            (primary, shadow) = replay(log, shadow is not null);
            claude.Clear();
            onFinish = null;
            labels.Add("replay");
        }
        OperationalOptions? operations = null;
        FeatureOptions? features = null;
        if (!OpponentStyles.ContainsKey(name) && context.ArmKnobs.Count > 0)
        {
            operations = Tuning.TunedParameterSet.Active.ApplyTo(new OperationalOptions());
            features = Tuning.TunedParameterSet.Active.ApplyTo(new FeatureOptions());
            foreach ((string knob, double value) in context.ArmKnobs.OrderBy(static k => k.Key, StringComparer.Ordinal))
            {
                if (Tuning.TuningKnobs.IsOperational(knob)) operations = Tuning.TuningKnobs.Set(operations, knob, value);
                else features = Tuning.TuningKnobs.Set(features, knob, value);
                labels.Add(string.Create(CultureInfo.InvariantCulture, $"knob:{knob}={value}"));
            }
        }
        BotRuntime runtime = StandardBot.Create(rules, playbooks, primary, fallback, shadow, log, options, operations, features);
        foreach (ClaudeStrategist strategist in claude)
        {
            strategist.ProposalFailed += (_, failure) =>
            {
                // The record goes into the hashed decision log: under a fixed simulated latency it carries that
                // latency, not the strategist's wall-clock stopwatch, or the same match would hash differently on
                // every run (successful proposals get the same treatment from SimulatedLatencyStrategist).
                if (SimulatedLatencySeconds is { } simulated) failure = failure with { Cost = failure.Cost with { LatencySeconds = simulated } };
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

    private IStrategist Llm(StrategistMode mode, List<ClaudeStrategist> claude, List<string> labels, VocabularyTier? tier = null)
    {
        Label(labels);
        return Latency(Unwrapped(mode, claude, tier));
    }

    private IStrategist Unwrapped(StrategistMode mode, List<ClaudeStrategist> claude, VocabularyTier? tier = null)
    {
        IMessageClient client = context.CreateClient() ?? new UnavailableClient(context.LlmSkipReason ?? "skipped: no credential");
        ClaudeStrategistOptions options = mode == StrategistMode.Refine ? ClaudeStrategistOptions.ForRefine() : new ClaudeStrategistOptions();
        if (tier is { } t) options = options with { Vocabulary = t };
        if (context.ExtendedMetrics) options = options with { ExtendedConditionMetrics = true };
        // An OpenAI-compatible endpoint serves one configured model for both roles; the arm reports that model, not
        // the Claude default, so no summary can credit a local model's play to Claude.
        if (context.LlmEndpoint is not null && !context.LlmFake) options = options with { Model = context.LlmModel };
        ClaudeStrategist strategist = new(client, options);
        claude.Add(strategist);
        return strategist;
    }

    private IStrategist Latency(IStrategist inner) => new SimulatedLatencyStrategist(inner, SimulatedLatencySeconds);

    /// <summary>The fixed game-time latency LLM answers get (<c>--llm-latency</c>, or the fake's default); null for measured latency.</summary>
    private double? SimulatedLatencySeconds => context.LlmLatencySeconds ?? (context.LlmFake ? FakeLatencySeconds : null);

    private void Label(List<string> labels)
    {
        if (context.LlmFake && !labels.Contains("llm-fake")) labels.Add("llm-fake");
        // Results from a non-Anthropic model must say so wherever an arm's name appears.
        if (context.LlmEndpoint is not null && !labels.Contains($"llm-openai:{context.LlmModel}")) labels.Add($"llm-openai:{context.LlmModel}");
    }

    /// <summary>Stands in when no credential resolved: every call fails as unauthorized.</summary>
    private sealed class UnavailableClient(string reason) : IMessageClient
    {
        public Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken cancellationToken) =>
            Task.FromException<ModelReply>(new ModelClientException(ModelFailureKind.Unauthorized, reason));
    }
}
