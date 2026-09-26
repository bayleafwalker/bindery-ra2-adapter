// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Features;
using Bindery.Ra2.Bot.Operations;

namespace Bindery.Ra2.Bot.Tuning;

/// <summary>
/// One option field exposed to evolutionary tuning, with the range the search may explore.
/// Ranges are deliberately wider than "sensible" so the search can find out, but narrow enough that
/// every value in them still produces a working bot (no zero cadences, no negative counts).
/// </summary>
/// <param name="Name">The <see cref="OperationalOptions"/> or <see cref="FeatureOptions"/> property name, verbatim.</param>
/// <param name="Integer">True when the property is an <c>int</c>: decoded values are rounded half away from zero.</param>
public sealed record OptionKnob(string Name, double Min, double Max, bool Integer, string Description);

/// <summary>
/// The option knobs the tuner may change, and the only code that reads or writes them by name.
/// Only knobs with a consumer in the planner or feature compiler are listed: tuning a field nothing reads
/// would let the search drift it at random and publish the drift as a "tuned" value.
/// </summary>
/// <remarks>
/// Deliberately excluded: <c>ReinforceSquadTargetSize</c> (declared, read by nothing), controller ids, lease
/// timings and placement search geometry (mechanics, not strategy), and the feature history/event windows
/// the spec fixes (15 s swing window, 60 s scouting window).
/// </remarks>
public static class TuningKnobs
{
    /// <summary>
    /// Playbook parameters the <see cref="OperationalPlanner"/> reads from an intent (the squad controller reads
    /// <c>siegeRangeBufferCells</c> through <see cref="SquadOrder.StandoffBufferCells"/>). A playbook parameter not
    /// in this list would change no behaviour, so the tuner would leave it at its authored default; a test requires
    /// every declared parameter to be listed here.
    /// </summary>
    public static IReadOnlyList<string> ConsumedPlaybookParameters { get; } =
        ["attackArmyValue", "defendThreatRatio", "expandAtSeconds", "harassIntervalSeconds", "harvesterTarget", "retreatBelowForceRatio", "siegeRangeBufferCells"];

    public static IReadOnlyList<OptionKnob> Operational { get; } =
    [
        new("MinAttackArmyValue", 600, 3000, false, "Army value an attack waits for when the playbook has no attackArmyValue (most playbooks)."),
        new("AttackHoldFraction", 0.2, 0.9, false, "A running attack continues while army value stays above this fraction of the trigger."),
        new("DefaultRetreatBelowForceRatio", 0.3, 1.0, false, "Local force ratio below which a squad retreats."),
        new("DefaultHarvesterTargetPerRefinery", 1, 4, false, "Harvesters per refinery when the playbook has no harvesterTarget."),
        new("DefaultExpandAtSeconds", 60, 300, false, "Second-refinery time when the playbook has no expandAtSeconds."),
        new("PowerBuffer", 0, 200, true, "Power surplus below which an idle build queue adds a power plant."),
        new("ExtraProductionCredits", 800, 5000, true, "Credits on hand before an extra production building is considered."),
        new("MaxProductionBuildings", 1, 8, true, "Cap on production buildings added when rich."),
        new("MaxRefineries", 1, 6, true, "Refinery cap (also capped by ore regions)."),
        new("MaxDefenses", 0, 12, true, "Static defense cap for defensive budgets."),
        new("HarassSquadSize", 2, 6, true, "Units in a harass squad."),
        new("ScoutRevisitSeconds", 20, 180, false, "A start location seen this recently is not re-scouted first."),
    ];

    public static IReadOnlyList<OptionKnob> Features { get; } =
    [
        new("ThreatSearchCells", 20, 120, false, "Travel distance within which an enemy contact counts toward a region's threat."),
        new("SlowestTypicalSpeed", 2, 8, false, "Cells/second used for every ETA and reinforcement estimate."),
        new("ArmyValueSwingThreshold", 0.1, 0.5, false, "Fractional army-value loss over 15 s that raises an army-value-swing event (a replan trigger)."),
    ];

