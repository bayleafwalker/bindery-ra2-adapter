// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Features;
using Bindery.Ra2.Bot.Operations;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Runtime;
using Bindery.Ra2.Bot.Strategy;
using Bindery.Ra2.Bot.Tuning;

namespace Bindery.Ra2.Bot.Tune;

/// <summary>
/// One parameterisation of the live selector bot: a playbook library and the planner/feature options it runs with.
/// The tuner's candidates, the untuned baseline and the self-play opponents are all variants.
/// </summary>
public sealed record BotVariant(string Label, IPlaybookLibrary Playbooks, OperationalOptions Operational, FeatureOptions Features)
{
    /// <summary>The authored playbooks and option defaults, ignoring any embedded tuned set.</summary>
    public static BotVariant Authored { get; } = new("authored", PlaybookLibrary.LoadAuthored(), new OperationalOptions(), new FeatureOptions());

    /// <summary>The authored defaults with <paramref name="set"/> applied (whether or not it is marked adopted).</summary>
    public static BotVariant From(string label, TunedParameterSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return new BotVariant(label, new PlaybookLibrary(set.ApplyTo(PlaybookLibrary.LoadAuthored().All)), set.ApplyTo(new OperationalOptions()), set.ApplyTo(new FeatureOptions()));
    }

    /// <summary>A selector-driven bot for one side of a match, built exactly like the arena's <c>selector</c> arm.</summary>
    public IArenaAgent CreateAgent(IRulesDatabase rules)
    {
        DecisionLog log = new();
        BotRuntime runtime = StandardBot.Create(
            rules, Playbooks, new PlaybookSelector(), new PlaybookSelector(id: "selector-fallback"), null, log,
            StandardBot.SimulatorOptions, Operational, Features);
        return new BotArenaAgent(runtime, log, [$"variant:{Label}"], null);
    }
}

/// <summary>
/// Arena agent factory for tuning matches: the arm <see cref="CandidateArm"/> and the self-play opponents
/// <c>bot:default</c> / <c>bot:champion</c> are <see cref="BotVariant"/>s; every other opponent name
/// (<c>ai-*</c>, pinned styles, <c>live-*</c>) is delegated to the arena's own factory.
/// </summary>
public sealed class VariantAgentFactory(IRulesDatabase rules, BotAgentFactory arena, BotVariant candidate, BotVariant champion) : IArenaAgentFactory
{
    public const string CandidateArm = "candidate";
    public const string DefaultBot = "bot:default";
    public const string ChampionBot = "bot:champion";

    public static IReadOnlyList<string> SelfPlayOpponents { get; } = [DefaultBot, ChampionBot];

    public static bool IsOpponent(string name) => SelfPlayOpponents.Contains(name) || BotAgentFactory.IsOpponent(name);

    public IArenaAgent Create(ArmSpec arm, PlayerId player, Faction faction, MapInfo map, int seed)
    {
        ArgumentNullException.ThrowIfNull(arm);
        return arm.Name switch
        {
            CandidateArm => candidate.CreateAgent(rules),
            DefaultBot => BotVariant.Authored.CreateAgent(rules),
            ChampionBot => champion.CreateAgent(rules),
            _ => arena.Create(arm, player, faction, map, seed),
        };
    }
}
