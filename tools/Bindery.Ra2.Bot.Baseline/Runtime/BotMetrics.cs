// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Baseline.Runtime;

/// <summary>
/// Running counters for one bot instance. These are conveniences for live
/// display and quick assertions; the decision log remains the evidence every
/// arena metric is computed from. Counters are only mutated on the tick thread.
/// </summary>
public sealed class BotMetrics
{
    /// <summary>Frames processed by <see cref="BotRuntime.Tick"/>.</summary>
    public long Frames { get; internal set; }

    /// <summary>Strategist requests started (primary, fallback and shadow).</summary>
    public long Requests { get; internal set; }

    /// <summary>Non-shadow proposals received (a strategist returned an intent).</summary>
    public long Proposals { get; internal set; }

    /// <summary>Requests that ended without a proposal: exception, null, cancellation or timeout.</summary>
    public long ProposalsFailed { get; internal set; }

    /// <summary>Non-shadow proposals the validator rejected for reasons other than staleness.</summary>
    public long Rejected { get; internal set; }

    /// <summary>Proposals discarded as late (invariant 2).</summary>
    public long LateDiscarded { get; internal set; }

    /// <summary>Validated proposals the arbiter refused (commitment, hysteresis, ...).</summary>
    public long Refused { get; internal set; }

    /// <summary>Intent activations that replaced the playbook or posture (or started from none).</summary>
    public long Activations { get; internal set; }

    /// <summary>Accepted proposals that renewed the active playbook and posture.</summary>
    public long Renewals { get; internal set; }

    /// <summary>Activations whose posture differs from the previous intent's.</summary>
    public long PostureFlips { get; internal set; }

    /// <summary>Activations of intents proposed by the fallback strategist.</summary>
    public long FallbackActivations { get; internal set; }

    /// <summary>Activations of the playbook-default emergency intent (no strategist produced anything).</summary>
    public long EmergencyActivations { get; internal set; }

    /// <summary>Shadow proposals recorded (never applied).</summary>
    public long ShadowProposals { get; internal set; }

    /// <summary>Operational plans produced.</summary>
    public long Plans { get; internal set; }

    /// <summary>Commands that passed the command gate.</summary>
    public long CommandsPassed { get; internal set; }

    /// <summary>Commands dropped by the command gate (invariant 4).</summary>
    public long CommandsDropped { get; internal set; }

    /// <summary>Planner or tactical controller calls that threw and were contained.</summary>
    public long ComponentFailures { get; internal set; }
}