    /// <summary>Reads a knob's value from <paramref name="options"/>.</summary>
    public static double Get(OperationalOptions options, string name)
    {
        ArgumentNullException.ThrowIfNull(options);
        return name switch
        {
            "MinAttackArmyValue" => options.MinAttackArmyValue,
            "AttackHoldFraction" => options.AttackHoldFraction,
            "DefaultRetreatBelowForceRatio" => options.DefaultRetreatBelowForceRatio,
            "DefaultHarvesterTargetPerRefinery" => options.DefaultHarvesterTargetPerRefinery,
            "DefaultExpandAtSeconds" => options.DefaultExpandAtSeconds,
            "PowerBuffer" => options.PowerBuffer,
            "ExtraProductionCredits" => options.ExtraProductionCredits,
            "MaxProductionBuildings" => options.MaxProductionBuildings,
            "MaxRefineries" => options.MaxRefineries,
            "MaxDefenses" => options.MaxDefenses,
            "HarassSquadSize" => options.HarassSquadSize,
            "ScoutRevisitSeconds" => options.ScoutRevisitSeconds,
            _ => throw new InvalidDataException($"Unknown operational knob '{name}'."),
        };
    }

    /// <summary>Returns <paramref name="options"/> with one knob set; integer knobs are rounded half away from zero.</summary>
    public static OperationalOptions Set(OperationalOptions options, string name, double value)
    {
        ArgumentNullException.ThrowIfNull(options);
        int i = (int)Math.Round(value, MidpointRounding.AwayFromZero);
        return name switch
        {
            "MinAttackArmyValue" => options with { MinAttackArmyValue = value },
            "AttackHoldFraction" => options with { AttackHoldFraction = value },
            "DefaultRetreatBelowForceRatio" => options with { DefaultRetreatBelowForceRatio = value },
            "DefaultHarvesterTargetPerRefinery" => options with { DefaultHarvesterTargetPerRefinery = value },
            "DefaultExpandAtSeconds" => options with { DefaultExpandAtSeconds = value },
            "PowerBuffer" => options with { PowerBuffer = i },
            "ExtraProductionCredits" => options with { ExtraProductionCredits = i },
            "MaxProductionBuildings" => options with { MaxProductionBuildings = i },
            "MaxRefineries" => options with { MaxRefineries = i },
            "MaxDefenses" => options with { MaxDefenses = i },
            "HarassSquadSize" => options with { HarassSquadSize = i },
            "ScoutRevisitSeconds" => options with { ScoutRevisitSeconds = value },
            _ => throw new InvalidDataException($"Unknown operational knob '{name}'."),
        };
    }

    /// <summary>Reads a knob's value from <paramref name="options"/>.</summary>
    public static double Get(FeatureOptions options, string name)
    {
        ArgumentNullException.ThrowIfNull(options);
        return name switch
        {
            "ThreatSearchCells" => options.ThreatSearchCells,
            "SlowestTypicalSpeed" => options.SlowestTypicalSpeed,
            "ArmyValueSwingThreshold" => options.ArmyValueSwingThreshold,
            _ => throw new InvalidDataException($"Unknown feature knob '{name}'."),
        };
    }

    /// <summary>Returns <paramref name="options"/> with one knob set.</summary>
    public static FeatureOptions Set(FeatureOptions options, string name, double value)
    {
        ArgumentNullException.ThrowIfNull(options);
        return name switch
        {
            "ThreatSearchCells" => options with { ThreatSearchCells = value },
            "SlowestTypicalSpeed" => options with { SlowestTypicalSpeed = value },
            "ArmyValueSwingThreshold" => options with { ArmyValueSwingThreshold = value },
            _ => throw new InvalidDataException($"Unknown feature knob '{name}'."),
        };
    }
}
