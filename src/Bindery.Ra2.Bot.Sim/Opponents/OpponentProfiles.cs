// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Sim.Opponents;

/// <summary>
/// Script aggressiveness of a <see cref="ScriptedSkirmishAi"/>. Difficulty never changes build speed, credits or
/// vision (the AI plays fair under fog): it only changes how long the script pauses between build steps, how large
/// its attack waves are and how early it may launch the first one — the knobs retail RA2's AI difficulty turns
/// through its TeamTypes and TaskForces.
/// </summary>
public enum OpponentDifficulty { Easy, Medium, Hard }

/// <summary>One step of a faction build script: keep at least <paramref name="Count"/> of <paramref name="TypeId"/>.</summary>
public sealed record BuildStep(string TypeId, int Count);

/// <summary>One slot of a task force: this many units of this type.</summary>
public sealed record TaskForceSlot(string TypeId, int Count);

/// <summary>
/// A complete script for one style and faction, modelled on retail RA2's ai.ini: an ordered base build list, a
/// defence list, the task force every attack wave is built from, and wave timing.
/// </summary>
/// <param name="Build">Base structures in order; a step is satisfied when enough are owned or queued.</param>
/// <param name="Defenses">Defensive structures, built once the base list reaches <paramref name="DefensesAfterStep"/>.</param>
/// <param name="DefensesAfterStep">Index into <paramref name="Build"/> that must be satisfied before defences start.</param>
/// <param name="TaskForce">Units of the first wave at hard difficulty.</param>
/// <param name="WaveGrowth">Units added to the largest slot per launched wave (retail AI teams escalate).</param>
/// <param name="MaxWaveUnits">Cap on a wave's size.</param>
/// <param name="EarliestAttackSeconds">The first wave never launches before this game time at hard difficulty.</param>
/// <param name="HarvestersPerRefinery">Harvesters kept per refinery.</param>
/// <param name="Surplus">Extra structures built only while credits pile up (more factories, another refinery), as a
/// retail hard AI adds war factories when rich.</param>
public sealed record OpponentScript(
    IReadOnlyList<BuildStep> Build,
    IReadOnlyList<BuildStep> Defenses,
    int DefensesAfterStep,
    IReadOnlyList<TaskForceSlot> TaskForce,
    int WaveGrowth,
    int MaxWaveUnits,
    double EarliestAttackSeconds,
    int HarvestersPerRefinery,
    IReadOnlyList<BuildStep>? Surplus = null)
{
    /// <summary>Credits on hand above which the <see cref="Surplus"/> list is worked through.</summary>
    public const int SurplusCredits = 2500;
}

/// <summary>
/// The independent opponent styles (training <c>ai-rush</c>, <c>ai-balanced</c>, <c>ai-turtle</c>, <c>ai-air</c>; held-out <c>ai-horde</c>, <c>ai-armor</c>) per
/// faction, written against the fixture's type ids. They share no code or tables with the bot's playbooks: they
/// are deliberately a different author's view of how to play, so bot changes cannot move both sides.
/// </summary>
public static class OpponentProfiles
{
    /// <summary>The training styles: the arena's <c>--opponents all</c> and the tuner's default opponents.</summary>
    public static IReadOnlyList<string> Styles { get; } = ["ai-rush", "ai-balanced", "ai-turtle", "ai-air"];

    /// <summary>
    /// Styles written for evaluation only and never used to choose defaults, train the bandit, build distillation
    /// datasets or tune (the arena's held-out opponent set). <c>ai-horde</c> masses cheap infantry behind early
    /// defences from a barracks-first build and attacks late with large growing waves, the opposite of every
    /// training style's small early task force. <c>ai-armor</c> screens a second war factory with infantry and
    /// pillboxes and attacks with large tank waves, and carries an income handicap (<see cref="IncomeHandicap"/>).
    /// </summary>
    public static IReadOnlyList<string> HeldOutStyles { get; } = ["ai-horde", "ai-armor"];

    /// <summary>
    /// Multiplier on the harvest income of a style, whatever the benchmark: 1.5 for <c>ai-armor</c> (a retail-style
    /// AI economy bonus, which a tank-first script needs to survive the bot's infantry timing: without it the
    /// selector won 17 of 20 against it; with it 11 of 20, 2026-09-26, contested benchmark, all maps, 4 seeds),
    /// 1 for every other style.
    /// </summary>
    public static double IncomeHandicap(string style) => style == "ai-armor" ? 1.5 : 1.0;

