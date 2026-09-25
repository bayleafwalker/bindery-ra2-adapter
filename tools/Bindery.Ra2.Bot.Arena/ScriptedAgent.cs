// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Arena;

/// <summary>Behaviour a scripted, non-learning agent follows. Not a strategist: these exist so the arena runs before <c>BotRuntime</c> does.</summary>
public enum OpponentStyle { Placeholder, Rush, Turtle, Tech, Harass, Balanced }

/// <summary>
/// A trivial, hand-scripted agent: deploys its MCV, follows a fixed build
/// order (looping its last entry once reached), and — for anything but
/// <see cref="OpponentStyle.Placeholder"/> and <see cref="OpponentStyle.Turtle"/> —
/// mass-moves any idle combat units at the enemy start region once it has a
/// handful of them. It exists purely so <c>arena run</c> produces real
/// matches end-to-end; the integrator replaces it with a <c>BotRuntime</c>-backed
/// agent for the real arms once package C/E/F land.
/// </summary>
public sealed class ScriptedAgent : IArenaAgent
{
    private const int PlacementSearchRadius = 6;
    private const int RushSquadSize = 5;

    private readonly OpponentStyle style;
    private readonly ArenaRuleset rules;
    private readonly PlayerId self;
    private readonly IReadOnlyList<string> buildOrder;
    private int nextBuildIndex;
    private readonly HashSet<string> orderedBuildings = [];
    private int ticksSinceOrder;
    private RegionId? enemyStart;

    public ScriptedAgent(OpponentStyle style, ArenaRuleset rules, PlayerId self)
    {
        this.style = style;
        this.rules = rules;
        this.self = self;
        buildOrder = BuildOrderFor(style);
    }

    public ArenaAgentStats Stats { get; } = new();

    public IReadOnlyList<GameCommand> Tick(ObservationFrame frame)
    {
        List<GameCommand> commands = [];
        List<ObservedEntity> mine = frame.Entities.Where(e => e.Owner == self).ToList();

        DeployMcv(mine, commands);
        RequestPlacements(frame, mine, commands);
        AdvanceBuildOrder(frame, mine, commands);
        if (style is not (OpponentStyle.Placeholder or OpponentStyle.Turtle)) LaunchIdleCombatUnits(frame, mine, commands);

        ticksSinceOrder++;
        return commands;
    }

    private void DeployMcv(List<ObservedEntity> mine, List<GameCommand> commands)
    {
        foreach (ObservedEntity e in mine)
        {
            if (!e.Deployed && rules.TryGet(e.TypeId, out UnitRule rule) && rule.Role == UnitRole.Mcv)
            {
                commands.Add(new DeployCommand("scripted", e.Id));
            }
        }
    }

    private static void RequestPlacements(ObservationFrame frame, List<ObservedEntity> mine, List<GameCommand> commands)
    {
        foreach (ProductionQueueState queue in frame.Queues)
        {
            foreach (QueueItem item in queue.Items.Where(i => i.Ready))
            {
                ObservedEntity? anchor = mine.FirstOrDefault(e => e.TypeId is ArenaRuleset.ConstructionYard);
                anchor ??= mine.FirstOrDefault();
                if (anchor is null) continue;
                Cell cell = new(anchor.Position.X + PlacementSearchRadius, anchor.Position.Y);
                commands.Add(new PlaceBuildingCommand("scripted", item.TypeId, cell));
            }
        }
    }

    private void AdvanceBuildOrder(ObservationFrame frame, List<ObservedEntity> mine, List<GameCommand> commands)
    {
        if (nextBuildIndex >= buildOrder.Count || ticksSinceOrder < 15) return;
        string typeId = buildOrder[nextBuildIndex];
        if (!rules.TryGet(typeId, out UnitRule rule)) { nextBuildIndex++; return; }

        bool alreadyOwned = rule.Kind == EntityKind.Building && (mine.Any(e => e.TypeId == typeId) || orderedBuildings.Contains(typeId));
        if (alreadyOwned)
        {
            nextBuildIndex++;
            return;
        }

        HashSet<string> ownedBuildingTypes = mine.Where(e => rules.TryGet(e.TypeId, out UnitRule r) && r.Kind == EntityKind.Building)
                                                  .Select(e => e.TypeId).ToHashSet();
        if (!rules.CanBuild(frame.Faction, ownedBuildingTypes, typeId) || frame.Credits < rule.Cost) return;

        commands.Add(new ProduceCommand("scripted", typeId, rule.Queue));
        ticksSinceOrder = 0;
        if (rule.Kind == EntityKind.Building) orderedBuildings.Add(typeId);
        if (nextBuildIndex < buildOrder.Count - 1) nextBuildIndex++;
    }

