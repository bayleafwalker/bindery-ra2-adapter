// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Sim.Opponents;

namespace Bindery.Ra2.Bot.Arena;

/// <summary>
/// The arena's opponent split. Training opponents may inform anything: the selector's default playbook (chosen
/// against the pinned styles), bandit learning, distillation datasets and the tuner. Held-out opponents
/// (<see cref="HeldOut"/>) are for evaluation only: the arena never credits the bandit with a match against one,
/// never puts one in a distillation dataset or teacher run, and the tuner refuses them, so a win rate against them
/// is an out-of-sample result in opponent as well as in map.
/// </summary>
/// <remarks>
/// Every held-out opponent is an independent scripted AI style (<see cref="OpponentProfiles.HeldOutStyles"/>), not
/// the bot's own stack, and was calibrated to win some games: on the contested benchmark, all five maps, the
/// selector won 24 of 40 against <c>ai-horde</c> (8 seeds) and 11 of 20 against <c>ai-armor</c> (4 seeds), both
/// on 2026-09-26. The training <c>ai-*</c> styles and frozen pinned styles lose every contested game, and the
/// <c>live-*</c> styles run the bot's own planner, so neither can serve as held-out evidence.
/// </remarks>
public static class OpponentSets
{
    /// <summary>The held-out opponents, at their default (hard) difficulty.</summary>
    public static IReadOnlyList<string> HeldOut { get; } = OpponentProfiles.HeldOutStyles;

    /// <summary>
    /// Every training opponent: the training <c>ai-*</c> styles, the frozen pinned styles and the <c>live-*</c>
    /// pinned styles.
    /// </summary>
    public static IReadOnlyList<string> Training { get; } =
        [.. BotAgentFactory.AllOpponents, .. BotAgentFactory.OpponentStyles.Keys.Select(static k => BotAgentFactory.LivePrefix + k)];

    /// <summary>True for a held-out opponent at any difficulty (<c>ai-horde</c>, <c>ai-horde:easy</c>).</summary>
    public static bool IsHeldOut(string opponent)
    {
        ArgumentNullException.ThrowIfNull(opponent);
        return OpponentProfiles.TryParse(opponent, out string style, out _) && OpponentProfiles.HeldOutStyles.Contains(style);
    }

    /// <summary><c>heldout</c> for a held-out opponent, else <c>training</c> (the report's opponent split).</summary>
    public static string SplitOf(string opponent) => IsHeldOut(opponent) ? "heldout" : "training";

    /// <summary>
    /// Harvest income multiplier the opponent plays with on top of the benchmark (<see cref="OpponentProfiles.IncomeHandicap"/>
    /// for scripted styles, 1 otherwise).
    /// </summary>
    public static double IncomeHandicap(string opponent) =>
        OpponentProfiles.TryParse(opponent, out string style, out _) ? OpponentProfiles.IncomeHandicap(style) : 1.0;

    /// <summary>
    /// Throws when <paramref name="opponent"/> is held out: for callers that learn or tune from their matches
    /// (<paramref name="purpose"/> names them in the message).
    /// </summary>
    public static void EnsureTraining(string opponent, string purpose)
    {
        if (IsHeldOut(opponent))
        {
            throw new ArgumentException($"Opponent '{opponent}' is held out and is never used for {purpose}; held-out opponents: {string.Join(", ", HeldOut)}.");
        }
    }

    /// <summary>
    /// The opponents a distillation teacher plays: the run's training opponents, or, when the run named only
    /// held-out ones, the contested benchmark's training opponents.
    /// </summary>
    public static IReadOnlyList<string> TeacherOpponents(IReadOnlyList<string> runOpponents)
    {
        ArgumentNullException.ThrowIfNull(runOpponents);
        List<string> training = [.. runOpponents.Where(static o => !IsHeldOut(o))];
        return training.Count > 0 ? training : [.. BenchmarkSettings.Contested.DefaultOpponents!.Where(static o => !IsHeldOut(o))];
    }
}
