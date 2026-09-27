// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Baseline.Playbooks;

/// <summary>
/// The 12 authored playbooks named in the strategic-bot spec: five Allied, five
/// Soviet, two faction-generic. Every tech goal here names a type id present in
/// the committed approximate fixture (<c>Data/bindery-sim-approx.json</c>) and
/// reachable via <see cref="IRulesDatabase.PathTo"/> for the playbook's own
/// factions — a fixture-backed test enforces that so the two files cannot
/// silently drift apart.
/// </summary>
internal static class DefaultPlaybooks
{
    // A method, not a field initializer: field initializers run in textual
    // declaration order, and this list must be built after the individual
    // playbook properties below it are assigned, not before.
    public static IReadOnlyList<Playbook> All => new List<Playbook>
    {
        AlliedBoom,
        AlliedGrizzlyTiming,
        AlliedIfvMix,
        AlliedPrismTurtle,
        AlliedHarass,
        SovietRhinoRush,
        SovietFlakMix,
        SovietV3Siege,
        SovietApocTech,
        SovietTurtle,
        GenericDefend,
        GenericExpand,
    };

    private static readonly IReadOnlyList<Faction> AlliedOnly = [Faction.Allied];
    private static readonly IReadOnlyList<Faction> SovietOnly = [Faction.Soviet];
    private static readonly IReadOnlyList<Faction> AlliedAndSoviet = [Faction.Allied, Faction.Soviet];

    private static Playbook AlliedBoom { get; } = new(
        Id: "allied-boom",
        Description: "Economy-first opening: stack refineries and harvesters before committing to a large army, banking on Allied cash efficiency to out-produce the enemy later.",
        Factions: AlliedOnly,
        Posture: StrategicPosture.Boom,
        Budget: new BudgetShares(Economy: 0.5, Army: 0.2, Tech: 0.2, Defense: 0.1),
        Composition:
        [
            new CompositionTarget(UnitRole.AntiArmor, 0.3, 0.6),
            new CompositionTarget(UnitRole.AntiInfantry, 0.1, 0.3),
            new CompositionTarget(UnitRole.AntiAir, 0.0, 0.2),
        ],
        TechGoals: ["GAREFN", "GAWEAP"],
        AttackConditions: [new Condition(ConditionMetric.ArmyValueRatio, Comparison.Ge, 1.3)],
        AbortTriggers: [new Condition(ConditionMetric.BaseThreatRatio, Comparison.Ge, 1.5)],
        Parameters: [new PlaybookParameter("expandAtSeconds", 60, 240, 120, "Game time before queuing the next refinery/expansion.")],
        MinCommitSeconds: 60);

    private static Playbook AlliedGrizzlyTiming { get; } = new(
        Id: "allied-grizzly-timing",
        Description: "A single timed push of Grizzly tanks as soon as the war factory is up, before the enemy's own armor tech matures.",
        Factions: AlliedOnly,
        Posture: StrategicPosture.Pressure,
        Budget: new BudgetShares(Economy: 0.3, Army: 0.5, Tech: 0.1, Defense: 0.1),
        Composition: [new CompositionTarget(UnitRole.AntiArmor, 0.5, 0.8)],
        TechGoals: ["GAWEAP", "MTNK"],
        AttackConditions: [new Condition(ConditionMetric.OwnArmyValue, Comparison.Ge, 1500)],
        AbortTriggers: [new Condition(ConditionMetric.BaseThreatRatio, Comparison.Ge, 1.5)],
        Parameters: [new PlaybookParameter("attackArmyValue", 800, 3000, 1500, "Own army value that triggers the timing push.")],
        MinCommitSeconds: 45);

    private static Playbook AlliedIfvMix { get; } = new(
        Id: "allied-ifv-mix",
        Description: "Cheap IFV-heavy composition that trades on chassis flexibility, backed by a modest harvester count to keep the queue fed.",
        Factions: AlliedOnly,
        Posture: StrategicPosture.Pressure,
        Budget: new BudgetShares(Economy: 0.35, Army: 0.45, Tech: 0.1, Defense: 0.1),
        Composition:
        [
            new CompositionTarget(UnitRole.AntiInfantry, 0.3, 0.6),
            new CompositionTarget(UnitRole.AntiArmor, 0.2, 0.4),
        ],
        TechGoals: ["GAWEAP", "FV"],
        AttackConditions: [new Condition(ConditionMetric.OwnArmyValue, Comparison.Ge, 1200)],
        AbortTriggers: [new Condition(ConditionMetric.BaseThreatRatio, Comparison.Ge, 1.5)],
        Parameters: [new PlaybookParameter("harvesterTarget", 2, 6, 3, "Harvester count to reach before committing to the push.")],
        MinCommitSeconds: 40);

