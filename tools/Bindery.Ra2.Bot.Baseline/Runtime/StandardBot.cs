// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Baseline.Arbitration;
using Bindery.Ra2.Bot.Baseline.Belief;
using Bindery.Ra2.Bot.Baseline.Features;
using Bindery.Ra2.Bot.Baseline.Operations;
using Bindery.Ra2.Bot.Baseline.Strategy;
using Bindery.Ra2.Bot.Baseline.Tactics;

namespace Bindery.Ra2.Bot.Baseline.Runtime;

/// <summary>
/// The composition root for the standard layered bot: belief model, feature
/// compiler, intent validator, operational planner and the four tactical
/// controllers, around whichever strategists the caller supplies. The arena, the
/// integration tests and a live bridge all build their bots here, so every arm
/// differs only in its strategists and options.
/// </summary>
public static class StandardBot
{
    /// <summary>Options for a simulator: deterministic strategists answer inline and tactics run at 5 Hz.</summary>
    public static BotOptions SimulatorOptions { get; } = new(
        RunDeterministicStrategistsInline: true,
        TacticalHz: 5,
        OperationsControllerId: "ops");

    /// <param name="primary">The configured strategist.</param>
    /// <param name="fallback">Deterministic fallback; a <see cref="PlaybookSelector"/> when null.</param>
    /// <param name="shadow">Optional shadow strategist (recorded, never applied).</param>
    /// <param name="log">Decision log; a new in-memory <see cref="DecisionLog"/> when null.</param>
    /// <param name="options">Runtime options; <see cref="SimulatorOptions"/> when null.</param>
    /// <param name="operations">Planner options; its controller id is forced to the runtime's operations controller id.</param>
    public static BotRuntime Create(
        IRulesDatabase rules,
        IPlaybookLibrary playbooks,
        IStrategist primary,
        IStrategist? fallback = null,
        IStrategist? shadow = null,
        IDecisionLog? log = null,
        BotOptions? options = null,
        OperationalOptions? operations = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(playbooks);
        ArgumentNullException.ThrowIfNull(primary);
        options ??= SimulatorOptions;
        operations = (operations ?? new OperationalOptions()) with { ControllerId = options.OperationsControllerId };

        BotComponents components = new(
            new BeliefModel(rules, new BeliefOptions()),
            new FeatureCompiler(rules, new FeatureOptions()),
            rules,
            playbooks,
            new IntentValidator(),
            new OperationalPlanner(rules, playbooks, operations),
            // Controllers that take unit leases by preemption (deploy, harvester safety, repair) run before the
            // squad controller, so a unit preempted this frame is not also commanded by its squad and dropped.
            [
                new DeployController(new DeployControllerOptions()),
                new HarvesterSafetyController(new HarvesterSafetyOptions()),
                new RepairController(new RepairControllerOptions()),
                new SquadController(new SquadControllerOptions()),
            ],
            primary,
            fallback ?? new PlaybookSelector(id: "selector-fallback"),
            shadow,
            log ?? new DecisionLog(),
            options);
        return new BotRuntime(components);
    }
}