    /// <summary>Parses <c>ai-style</c> or <c>ai-style:difficulty</c> (default hard); false for any other name.</summary>
    public static bool TryParse(string name, out string style, out OpponentDifficulty difficulty)
    {
        ArgumentNullException.ThrowIfNull(name);
        style = name;
        difficulty = OpponentDifficulty.Hard;
        int colon = name.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            style = name[..colon];
            string level = name[(colon + 1)..];
            switch (level)
            {
                case "easy": difficulty = OpponentDifficulty.Easy; break;
                case "medium": difficulty = OpponentDifficulty.Medium; break;
                case "hard": difficulty = OpponentDifficulty.Hard; break;
                default: return false;
            }
        }
        return Styles.Contains(style) || HeldOutStyles.Contains(style);
    }

    /// <summary>The script for a style and faction. Yuri has no fixture units and plays the Soviet script.</summary>
    public static OpponentScript For(string style, Faction faction)
    {
        OpponentScript script = Base(style, faction);
        return script with
        {
            Surplus = faction == Faction.Allied
                ? [new("GAWEAP", 2), new("GAREFN", 2), new("GAPILE", 2), new("GAWEAP", 3), new("GAREFN", 3), new("GAPILE", 3), new("GAWEAP", 4)]
                : [new("NAWEAP", 2), new("NAREFN", 2), new("NAHAND", 2), new("NAWEAP", 3), new("NAREFN", 3), new("NAHAND", 3), new("NAWEAP", 4)],
        };
    }

    private static OpponentScript Base(string style, Faction faction) => faction == Faction.Allied
        ? style switch
        {
            "ai-rush" => new(
                [new("GAPOWR", 1), new("GAPILE", 1), new("GAREFN", 1), new("GAWEAP", 1), new("GAPOWR", 2)],
                [new("PBOX", 1)], 4,
                [new("MTNK", 3), new("E1", 2)], 1, 10, 0, 1),
            "ai-armor" => new(
                [new("GAPOWR", 1), new("GAPILE", 1), new("GAREFN", 1), new("GAWEAP", 1), new("GAPOWR", 2), new("GAREFN", 2), new("GAWEAP", 2), new("GAPOWR", 3)],
                [new("PBOX", 2), new("GAPATR", 2)], 1,
                [new("MTNK", 6), new("FV", 2), new("E1", 8)], 2, 20, 300, 2),
            "ai-horde" => new(
                [new("GAPOWR", 1), new("GAPILE", 1), new("GAREFN", 1), new("GAWEAP", 1), new("GAPOWR", 2), new("GAREFN", 2), new("GAPOWR", 3)],
                [new("PBOX", 3), new("GAPATR", 1)], 3,
                [new("E1", 10), new("MTNK", 4)], 2, 24, 360, 2),
            "ai-turtle" => new(
                [new("GAPOWR", 1), new("GAPILE", 1), new("GAREFN", 1), new("GAWEAP", 1), new("GAPOWR", 2), new("GAREFN", 2), new("GAAIRC", 1), new("GAPOWR", 3), new("GATECH", 1), new("GAPOWR", 4), new("GAPOWR", 5)],
                [new("PBOX", 2), new("GAPATR", 1), new("GAPRIS", 3)], 4,
                [new("MTNK", 4), new("SREF", 2), new("E1", 2)], 1, 12, 420, 2),
            "ai-air" => new(
                [new("GAPOWR", 1), new("GAPILE", 1), new("GAREFN", 1), new("GAWEAP", 1), new("GAPOWR", 2), new("GAREFN", 2), new("GAAIRC", 1), new("GAPOWR", 3)],
                [new("PBOX", 1), new("GAPATR", 1)], 6,
                [new("HARR", 3), new("MTNK", 2)], 1, 10, 240, 2),
            _ => new(
                [new("GAPOWR", 1), new("GAPILE", 1), new("GAREFN", 1), new("GAWEAP", 1), new("GAPOWR", 2), new("GAREFN", 2), new("GAAIRC", 1), new("GAPOWR", 3)],
                [new("PBOX", 1), new("GAPATR", 1)], 6,
                [new("MTNK", 3), new("FV", 1), new("E1", 2)], 1, 12, 150, 2),
        }
        : style switch
        {
            "ai-rush" => new(
                [new("NAPOWR", 1), new("NAHAND", 1), new("NAREFN", 1), new("NAWEAP", 1), new("NAPOWR", 2)],
                [new("NASNGN", 1)], 4,
                [new("HTNK", 3), new("E2", 3)], 1, 10, 0, 1),
            "ai-armor" => new(
                [new("NAPOWR", 1), new("NAHAND", 1), new("NAREFN", 1), new("NAWEAP", 1), new("NAPOWR", 2), new("NAREFN", 2), new("NAWEAP", 2), new("NAPOWR", 3)],
                [new("NASNGN", 2), new("NAFLAK", 2)], 1,
                [new("HTNK", 6), new("HTK", 1), new("E2", 10)], 2, 20, 300, 2),
            "ai-horde" => new(
                [new("NAPOWR", 1), new("NAHAND", 1), new("NAREFN", 1), new("NAWEAP", 1), new("NAPOWR", 2), new("NAREFN", 2), new("NAPOWR", 3)],
                [new("NASNGN", 3), new("NAFLAK", 1)], 3,
                [new("E2", 12), new("HTNK", 4)], 2, 24, 360, 2),
            "ai-turtle" => new(
                [new("NAPOWR", 1), new("NAHAND", 1), new("NAREFN", 1), new("NAWEAP", 1), new("NAPOWR", 2), new("NAREFN", 2), new("NARADR", 1), new("NAPOWR", 3), new("NATECH", 1), new("NAPOWR", 4), new("NAPOWR", 5)],
                [new("NASNGN", 2), new("NAFLAK", 1), new("NATSLA", 3)], 4,
                [new("HTNK", 4), new("APOC", 1), new("E2", 3)], 1, 12, 420, 2),
            "ai-air" => new(
                [new("NAPOWR", 1), new("NAHAND", 1), new("NAREFN", 1), new("NAWEAP", 1), new("NAPOWR", 2), new("NAREFN", 2), new("NARADR", 1), new("NAPOWR", 3), new("NATECH", 1), new("NAPOWR", 4)],
                [new("NASNGN", 1), new("NAFLAK", 1)], 6,
                [new("KIROV", 1), new("HTNK", 3)], 1, 8, 300, 2),
            _ => new(
                [new("NAPOWR", 1), new("NAHAND", 1), new("NAREFN", 1), new("NAWEAP", 1), new("NAPOWR", 2), new("NAREFN", 2), new("NARADR", 1), new("NAPOWR", 3)],
                [new("NASNGN", 1), new("NAFLAK", 1)], 6,
                [new("HTNK", 3), new("HTK", 1), new("E2", 3)], 1, 12, 150, 2),
        };
}