    private static Playbook AlliedPrismTurtle { get; } = new(
        Id: "allied-prism-turtle",
        Description: "Defensive tech rush to Prism Tanks and Prism Towers, trading map presence for a hard-to-crack base and a late-game power spike.",
        Factions: AlliedOnly,
        Posture: StrategicPosture.Turtle,
        Budget: new BudgetShares(Economy: 0.3, Army: 0.3, Tech: 0.2, Defense: 0.2),
        Composition:
        [
            new CompositionTarget(UnitRole.Defense, 0.2, 0.5),
            new CompositionTarget(UnitRole.AntiArmor, 0.2, 0.4),
        ],
        TechGoals: ["GATECH", "SREF", "GAPRIS"],
        AttackConditions: [new Condition(ConditionMetric.ArmyValueRatio, Comparison.Ge, 1.4)],
        AbortTriggers: [new Condition(ConditionMetric.BaseThreatRatio, Comparison.Ge, 1.5)],
        Parameters: [new PlaybookParameter("expandAtSeconds", 90, 300, 150, "Game time before the first expansion, kept late for a turtle opening.")],
        MinCommitSeconds: 90);

    private static Playbook AlliedHarass { get; } = new(
        Id: "allied-harass",
        Description: "Rocketeer-led harassment of enemy harvesters and unguarded regions while the main base develops behind it.",
        Factions: AlliedOnly,
        Posture: StrategicPosture.Harass,
        Budget: new BudgetShares(Economy: 0.4, Army: 0.4, Tech: 0.1, Defense: 0.1),
        Composition:
        [
            new CompositionTarget(UnitRole.AntiAir, 0.2, 0.5),
            new CompositionTarget(UnitRole.Scout, 0.0, 0.2),
        ],
        TechGoals: ["GAAIRC", "E3"],
        AttackConditions: [new Condition(ConditionMetric.EnemyArmyValueEstimate, Comparison.Lt, 1000)],
        AbortTriggers: [new Condition(ConditionMetric.LossesValue15s, Comparison.Ge, 500)],
        Parameters: [new PlaybookParameter("harassIntervalSeconds", 30, 120, 60, "Seconds between harassment sorties.")],
        MinCommitSeconds: 30);

    private static Playbook SovietRhinoRush { get; } = new(
        Id: "soviet-rhino-rush",
        Description: "Early Rhino Tank numbers spent aggressively before the enemy can out-tech them, the classic Soviet armor rush.",
        Factions: SovietOnly,
        Posture: StrategicPosture.Pressure,
        Budget: new BudgetShares(Economy: 0.25, Army: 0.6, Tech: 0.05, Defense: 0.1),
        Composition: [new CompositionTarget(UnitRole.AntiArmor, 0.6, 0.9)],
        TechGoals: ["NAWEAP", "HTNK"],
        AttackConditions: [new Condition(ConditionMetric.OwnArmyValue, Comparison.Ge, 1000)],
        AbortTriggers: [new Condition(ConditionMetric.BaseThreatRatio, Comparison.Ge, 1.5)],
        Parameters: [new PlaybookParameter("attackArmyValue", 600, 2000, 1000, "Own army value that triggers the rush.")],
        MinCommitSeconds: 35);

    private static Playbook SovietFlakMix { get; } = new(
        Id: "soviet-flak-mix",
        Description: "Flak Track and Rhino combined arms, built to answer Allied air harassment while still pressing on the ground.",
        Factions: SovietOnly,
        Posture: StrategicPosture.Pressure,
        Budget: new BudgetShares(Economy: 0.3, Army: 0.5, Tech: 0.1, Defense: 0.1),
        Composition:
        [
            new CompositionTarget(UnitRole.AntiAir, 0.2, 0.4),
            new CompositionTarget(UnitRole.AntiArmor, 0.3, 0.5),
        ],
        TechGoals: ["NAWEAP", "HTK"],
        AttackConditions: [new Condition(ConditionMetric.OwnArmyValue, Comparison.Ge, 1200)],
        AbortTriggers: [new Condition(ConditionMetric.BaseThreatRatio, Comparison.Ge, 1.5)],
        Parameters: [new PlaybookParameter("harvesterTarget", 2, 6, 3, "Harvester count to reach before committing to the push.")],
        MinCommitSeconds: 45);

