// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Baseline.Runtime;

/// <summary>
/// Decision-record kinds the runtime writes in addition to
/// <see cref="DecisionRecordKinds"/>. They make decision lateness and contained
/// component failures measurable from the log alone.
/// </summary>
public static class RuntimeRecordKinds
{
    /// <summary>A strategist request was started (role, strategist, trigger).</summary>
    public const string Request = "strategy.request";

    /// <summary>The operational planner threw; the previous plan stays in force.</summary>
    public const string PlanFailed = "operations.failed";

    /// <summary>A tactical controller threw; its commands for the frame are empty.</summary>
    public const string TacticsFailed = "tactics.failed";
}

/// <summary>
/// Optional capability for strategists whose answers depend on game time rather
/// than on wall-clock completion, chiefly <see cref="ReplayStrategist"/>. The
/// scheduler calls <see cref="OnFrame"/> at the start of each tick, before polling,
/// so a replayed proposal completes on exactly the frame it was recorded on.
/// Such strategists are never waited on inline.
/// </summary>
public interface IFrameAwareStrategist
{
    void OnFrame(GameTime now);
}