    private void LaunchIdleCombatUnits(ObservationFrame frame, List<ObservedEntity> mine, List<GameCommand> commands)
    {
        List<ObservedEntity> combatUnits = mine.Where(e => rules.TryGet(e.TypeId, out UnitRule r) && r.Weapon != WeaponClass.None && r.Kind != EntityKind.Building && e.Target is null).ToList();
        if (combatUnits.Count < (style == OpponentStyle.Harass ? 2 : RushSquadSize)) return;
        RegionId? enemy = ResolveEnemyStart(frame, mine);
        Region? target = enemy is { } id ? frame.Map.Regions.FirstOrDefault(r => r.Id == id) : null;
        if (target is null) return;
        commands.Add(new AttackMoveCommand("scripted", combatUnits.Select(e => e.Id).ToList(), target.Center));
    }

    /// <summary>
    /// The agent is only given <see cref="ObservedEntity.Position"/>, not the
    /// engine's start-region assignment, so it infers the enemy's start the
    /// same way a human would: its own start is whichever start region its
    /// first unit sits in, and (on a 2-player map) the enemy's is the other one.
    /// </summary>
    private RegionId? ResolveEnemyStart(ObservationFrame frame, List<ObservedEntity> mine)
    {
        if (enemyStart is not null) return enemyStart;
        List<Region> starts = frame.Map.Regions.Where(r => r.IsStartLocation).ToList();
        ObservedEntity? anyMine = mine.FirstOrDefault();
        if (starts.Count < 2 || anyMine is null) return null;
        Region? myStart = frame.Map.RegionOf(anyMine.Position);
        Region? enemy = starts.FirstOrDefault(r => r.Id != myStart?.Id);
        if (enemy is not null) enemyStart = enemy.Id;
        return enemyStart;
    }

    private static IReadOnlyList<string> BuildOrderFor(OpponentStyle style) => style switch
    {
        OpponentStyle.Placeholder => [ArenaRuleset.PowerPlant, ArenaRuleset.Refinery],
        OpponentStyle.Rush => [ArenaRuleset.Barracks, ArenaRuleset.Rifleman, ArenaRuleset.Rifleman, ArenaRuleset.Rifleman, ArenaRuleset.Rifleman, ArenaRuleset.Rifleman, ArenaRuleset.Rifleman],
        OpponentStyle.Turtle => [ArenaRuleset.PowerPlant, ArenaRuleset.Refinery, ArenaRuleset.Harvester, ArenaRuleset.Barracks, ArenaRuleset.Pillbox, ArenaRuleset.Pillbox, ArenaRuleset.WarFactory, ArenaRuleset.Pillbox, ArenaRuleset.Tank, ArenaRuleset.Tank],
        OpponentStyle.Tech => [ArenaRuleset.PowerPlant, ArenaRuleset.Refinery, ArenaRuleset.Harvester, ArenaRuleset.Barracks, ArenaRuleset.PowerPlant, ArenaRuleset.WarFactory, ArenaRuleset.Tank, ArenaRuleset.FlakTrack, ArenaRuleset.Tank, ArenaRuleset.FlakTrack],
        OpponentStyle.Harass => [ArenaRuleset.Barracks, ArenaRuleset.Rifleman, ArenaRuleset.Rifleman, ArenaRuleset.PowerPlant, ArenaRuleset.Refinery, ArenaRuleset.Harvester, ArenaRuleset.Rifleman, ArenaRuleset.Rifleman],
        OpponentStyle.Balanced => [ArenaRuleset.PowerPlant, ArenaRuleset.Refinery, ArenaRuleset.Harvester, ArenaRuleset.Barracks, ArenaRuleset.Rifleman, ArenaRuleset.WarFactory, ArenaRuleset.Tank, ArenaRuleset.Tank],
        _ => [ArenaRuleset.PowerPlant, ArenaRuleset.Refinery],
    };
}