    private static Playbook SovietV3Siege { get; } = new(
        Id: "soviet-v3-siege",
        Description: "V3 Launchers sieging from behind a Rhino screen, out-ranging static defenses instead of trading into them.",
        Factions: SovietOnly,
        Posture: StrategicPosture.Pressure,
        Budget: new BudgetShares(Economy: 0.3, Army: 0.45, Tech: 0.15, Defense: 0.1),
        Composition:
        [
            new CompositionTarget(UnitRole.Artillery, 0.2, 0.5),
            new CompositionTarget(UnitRole.AntiArmor, 0.2, 0.4),
        ],
        TechGoals: ["NARADR", "V3"],
        AttackConditions: [new Condition(ConditionMetric.ArmyValueRatio, Comparison.Ge, 1.2)],
        AbortTriggers: [new Condition(ConditionMetric.BaseThreatRatio, Comparison.Ge, 1.5)],
        Parameters: [new PlaybookParameter("siegeRangeBufferCells", 1, 5, 2, "Extra cells of standoff kept beyond enemy defensive range.")],
        MinCommitSeconds: 50);

    private static Playbook SovietApocTech { get; } = new(
        Id: "soviet-apoc-tech",
        Description: "Tech straight to Apocalypse Tanks, accepting a slower start for the strongest Soviet late-game armor.",
        Factions: SovietOnly,
        Posture: StrategicPosture.Tech,
        Budget: new BudgetShares(Economy: 0.3, Army: 0.3, Tech: 0.3, Defense: 0.1),
        Composition:
        [
            new CompositionTarget(UnitRole.AntiArmor, 0.4, 0.7),
            new CompositionTarget(UnitRole.AntiAir, 0.1, 0.3),
        ],
        TechGoals: ["NATECH", "APOC"],
        AttackConditions: [new Condition(ConditionMetric.OwnArmyValue, Comparison.Ge, 2500)],
        AbortTriggers: [new Condition(ConditionMetric.BaseThreatRatio, Comparison.Ge, 1.5)],
        Parameters: [new PlaybookParameter("expandAtSeconds", 60, 240, 150, "Game time before queuing the next refinery/expansion.")],
        MinCommitSeconds: 90);

    private static Playbook SovietTurtle { get; } = new(
        Id: "soviet-turtle",
        Description: "Tesla Coil and Flak Cannon rings around the base while the economy compounds behind them.",
        Factions: SovietOnly,
        Posture: StrategicPosture.Turtle,
        Budget: new BudgetShares(Economy: 0.3, Army: 0.3, Tech: 0.1, Defense: 0.3),
        Composition: [new CompositionTarget(UnitRole.Defense, 0.3, 0.6)],
        TechGoals: ["NATSLA", "NAFLAK"],
        AttackConditions: [new Condition(ConditionMetric.ArmyValueRatio, Comparison.Ge, 1.4)],
        AbortTriggers: [new Condition(ConditionMetric.BaseThreatRatio, Comparison.Ge, 1.5)],
        Parameters: [new PlaybookParameter("expandAtSeconds", 90, 300, 150, "Game time before the first expansion, kept late for a turtle opening.")],
        MinCommitSeconds: 90);

    private static Playbook GenericDefend { get; } = new(
        Id: "generic-defend",
        Description: "Faction-neutral fallback: hold the current footprint and answer threats, used when no more specific playbook fits.",
        Factions: AlliedAndSoviet,
        Posture: StrategicPosture.Defend,
        Budget: new BudgetShares(Economy: 0.4, Army: 0.3, Tech: 0.1, Defense: 0.2),
        Composition: [new CompositionTarget(UnitRole.Defense, 0.3, 0.6)],
        TechGoals: [],
        AttackConditions: [new Condition(ConditionMetric.BaseThreatRatio, Comparison.Ge, 1.3)],
        AbortTriggers: [new Condition(ConditionMetric.BaseThreatRatio, Comparison.Lt, 0.8)],
        Parameters: [new PlaybookParameter("defendThreatRatio", 1.0, 2.0, 1.3, "Base threat ratio that pulls the army home to defend.")],
        MinCommitSeconds: 30);

    private static Playbook GenericExpand { get; } = new(
        Id: "generic-expand",
        Description: "Faction-neutral fallback: take a second ore field as soon as it is safe, used when no more specific playbook fits.",
        Factions: AlliedAndSoviet,
        Posture: StrategicPosture.Expand,
        Budget: new BudgetShares(Economy: 0.6, Army: 0.2, Tech: 0.1, Defense: 0.1),
        Composition: [new CompositionTarget(UnitRole.Economy, 0.1, 0.3)],
        TechGoals: [],
        AttackConditions: [new Condition(ConditionMetric.HarvesterCount, Comparison.Ge, 4)],
        AbortTriggers: [new Condition(ConditionMetric.BaseThreatRatio, Comparison.Ge, 1.5)],
        Parameters: [new PlaybookParameter("expandAtSeconds", 30, 180, 90, "Game time before queuing the expansion refinery.")],
        MinCommitSeconds: 60);
}
